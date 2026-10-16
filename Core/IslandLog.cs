using System;
using System.IO;
using System.Text;

namespace alpha_island.Core
{
    public enum IslandLogLevel
    {
        Trace = 0,
        Debug = 1,
        Info = 2,
        Warn = 3,
        Error = 4,
    }

    /// <summary>
    /// 全应用统一日志出口。
    ///
    /// 替换原先散落的 App.Log(...) 与 System.Diagnostics.Debug.WriteLine(...)：
    ///   - 级别过滤（Trace/Debug/Info/Warn/Error），MinLevel 可运行时调整；
    ///   - 内存环形缓冲，供"关于"页与诊断导出直接读取，不必翻文件；
    ///   - 文件 sink 带轮转：error.log 超过 512KB 就 error.log → error.1.log → error.2.log，
    ///     最老的丢弃。旧实现是 File.AppendAllText 无限增长（实测被 SMTC 探针刷到 278KB 且永不回收）；
    ///   - 全程 try/catch 吞掉自身异常 —— 日志失败绝不能让应用挂掉。
    ///
    /// 线程安全：内存环形缓冲和入队在 Gate 下串行；后台单消费者批量写入并轮转，不在 UI 线程碰磁盘。
    /// </summary>
    public static class IslandLog
    {
        /// <summary>单个日志文件的大小上限，超过即轮转。</summary>
        public const long MaxFileBytes = 512 * 1024;

        /// <summary>保留的文件总数（error.log + error.1.log + error.2.log）。</summary>
        public const int MaxFiles = 3;

        private const int RingCapacity = 500;

        private static readonly object Gate = new();
        private static readonly LogEntry[] Ring = new LogEntry[RingCapacity];

        private static int _ringCount;
        private static int _ringNext;
        private static long _sequence;
        private static readonly Lazy<RollingFileLogSink> FileSink = new(() => new(AppPaths.ErrorLogFile, MaxFileBytes, MaxFiles));
        public static bool Flush(TimeSpan timeout) => !FileSink.IsValueCreated || FileSink.Value.FlushAsync(timeout).GetAwaiter().GetResult();
        public static string FileDiagnostics => !FileSink.IsValueCreated ? "文件日志尚未启动"
            : $"后台写入；排队 {FileSink.Value.Pending}；拥塞丢弃 {FileSink.Value.Dropped}；写入失败 {FileSink.Value.WriteFailures}";

        /// <summary>低于该级别的日志直接丢弃。</summary>
        public static IslandLogLevel MinLevel { get; set; } =
#if DEBUG
            IslandLogLevel.Debug;
#else
            IslandLogLevel.Info;
#endif
        public static bool IsEnabled(IslandLogLevel level) => level >= MinLevel;

        /// <summary>是否写文件。设 false 只留内存缓冲（诊断导出仍可用）。</summary>
        public static bool FileSinkEnabled { get; set; } = true;

        public readonly record struct LogEntry(
            long Seq, DateTime Time, IslandLogLevel Level, string Source, string Message)
        {
            public override string ToString() =>
                $"[{Time:yyyy-MM-dd HH:mm:ss.fff}] [{Level.ToString().ToUpperInvariant()}] ({Source}) {Message}";
        }

        // ---------- 写入入口 ----------

        public static void Trace(string source, string message) => Write(IslandLogLevel.Trace, source, message);
        public static void Debug(string source, string message) => Write(IslandLogLevel.Debug, source, message);
        public static void Debug(string source, ref DebugLogInterpolatedStringHandler message)
        {
            if (message.Enabled) Write(IslandLogLevel.Debug, source, message.GetText());
        }
        public static void Info(string source, string message) => Write(IslandLogLevel.Info, source, message);
        public static void Warn(string source, string message) => Write(IslandLogLevel.Warn, source, message);

        /// <summary>记一条错误。异常为 null 时静默返回（保持旧 App.Log 的语义）。</summary>
        public static void Error(string source, Exception? ex)
        {
            if (ex == null) return;
            Write(IslandLogLevel.Error, source, ex.ToString());
        }

        public static void Write(IslandLogLevel level, string source, string message)
        {
            if (level < MinLevel) return;

            try
            {
                LogEntry entry;
                lock (Gate)
                {
                    entry = new LogEntry(++_sequence, DateTime.Now, level, source ?? "?", message ?? string.Empty);
                    AddToRing(entry);
                    if (FileSinkEnabled) AppendToFile(entry);
                }
            }
            catch
            {
                // 日志失败不能再抛 —— 与旧 App.Log 的约定一致。
            }
        }

        // ---------- 读取 ----------

        /// <summary>按时间正序返回最近最多 500 条。</summary>
        public static LogEntry[] Snapshot()
        {
            lock (Gate)
            {
                var result = new LogEntry[_ringCount];
                int start = _ringCount == RingCapacity ? _ringNext : 0;
                for (int i = 0; i < _ringCount; i++) result[i] = Ring[(start + i) % RingCapacity];
                return result;
            }
        }

        /// <summary>导出为可读文本，供诊断 zip 使用。</summary>
        public static string DumpText(int maxLines = RingCapacity)
        {
            var entries = Snapshot();
            var sb = new StringBuilder();
            int skip = Math.Max(0, entries.Length - maxLines);
            for (int i = skip; i < entries.Length; i++)
                sb.Append(entries[i]).Append(Environment.NewLine);
            return sb.ToString();
        }

        // ---------- 内部 ----------

        private static void AddToRing(LogEntry entry)
        {
            Ring[_ringNext] = entry;
            _ringNext = (_ringNext + 1) % RingCapacity;
            if (_ringCount < RingCapacity) _ringCount++;
        }

        private static void AppendToFile(LogEntry entry)
        {
            try { FileSink.Value.TryWrite(entry.ToString(), entry.Level >= IslandLogLevel.Warn); }
            catch { /* The in-memory ring remains available if a sink cannot start. */ }
        }
    }
    /// <summary>
    /// 把 <c>System.Diagnostics.Debug.WriteLine</c> / <c>Trace.WriteLine</c> 接进 IslandLog。
    ///
    /// 服务层有上百处既有的 Debug.WriteLine(...)，逐个改写风险大、收益低；
    /// 装一个监听器就能让它们全部落进日志环形缓冲与文件。
    /// 注意：Debug.WriteLine 带 [Conditional("DEBUG")]，Release 构建下这些调用会被编译器整段移除，
    /// 所以**关键路径**（启动、退出、服务失败）要显式用 IslandLog.Info/Warn/Error，别依赖本监听器。
    /// </summary>
    public sealed class IslandLogTraceListener : System.Diagnostics.TraceListener
    {
        private readonly IslandLogLevel _level;

        public IslandLogTraceListener(IslandLogLevel level = IslandLogLevel.Debug)
        {
            _level = level;
            Name = "IslandLog";
        }

        public override void Write(string? message)
        {
            // 本项目所有调用点都用 WriteLine；Write 的片段拼装不参与记录。
        }

        public override void WriteLine(string? message)
        {
            if (!IslandLog.IsEnabled(_level) || string.IsNullOrWhiteSpace(message)) return;

            // 既有调用点大多写成 "[Bluetooth] xxx"，把方括号前缀提成 Source，
            // 日志行就跟老格式一致了：(Bluetooth) xxx
            var text = message!;
            var source = Name;
            if (text[0] == '[')
            {
                int close = text.IndexOf(']');
                if (close > 1 && close <= 32)
                {
                    source = text.Substring(1, close - 1);
                    text = text.Substring(close + 1).TrimStart();
                }
            }

            IslandLog.Write(_level, source, text);
        }
    }
}

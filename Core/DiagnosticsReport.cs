using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;

namespace alpha_island.Core
{
    /// <summary>「关于」页里的一段只读诊断信息。</summary>
    public sealed record DiagnosticsSection(string Title, string Body);

    /// <summary>
    /// 诊断报告：把「环境 / 能力 / 服务 / 最近日志」汇成一段文本，并能导出成一个 zip。
    /// 各子系统（服务宿主等）通过 <see cref="RegisterSection"/> 把自己的状态段落挂进来，
    /// 所以这里不需要反过来认识它们。
    /// </summary>
    public static class DiagnosticsReport
    {
        private static readonly object Gate = new();
        private static readonly List<(string Title, Func<string> Body)> Extra = new();

        /// <summary>挂一段自定义诊断。同名的再挂一次会覆盖。</summary>
        public static void RegisterSection(string title, Func<string> body)
        {
            if (string.IsNullOrWhiteSpace(title) || body == null) return;
            lock (Gate)
            {
                int at = Extra.FindIndex(s => s.Title == title);
                if (at >= 0) Extra[at] = (title, body);
                else Extra.Add((title, body));
            }
        }

        /// <summary>按显示顺序给出所有段落。执行顺序：环境 → 能力 → 运行时 → 自定义 → 日志。</summary>
        public static IReadOnlyList<DiagnosticsSection> Sections()
        {
            var list = new List<DiagnosticsSection>
            {
                new("运行环境", EnvironmentText()),
                new("能力检测", CapabilityText()),
            };

            (string Title, Func<string> Body)[] extra;
            lock (Gate) extra = Extra.ToArray();

            foreach (var (title, body) in extra)
            {
                string text;
                try { text = body() ?? ""; }
                catch (Exception ex) { text = "（该段落生成失败：" + ex.GetType().Name + "）"; }
                list.Add(new DiagnosticsSection(title, text));
            }

            list.Add(new DiagnosticsSection("最近日志", RecentLogText(60)));
            return list;
        }

        /// <summary>取一段已注册段落（不触发其它段落）。没注册过返回空串。</summary>
        public static string RegisteredSectionText(string title)
        {
            (string Title, Func<string> Body) found;
            lock (Gate)
            {
                int at = Extra.FindIndex(s => s.Title == title);
                if (at < 0) return "";
                found = Extra[at];
            }

            try { return found.Body() ?? ""; }
            catch (Exception ex) { return "（生成失败：" + ex.GetType().Name + "）"; }
        }

        /// <summary>完整文本报告（「复制报告」和 zip 里的 report.txt 都用它）。</summary>
        public static string BuildText()
        {
            var sb = new StringBuilder();
            sb.AppendLine("Alpha Island 诊断报告");
            sb.AppendLine("生成时间：" + DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss"));
            sb.AppendLine(new string('-', 48));

            foreach (DiagnosticsSection s in Sections())
            {
                sb.AppendLine();
                sb.AppendLine("[" + s.Title + "]");
                sb.AppendLine(s.Body);
            }

            return sb.ToString();
        }

        public static string EnvironmentText()
        {
            var sb = new StringBuilder();
            Assembly asm = typeof(DiagnosticsReport).Assembly;

            sb.Append("程序版本：").AppendLine(asm.GetName().Version?.ToString() ?? "未知");
            sb.Append("目标框架：").AppendLine(asm.GetCustomAttribute<System.Runtime.Versioning.TargetFrameworkAttribute>()?.FrameworkName ?? "未知");
            sb.Append("运行时：").AppendLine(RuntimeInformation.FrameworkDescription);
            sb.Append("系统：").AppendLine(RuntimeInformation.OSDescription.Trim());
            sb.Append("系统版本号：").AppendLine(Environment.OSVersion.Version.ToString());
            sb.Append("体系结构：").Append(RuntimeInformation.ProcessArchitecture)
              .Append("（OS ").Append(RuntimeInformation.OSArchitecture).AppendLine("）");
            sb.Append("逻辑处理器：").AppendLine(Environment.ProcessorCount.ToString());
            sb.Append("进程 PID：").Append(Environment.ProcessId).Append("，").Append(Environment.Is64BitProcess ? "64 位" : "32 位").AppendLine();
            sb.Append("开机时长：").AppendLine(FormatSpan(TimeSpan.FromMilliseconds(Environment.TickCount64)));
            sb.Append("本次运行：").AppendLine(FormatSpan(SafeUptime()));
            sb.Append("自身工作集：").AppendLine(FormatBytes(SafeWorkingSet()));
            sb.Append("日志文件：").Append(AppPaths.ErrorLogFile).Append("（").Append(FormatBytes(SafeLength(AppPaths.ErrorLogFile))).AppendLine("）");
            sb.Append("数据目录：").AppendLine(AppPaths.Root);
            sb.Append("配置目录：").AppendLine(AppPaths.ConfigDir);

            return sb.ToString().TrimEnd();
        }

        public static string CapabilityText() => CapabilityProbe.Report();

        public static string RecentLogText(int lines) => IslandLog.DumpText(lines);

        /// <summary>
        /// 导出诊断 zip 到 <c>%LOCALAPPDATA%\alpha-island\logs\</c>。
        /// 返回 zip 完整路径；失败返回 null（原因已进日志）。
        /// </summary>
        public static string? Export()
        {
            try
            {
                AppPaths.EnsureRoot();
                string dir = Path.Combine(AppPaths.Root, "logs");
                Directory.CreateDirectory(dir);

                string zip = Path.Combine(dir, "diagnostics-" + DateTime.Now.ToString("yyyyMMdd-HHmmss") + ".zip");
                using var fs = new FileStream(zip, FileMode.Create, FileAccess.Write, FileShare.None);
                using (var archive = new ZipArchive(fs, ZipArchiveMode.Create))
                {
                    AddText(archive, "report.txt", BuildText());
                    AddText(archive, "options.masked.json", ReadOptionsMasked());

                    foreach (string log in Directory.EnumerateFiles(AppPaths.Root, "error*.log"))
                    {
                        try { archive.CreateEntryFromFile(log, Path.GetFileName(log), CompressionLevel.Optimal); }
                        catch (Exception ex) { IslandLog.Debug("Diagnostics", "打包日志失败 " + Path.GetFileName(log) + "：" + ex.Message); }
                    }
                }

                IslandLog.Info("Diagnostics", "已导出诊断包 " + zip);
                return zip;
            }
            catch (Exception ex)
            {
                IslandLog.Error("Diagnostics.Export", ex);
                return null;
            }
        }

        /// <summary>读 options.json 并把疑似密钥的字段值打码；读不到就返回一句说明。</summary>
        public static string ReadOptionsMasked()
        {
            try
            {
                if (!File.Exists(AppPaths.OptionsFile))
                    return "{\"_note\":\"options.json 不存在\"}";

                string json = File.ReadAllText(AppPaths.OptionsFile, Encoding.UTF8);
                return MaskJson(json);
            }
            catch (Exception ex)
            {
                return "{\"_error\":\"" + ex.GetType().Name + "\"}";
            }
        }

        /// <summary>把 JSON 里名字含 key/secret/token/password 的字段值统一换成 ***。</summary>
        public static string MaskJson(string json)
        {
            try
            {
                using var doc = JsonDocument.Parse(json);
                using var buffer = new MemoryStream();
                using (var writer = new Utf8JsonWriter(buffer, new JsonWriterOptions { Indented = true }))
                    WriteMasked(doc.RootElement, writer);

                return Encoding.UTF8.GetString(buffer.ToArray());
            }
            catch (Exception ex)
            {
                return "{\"_error\":\"解析失败 " + ex.GetType().Name + "\"}";
            }
        }

        private static readonly string[] SensitiveHints =
        {
            "key", "secret", "token", "password", "passwd", "credential", "apikey", "cookie",
        };

        private static bool IsSensitive(string name)
        {
            string lower = name.ToLowerInvariant();
            return SensitiveHints.Any(h => lower.Contains(h));
        }

        private static void WriteMasked(JsonElement element, Utf8JsonWriter writer)
        {
            switch (element.ValueKind)
            {
                case JsonValueKind.Object:
                    writer.WriteStartObject();
                    foreach (JsonProperty p in element.EnumerateObject())
                    {
                        if (IsSensitive(p.Name)) writer.WriteString(p.Name, "***");
                        else
                        {
                            writer.WritePropertyName(p.Name);
                            WriteMasked(p.Value, writer);
                        }
                    }
                    writer.WriteEndObject();
                    break;

                case JsonValueKind.Array:
                    writer.WriteStartArray();
                    foreach (JsonElement item in element.EnumerateArray()) WriteMasked(item, writer);
                    writer.WriteEndArray();
                    break;

                default:
                    element.WriteTo(writer);
                    break;
            }
        }

        private static void AddText(ZipArchive archive, string entryName, string text)
        {
            ZipArchiveEntry entry = archive.CreateEntry(entryName, CompressionLevel.Optimal);
            using Stream s = entry.Open();
            using var w = new StreamWriter(s, new UTF8Encoding(false));
            w.Write(text);
        }

        private static TimeSpan SafeUptime()
        {
            try { return DateTime.Now - Process.GetCurrentProcess().StartTime; }
            catch { return TimeSpan.Zero; }
        }

        private static long SafeWorkingSet()
        {
            try { return Process.GetCurrentProcess().WorkingSet64; }
            catch { return 0; }
        }

        private static long SafeLength(string path)
        {
            try { return File.Exists(path) ? new FileInfo(path).Length : 0; }
            catch { return 0; }
        }

        private static string FormatBytes(long bytes)
        {
            if (bytes <= 0) return "0 B";
            string[] units = { "B", "KB", "MB", "GB" };
            double value = bytes;
            int unit = 0;
            while (value >= 1024 && unit < units.Length - 1) { value /= 1024; unit++; }
            return value.ToString(unit == 0 ? "0" : "0.0") + " " + units[unit];
        }

        private static string FormatSpan(TimeSpan span)
        {
            if (span.TotalSeconds < 1) return "不到 1 秒";
            if (span.TotalMinutes < 1) return (int)span.TotalSeconds + " 秒";
            if (span.TotalHours < 1) return (int)span.TotalMinutes + " 分 " + span.Seconds + " 秒";
            return (int)span.TotalHours + " 小时 " + span.Minutes + " 分";
        }
    }
}

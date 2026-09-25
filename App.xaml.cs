using System;
using System.IO;
using System.Windows;
using System.Windows.Threading;

namespace alpha_island
{
    /// <summary>
    /// Interaction logic for App.xaml
    /// </summary>
    /// 注意: 本项目同时启用了 WPF 与 WindowsForms，两者都有 Application 类型
    /// （ImplicitUsings 会全局引入 System.Windows.Forms），必须显式限定。
    public partial class App : System.Windows.Application
    {
        private static readonly object LogGate = new();

        /// <summary>异常日志路径：%LOCALAPPDATA%\alpha-island\error.log</summary>
        public static string LogPath { get; } = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "alpha-island", "error.log");

        protected override void OnStartup(StartupEventArgs e)
        {
            base.OnStartup(e);

            Exit += (_, args) => Log(new Exception($"Application.Exit fired! Code={args.ApplicationExitCode}"), "AppExit");

            // 托盘/设置窗口跑在 WinForms 的消息循环里，异常默认会弹一个
            // "Microsoft .NET 未处理的异常" 模态框，既难看又断掉调试现场。
            // 统一改成写日志 + 继续运行，问题留痕可查。
            DispatcherUnhandledException += (_, args) =>
            {
                Log(args.Exception, "Dispatcher");
                args.Handled = true;
            };

            AppDomain.CurrentDomain.UnhandledException += (_, args) =>
                Log(args.ExceptionObject as Exception, "AppDomain");

            try
            {
                System.Windows.Forms.Application.SetUnhandledExceptionMode(
                    System.Windows.Forms.UnhandledExceptionMode.CatchException);
                System.Windows.Forms.Application.ThreadException += (_, args) =>
                    Log(args.Exception, "WinForms");
            }
            catch (Exception ex) { Log(ex, "SetupThreadException"); }
        }

        public static void Log(Exception? ex, string source)
        {
            if (ex == null) return;
            try
            {
                var dir = Path.GetDirectoryName(LogPath);
                if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);

                lock (LogGate)
                {
                    File.AppendAllText(LogPath,
                        $"[{DateTime.Now:yyyy-MM-dd HH:mm:ss.fff}] ({source}) {ex}{Environment.NewLine}{Environment.NewLine}");
                }
            }
            catch { /* 日志失败不能再抛 */ }
        }
    }
}

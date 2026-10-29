using System;
using System.IO;
using System.Windows;
using System.Windows.Threading;
using alpha_island.Core;

namespace alpha_island
{
    /// <summary>
    /// Interaction logic for App.xaml
    /// </summary>
    /// 注意: 本项目同时启用了 WPF 与 WindowsForms，两者都有 Application 类型
    /// （ImplicitUsings 会全局引入 System.Windows.Forms），必须显式限定。
    public partial class App : System.Windows.Application
    {
        /// <summary>异常日志路径：%LOCALAPPDATA%\alpha-island\error.log</summary>
        public static string LogPath => AppPaths.ErrorLogFile;
        internal static bool IsAutoStartup { get; private set; }
        internal static IslandOptions? StartupOptions { get; private set; }
        internal const string MainWindowXamlPath = "MainWindow/MainWindow.xaml";
        private Plugins.Isolation.PluginWorkerServer? _pluginWorker;

        protected override void OnStartup(StartupEventArgs e)
        {
            if (e.Args.FirstOrDefault() == "--plugin-worker")
            {
                ShutdownMode = ShutdownMode.OnExplicitShutdown;
                if (e.Args.Length != 3 || !System.Text.RegularExpressions.Regex.IsMatch(e.Args[1], "^AlphaIsland\\.Plugin\\.[a-f0-9]{32}$")
                    || !System.Text.RegularExpressions.Regex.IsMatch(e.Args[2], "^[A-F0-9]{48}$")) { Shutdown(2); return; }
                DispatcherUnhandledException += (_, args) => { Console.Error.WriteLine(args.Exception); args.Handled = true; Shutdown(94); };
                _pluginWorker = new(this, e.Args[1], e.Args[2]);
                _ = _pluginWorker.RunAsync(); return;
            }
            if (e.Args.FirstOrDefault() == "--read-browser-downloads")
            {
                var readings = System.Threading.Tasks.Task.Run(Services.BrowserDownloadProbe.ScanWindows).GetAwaiter().GetResult();
                Console.WriteLine(System.Text.Json.JsonSerializer.Serialize(readings));
                Console.Out.Flush();
                // This is a command-line helper, so stop before WPF loads StartupUri.
                Environment.Exit(0);
                return;
            }
            if (e.Args.FirstOrDefault() == "--grant-boot-log-read")
            {
                int result = e.Args.Length == 2 && int.TryParse(e.Args[1], out int parent) && parent > 0
                    ? Services.BootLogAccess.GrantReadToParent(parent) : 1;
                Shutdown(result);
                return;
            }
            StartupOptions = IslandOptions.Load();
            AlphaIsland.PluginSdk.Localization.UiText.Initialize(SettingsRegistry.GetText(StartupOptions, "InterfaceLanguage", "system"));
            StartupUri = new Uri(MainWindowXamlPath, UriKind.Relative);
            IsAutoStartup = e.Args.Contains("--startup", StringComparer.OrdinalIgnoreCase);
            base.OnStartup(e);

            // 正常退出不是异常。旧实现在这里塞一个 new Exception(..)，导致每次关程序
            // error.log 末尾都多一条假的 "Application.Exit fired!" 堆栈。
            Exit += (_, args) => { IslandLog.Info("AppExit", $"Application.Exit code={args.ApplicationExitCode}"); IslandLog.Flush(TimeSpan.FromSeconds(2)); };

            // 服务层既有的上百处 Debug.WriteLine 经由这个监听器统一落盘（Debug 构建）。
            // .NET Core 里 Debug.WriteLine 会转发到 Trace.Listeners；
            // Debug.Listeners 在 .NET Core 已被移除，只能挂在 Trace 上。
#if DEBUG
            System.Diagnostics.Trace.Listeners.Add(new IslandLogTraceListener(IslandLogLevel.Debug));
#endif
            if (Enum.TryParse<IslandLogLevel>(Environment.GetEnvironmentVariable("ALPHA_ISLAND_LOG_LEVEL"), true, out var logLevel)
                && Enum.IsDefined(logLevel)) IslandLog.MinLevel = logLevel;

            IslandLog.Info("App", $"启动 v{typeof(App).Assembly.GetName().Version} pid={Environment.ProcessId}");

            // 托盘/设置窗口跑在 WinForms 的消息循环里，异常默认会弹一个
            // "Microsoft .NET 未处理的异常" 模态框，既难看又断掉调试现场。
            // 统一改成写日志 + 继续运行，问题留痕可查。
            DispatcherUnhandledException += (_, args) =>
            {
                Log(args.Exception, "Dispatcher");
                args.Handled = true;
            };

            AppDomain.CurrentDomain.UnhandledException += (_, args) =>
            { Log(args.ExceptionObject as Exception, "AppDomain"); IslandLog.Flush(TimeSpan.FromSeconds(1)); };

            try
            {
                System.Windows.Forms.Application.SetUnhandledExceptionMode(
                    System.Windows.Forms.UnhandledExceptionMode.CatchException);
                System.Windows.Forms.Application.ThreadException += (_, args) =>
                    Log(args.Exception, "WinForms");
            }
            catch (Exception ex) { Log(ex, "SetupThreadException"); }
        }

        /// <summary>
        /// 保留的老入口，转发到统一的 <see cref="IslandLog"/>。
        /// 新代码请直接调 IslandLog.Debug/Info/Warn/Error。
        /// </summary>
        public static void Log(Exception? ex, string source) => IslandLog.Error(source, ex);
    }
}

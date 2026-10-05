using System;
using System.IO;

namespace alpha_island.Core
{
    /// <summary>
    /// 应用的数据目录。原先这些路径散在 App.xaml.cs / MainWindow 里各写一遍，
    /// 现在统一从这里取，换目录只改一处。
    /// </summary>
    public static class AppPaths
    {
        /// <summary>%LOCALAPPDATA%\alpha-island</summary>
        public static string Root { get; } = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "alpha-island");

        public static string ConfigDir => ConfigurationLayout.ConfigDirectory(Root);
        public static string OptionsFile => ConfigurationLayout.OptionsFile(Root);
        public static string CodexHooksConfigFile => ConfigurationLayout.CodexFile(Root);

        /// <summary>用户自定义托盘/窗口图标（放一个 .ico 进来就能换掉内置图标，不需要重编译）。</summary>
        public static string UserIconFile => Path.Combine(ConfigDir, "icon.ico");

        /// <summary>设置页图标槽的自定义图片目录（可选的 &lt;槽名&gt;.png 覆盖内置字形）。</summary>
        public static string UserIconsDir => Path.Combine(ConfigDir, "icons");
        public static string UserPluginsDir => Path.Combine(Root, "plugins");

        /// <summary>当前日志文件。IslandLog 超限后轮转出 error.1.log / error.2.log。</summary>
        public static string ErrorLogFile => Path.Combine(Root, "error.log");

        public static string ClipboardHistoryFile => Path.Combine(Root, "clipboard_history.json");

        /// <summary>AI 会话历史 / 本地知识文件。</summary>
        public static string AiDir => Path.Combine(Root, "ai");

        /// <summary>AI 密钥（DPAPI 加密，当前用户范围）。**绝不写进 options.json**。</summary>
        public static string AiSecretsFile => ConfigurationLayout.SecretFile(Root);

        private static readonly object ConfigurationLock = new();
        private static bool _configurationReady;
        internal static bool EnsureConfiguration()
        {
            lock (ConfigurationLock)
            {
                if (_configurationReady) return true;
                try
                {
                    string codexRoot = Environment.GetEnvironmentVariable("CODEX_HOME") ?? Path.Combine(
                        Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".codex");
                    ConfigurationLayout.Migrate(Root, Path.Combine(codexRoot, "alpha-island", "config.json"));
                    _configurationReady = true;
                    return true;
                }
                catch (Exception ex)
                {
                    IslandLog.Warn("Config", "配置迁移未完成，保留原文件：" + ex.Message);
                    return false;
                }
            }
        }

        /// <summary>AI 会话历史目录（每会话一个 json）。</summary>
        public static string AiSessionsDir => Path.Combine(AiDir, "sessions");

        /// <summary>agent 工具权限审批的流水（每行一条 JSON：时间/工具/结果/裁决者）。</summary>
        public static string AiApprovalLogFile => Path.Combine(AiDir, "approvals.log");

        /// <summary>天气、行情等需要过期的网络数据缓存。</summary>
        public static string CacheDir => Path.Combine(Root, "cache");

        public static string AiHistoryFile => Path.Combine(AiDir, "history.json");
        public static string WeatherCacheFile => Path.Combine(CacheDir, "weather.json");

        /// <summary>确保目录存在；失败时静默（只读环境不该让应用起不来）。</summary>
        public static void EnsureRoot() => EnsureDirectory(Root);

        /// <summary>确保任意一个目录存在，失败静默。</summary>
        public static void EnsureDirectory(string path)
        {
            try { Directory.CreateDirectory(path); } catch { }
        }
    }
}

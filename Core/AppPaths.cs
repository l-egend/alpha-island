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

        public static string OptionsFile => Path.Combine(Root, "options.json");
        public static string ErrorLogFile => Path.Combine(Root, "error.log");
        public static string ClipboardHistoryFile => Path.Combine(Root, "clipboard_history.json");

        /// <summary>确保目录存在；失败时静默（只读环境不该让应用起不来）。</summary>
        public static void EnsureRoot()
        {
            try { Directory.CreateDirectory(Root); } catch { }
        }
    }
}

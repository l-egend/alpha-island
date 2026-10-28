using System.Collections;
using System.IO;
using System.Resources;
using System.Windows;
using System.Windows.Media.Imaging;
using System.Windows.Navigation;

internal static partial class Program
{
    private static void MainWindowResourceChecks(alpha_island.MainWindow window)
    {
        var assembly = typeof(alpha_island.MainWindow).Assembly;
        using var stream = assembly.GetManifestResourceStream(assembly.GetName().Name + ".g.resources")!;
        using var reader = new ResourceReader(stream);
        var resources = reader.Cast<DictionaryEntry>().Select(entry => (string)entry.Key).ToHashSet(StringComparer.OrdinalIgnoreCase);
        string startupResource = Path.ChangeExtension(alpha_island.App.MainWindowXamlPath, ".baml").Replace('\\', '/');
        Check(resources.Contains(startupResource), "程序启动路径对应实际编译到程序集中的主窗口资源");
        Check(!resources.Contains("mainwindow.baml"), "完整重建后不再保留根目录旧主窗口资源");
        Check(BaseUriHelper.GetBaseUri(window).OriginalString.EndsWith(alpha_island.App.MainWindowXamlPath, StringComparison.OrdinalIgnoreCase),
            "InitializeComponent 从新目录加载主窗口，与启动入口保持一致");
        Check(window.Icon is BitmapSource icon && icon.PixelWidth > 0 && icon.PixelHeight > 0,
            "移动主窗口后仍能从程序集根路径加载窗口图标");
        Check(window.FindName("IslandBody") is System.Windows.Controls.Border
            && window.FindName("ComponentHandle") != null && window.FindName("CardCarouselHost") != null,
            "新目录中的 XAML 正常生成主岛、切换条和卡片控件");
    }
}

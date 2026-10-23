using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using alpha_island.Core;
using alpha_island.Interop;

internal static partial class Program
{
    private static void CrossMachineCompatibilityChecks(string root)
    {
        string folder = Path.Combine(root, "other drive's 中文配置"); Directory.CreateDirectory(folder);
        string file = Path.Combine(folder, "options.json");
        AtomicJsonFile.Write(file, "{\"value\":1}");
        AtomicJsonFile.WriteForFileSystem(file, "{\"value\":2}", true,
            (_, _, _) => throw new IOException("unsupported file replacement", unchecked((int)0x80070032)));
        Check(AtomicJsonFile.Read<Dictionary<string, int>>(file)!["value"] == 2
            && AtomicJsonFile.Read<Dictionary<string, int>>(file + ".bak")!["value"] == 1, "文件系统不支持替换时仍保存新配置并保留备份");
        bool denied = false;
        try { AtomicJsonFile.WriteForFileSystem(file, "{\"value\":3}", true,
            (_, _, _) => throw new IOException("sharing violation", unchecked((int)0x80070020))); }
        catch (IOException) { denied = true; }
        Check(denied && AtomicJsonFile.Read<Dictionary<string, int>>(file)!["value"] == 2, "文件正被占用时不绕过保护或破坏原配置");
        File.SetAttributes(file + ".bak", FileAttributes.ReadOnly);
        try
        {
            denied = false;
            try { AtomicJsonFile.WriteForFileSystem(file, "{\"value\":4}", true,
                (_, _, _) => throw new PlatformNotSupportedException()); }
            catch (UnauthorizedAccessException) { denied = true; }
            Check(denied && AtomicJsonFile.Read<Dictionary<string, int>>(file)!["value"] == 2, "备份不可写时保留原配置，不先删除旧文件");
        }
        finally { File.SetAttributes(file + ".bak", FileAttributes.Normal); }
        Check(Directory.GetFiles(folder, "*.tmp").Length == 0, "保存失败也清理可删除的临时文件");
        File.WriteAllText(file, "{\"value\":5}", Encoding.Unicode);
        Check(AtomicJsonFile.Read<Dictionary<string, int>>(file)!["value"] == 5, "其他电脑旧脚本生成的 UTF-16 配置仍可读取");
        File.WriteAllText(file, new string(' ', 2048) + "{}"); bool bounded = false;
        try { AtomicJsonFile.Read<Dictionary<string, int>>(file, maximumBytes: 1024); }
        catch (InvalidDataException) { bounded = true; }
        Check(bounded, "异常膨胀的配置在分配大字符串之前被拒绝");
        var attribute = typeof(alpha_island.App).Assembly.GetCustomAttributes(typeof(DefaultDllImportSearchPathsAttribute), false)
            .Cast<DefaultDllImportSearchPathsAttribute>().Single();
        Check(attribute.Paths == DllImportSearchPath.System32, "宿主的 Windows 系统接口不从当前目录解析同名 DLL");
        var native = typeof(alpha_island.Conversations.IChatSettings).Assembly.GetCustomAttributes(typeof(DefaultDllImportSearchPathsAttribute), false)
            .Cast<DefaultDllImportSearchPathsAttribute>().Single();
        Check(native.Paths == DllImportSearchPath.System32, "密钥仓也只使用系统提供的加密接口");
        string conversation = Path.Combine(folder, "conversation.json");
        alpha_island.Conversations.AtomicJsonFile.Write(conversation, "{\"value\":1}");
        alpha_island.Conversations.AtomicJsonFile.WriteForFileSystem(conversation, "{\"value\":2}", true,
            (_, _, _) => throw new PlatformNotSupportedException());
        Check(alpha_island.Conversations.AtomicJsonFile.Read<Dictionary<string, int>>(conversation)!["value"] == 2,
            "对话记录与普通配置采用一致的文件系统回退");
        string target = Path.Combine(folder, "cipher.bin"), pending = target + ".tmp";
        File.WriteAllBytes(target, [1, 2]); File.WriteAllBytes(pending, [3, 4]);
        alpha_island.Conversations.AtomicJsonFile.CommitTemporary(pending, target, null,
            (_, _, _) => throw new PlatformNotSupportedException());
        Check(File.ReadAllBytes(target).SequenceEqual(new byte[] { 3, 4 }) && !File.Exists(target + ".bak"),
            "密文替换回退保持不保存旧密钥备份的规则");
        string readOnly = Path.Combine(folder, "locked.tmp"); File.WriteAllText(readOnly, "temporary");
        File.SetAttributes(readOnly, FileAttributes.ReadOnly);
        AtomicJsonFile.TryDeleteTemporary(readOnly);
        Check(File.Exists(readOnly), "临时文件不可删除时不抛出第二个异常或影响后续保存");
        File.SetAttributes(readOnly, FileAttributes.Normal); File.Delete(readOnly);
        FlyoutCompatibilityCleanup();
    }

    private static void FlyoutCompatibilityCleanup()
    {
        IntPtr popup = IntPtr.Zero; bool unavailable = false;
        using var thread = new HookThread("Optional API compatibility fixture");
        Check(thread.Start(() => popup = CreateWindowEx(0x08000080, "STATIC", "compatibility volume", 0x80000000,
            -32000, -32000, 200, 60, IntPtr.Zero, IntPtr.Zero, IntPtr.Zero, IntPtr.Zero), () => DestroyWindow(popup)), "创建系统接口降级复现窗口");
        using var guard = new VolumeFlyoutGuard(handle => unavailable ? throw new EntryPointNotFoundException("optional shell API") : handle == popup);
        guard.SetEnabled(true); guard.TakeOverFeedback(); NativeMethods.ShowWindowAsync(popup, 4); Thread.Sleep(100);
        unavailable = true; guard.SetEnabled(false); Thread.Sleep(80);
        Check(ReliabilityCloak(popup, 14, out int cloak, 4) == 0 && (cloak & 1) == 0 && NativeMethods.IsWindowVisible(popup),
            "可选系统接口失效后仍能释放本程序的浮层遮蔽，避免留下隐藏窗口");
    }

    private static void SmallScreenCompatibilityChecks(Grid root)
    {
        var settings = new alpha_island.Settings.SettingsWindow(persistSettings: false);
        try
        {
            settings.Left = 1800; settings.Top = 1000;
            PrivateCall(settings, "FitSettingsToWorkArea", new Rect(0, 0, 640, 480));
            Check(settings.Width <= 640 && settings.Height <= 480 && settings.Left >= 0 && settings.Top >= 0,
                "设置在高缩放小工作区内完整显示，不受原最小尺寸限制");
            Check(!AgentField<bool>(settings, "_navExpanded"), "小工作区自动收起导航，为设置正文和搜索保留空间");
            WindowWorkArea.Fit(settings, new Rect(-1280, 0, 1280, 720), 780, 540);
            Check(settings.Left >= -1280 && settings.Left + settings.Width <= 0 && settings.MinWidth == 780 && settings.MinHeight == 540,
                "切回大屏或负坐标副屏时恢复正常最小尺寸并保持窗口可见");
            var content = (Grid)settings.Content; settings.Content = null; root.Children.Add(content);
            content.Width = 640; content.Height = 480; root.UpdateLayout();
            Directory.CreateDirectory("bin/Compatibility154/previews");
            SaveThemePreview(content, "bin/Compatibility154/previews/settings-small-screen.png");
            root.Children.Remove(content);
        }
        finally { settings.ForceClose = true; settings.Close(); }
    }
}

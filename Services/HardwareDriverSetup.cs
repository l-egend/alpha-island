using AlphaIsland.Hardware;
using System.Diagnostics;
using System.IO;
using System.Net.Http;
using System.Security.Cryptography;

namespace alpha_island.Services;

/// <summary>Explicit setup action, separate from sensor reads. The exact official installer is hash pinned.</summary>
internal static class HardwareDriverSetup
{
    internal const string InstallerUrl = "https://github.com/namazso/PawnIO.Setup/releases/download/2.2.0/PawnIO_setup.exe";
    internal const string InstallerSha256 = "1F519A22E47187F70A1379A48CA604981C4FCF694F4E65B734AAA74A9FBA3032";
    internal static async Task<string> InstallAsync(CancellationToken token = default)
    {
        if (!HardwareDriverAccess.NeedsInstallation) return "硬件驱动已安装";
        string folder = Path.Combine(Core.AppPaths.CacheDir, "hardware-driver"); Directory.CreateDirectory(folder);
        string path = Path.Combine(folder, "PawnIO_setup-2.2.0.exe");
        if (!await IsVerifiedAsync(path, token))
        {
            using var client = new HttpClient { Timeout = TimeSpan.FromSeconds(90) };
            byte[] bytes = await client.GetByteArrayAsync(InstallerUrl, token);
            if (bytes.Length > 10 * 1024 * 1024 || Convert.ToHexString(SHA256.HashData(bytes)) != InstallerSha256)
                throw new IOException("官方驱动安装包校验失败，已停止安装");
            await File.WriteAllBytesAsync(path, bytes, token);
        }
        var start = new ProcessStartInfo(path, "-install -silent") { UseShellExecute = true, Verb = "runas", WindowStyle = ProcessWindowStyle.Hidden };
        using var process = await Task.Run(() => Process.Start(start), token) ?? throw new IOException("无法启动驱动安装程序");
        await process.WaitForExitAsync(token);
        if (process.ExitCode != 0 || HardwareDriverAccess.NeedsInstallation) throw new IOException("驱动安装未完成，请检查 Windows 授权或重启电脑后重试");
        return "硬件驱动安装完成";
    }
    internal static async Task<bool> IsVerifiedAsync(string path, CancellationToken token)
    {
        if (!File.Exists(path)) return false;
        using var file = File.OpenRead(path);
        return Convert.ToHexString(await SHA256.HashDataAsync(file, token)) == InstallerSha256;
    }
}

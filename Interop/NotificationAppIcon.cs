using System.IO;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Media.Imaging;
using alpha_island.Core;

namespace alpha_island.Interop;

/// <summary>Read registered desktop app icons when AppDisplayInfo.GetLogo supplies no stream.</summary>
internal static class NotificationAppIcon
{
    private static readonly Guid AppsFolder = new("1e87508d-89c2-42f0-8a7e-645a0f50ca58");

    [StructLayout(LayoutKind.Sequential)]
    private struct IconSize { public int Width, Height; }

    [ComImport, Guid("bcc18b79-ba16-442f-80c4-8a59c30c463b"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IShellItemImageFactory
    {
        [PreserveSig] int GetImage(IconSize size, uint flags, out IntPtr bitmap);
    }

    [DllImport("shell32.dll", CharSet = CharSet.Unicode)]
    private static extern int SHCreateItemInKnownFolder(in Guid folder, uint flags, string item,
        in Guid iid, [MarshalAs(UnmanagedType.Interface)] out IShellItemImageFactory? factory);
    [DllImport("ole32.dll")]
    private static extern int CoInitializeEx(IntPtr reserved, uint mode);
    [DllImport("ole32.dll")]
    private static extern void CoUninitialize();
    [DllImport("gdi32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool DeleteObject(IntPtr bitmap);

    internal static Task<byte[]?> ReadAsync(string appId)
    {
        if (string.IsNullOrWhiteSpace(appId) || appId.Length > 2048 || appId.Any(char.IsControl))
            return Task.FromResult<byte[]?>(null);
        // Shell extraction can touch disk; never perform it on the island's UI thread.
        return Task.Run(() => Read(appId));
    }

    private static byte[]? Read(string appId)
    {
        IShellItemImageFactory? factory = null;
        IntPtr bitmap = IntPtr.Zero;
        int initialized = CoInitializeEx(IntPtr.Zero, 0);
        if (initialized < 0) return null;
        try
        {
            var iid = typeof(IShellItemImageFactory).GUID;
            if (SHCreateItemInKnownFolder(AppsFolder, 0, appId, iid, out factory) < 0 || factory == null) return null;
            // SIIGBF_ICONONLY: use the registered app icon, not file contents or a thumbnail.
            if (factory.GetImage(new IconSize { Width = 32, Height = 32 }, 0x4, out bitmap) < 0 || bitmap == IntPtr.Zero)
                return null;
            var source = Imaging.CreateBitmapSourceFromHBitmap(bitmap, IntPtr.Zero, Int32Rect.Empty, BitmapSizeOptions.FromEmptyOptions());
            var encoder = new PngBitmapEncoder();
            encoder.Frames.Add(BitmapFrame.Create(source));
            using var bytes = new MemoryStream();
            encoder.Save(bytes);
            return bytes.Length <= 262144 ? bytes.ToArray() : null;
        }
        catch (Exception ex)
        {
            IslandLog.Debug("ToastLogo", "应用图标读取失败：0x" + ex.HResult.ToString("X8"));
            return null;
        }
        finally
        {
            if (bitmap != IntPtr.Zero) DeleteObject(bitmap);
            if (factory != null) Marshal.ReleaseComObject(factory);
            CoUninitialize();
        }
    }
}

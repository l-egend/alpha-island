using System.Collections.Concurrent;
using System.IO;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Media.Imaging;
using alpha_island.Interop;
using Drawing = System.Drawing;

namespace alpha_island.Services;

/// <summary>One-shot window-owned capture, kept only in memory. No foreground activation or disk screenshots.</summary>
internal static class WindowSnapshotCapture
{
    private static readonly ConcurrentDictionary<IntPtr, Task<BitmapSource?>> Pending = new();

    internal static Task<BitmapSource?> CaptureAsync(IntPtr window, Rect bounds)
    {
        var task = Pending.GetOrAdd(window, _ => Task.Run(() => Capture(window, bounds)));
        _ = task.ContinueWith(done => Pending.TryRemove(new KeyValuePair<IntPtr, Task<BitmapSource?>>(window, done)),
            CancellationToken.None, TaskContinuationOptions.ExecuteSynchronously, TaskScheduler.Default);
        return task;
    }

    private static BitmapSource? Capture(IntPtr window, Rect bounds)
    {
        if (!NativeMethods.IsWindow(window) || bounds.IsEmpty || bounds.Width < 80 || bounds.Height < 80
            || bounds.Width * bounds.Height > 16_000_000) return null;
        NativeMethods.GetWindowThreadProcessId(window, out uint process);
        int width = (int)Math.Ceiling(bounds.Width), height = (int)Math.Ceiling(bounds.Height);
        using var bitmap = new Drawing.Bitmap(width, height, Drawing.Imaging.PixelFormat.Format32bppArgb);
        using (var graphics = Drawing.Graphics.FromImage(bitmap))
        {
            IntPtr dc = graphics.GetHdc();
            // A hung target must not keep our worker, HDC or bitmap alive indefinitely.
            // GPU windows can decline WM_PRINT; the caller retains the foreground cache
            // or falls back to native window transitions rather than inventing a surface.
            const uint wmPrint = 0x0317, printNonClientClientBackgroundChildren = 0x001E;
            const uint abortIfHungBlockErrorOnExit = 0x0023;
            try
            {
                if (SendMessageTimeout(window, wmPrint, dc, new IntPtr(printNonClientClientBackgroundChildren),
                    abortIfHungBlockErrorOnExit, 250, out _) == IntPtr.Zero) return null;
            }
            finally { graphics.ReleaseHdc(dc); }
        }
        NativeMethods.GetWindowThreadProcessId(window, out uint currentProcess);
        if (!NativeMethods.IsWindow(window) || currentProcess != process) return null;
        bool content = false;
        for (int y = 1; y < 8 && !content; y++)
            for (int x = 1; x < 8; x++)
                if ((bitmap.GetPixel(width * x / 8, height * y / 8).ToArgb() & 0xFFFFFF) != 0) { content = true; break; }
        if (!content) return null; // GPU/protected surfaces may decline capture while reporting success.
        using var bytes = new MemoryStream();
        bitmap.Save(bytes, Drawing.Imaging.ImageFormat.Png); bytes.Position = 0;
        var image = new BitmapImage(); image.BeginInit(); image.CacheOption = BitmapCacheOption.OnLoad;
        image.DecodePixelWidth = Math.Min(1400, width); image.StreamSource = bytes; image.EndInit(); image.Freeze();
        return image;
    }

    [DllImport("user32.dll", SetLastError = true)]
    private static extern IntPtr SendMessageTimeout(IntPtr window, uint message, IntPtr wParam, IntPtr lParam,
        uint flags, uint timeoutMilliseconds, out IntPtr result);
}

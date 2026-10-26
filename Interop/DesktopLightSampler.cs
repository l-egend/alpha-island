using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Media;
using alpha_island.Core;
using Color = System.Windows.Media.Color;
using Point = System.Windows.Point;

namespace alpha_island.Interop;

/// <summary>Small desktop patches and an optional blank material patch; no persistent bitmap or UI-thread readback.</summary>
internal static class DesktopLightSampler
{
    private const int Patch = 8;
    [StructLayout(LayoutKind.Sequential)] private struct BitmapInfo
    {
        public uint Size; public int Width, Height; public ushort Planes, Bits;
        public uint Compression, ImageSize; public int X, Y; public uint Used, Important;
    }
    [DllImport("user32.dll")] private static extern IntPtr GetDC(IntPtr hwnd);
    [DllImport("user32.dll")] private static extern int ReleaseDC(IntPtr hwnd, IntPtr dc);
    [DllImport("user32.dll")] private static extern IntPtr SetThreadDpiAwarenessContext(IntPtr context);
    [DllImport("user32.dll")] private static extern int GetSystemMetrics(int index);
    [DllImport("gdi32.dll")] private static extern IntPtr CreateCompatibleDC(IntPtr source);
    [DllImport("gdi32.dll")] private static extern IntPtr CreateDIBSection(IntPtr dc, ref BitmapInfo info, uint usage, out IntPtr bits, IntPtr section, uint offset);
    [DllImport("gdi32.dll")] private static extern IntPtr SelectObject(IntPtr dc, IntPtr value);
    [DllImport("gdi32.dll")] private static extern bool DeleteObject(IntPtr value);
    [DllImport("gdi32.dll")] private static extern bool DeleteDC(IntPtr dc);
    [DllImport("gdi32.dll")] private static extern bool StretchBlt(IntPtr dest, int x, int y, int w, int h, IntPtr source, int sx, int sy, int sw, int sh, uint op);
    [DllImport("gdi32.dll")] private static extern bool GdiFlush();
    public readonly record struct Result(GlassLight Light, double Milliseconds, int Patches,
        double? SurfaceBrightness = null, double? HandleBrightness = null);

    public static Rect DesktopBounds()
    {
        var previous = SetThreadDpiAwarenessContext(new IntPtr(-4));
        try { return new Rect(GetSystemMetrics(76), GetSystemMetrics(77), GetSystemMetrics(78), GetSystemMetrics(79)); }
        finally { if (previous != IntPtr.Zero) SetThreadDpiAwarenessContext(previous); }
    }

    public static Rect? ProbeArea(Rect island, Vector direction, Rect desktop, IReadOnlyList<Rect> exclusions)
    {
        foreach (double offset in new[] { 36d, 72, 120, 180 })
        {
            var center = new Point(island.Left + island.Width / 2 + direction.X * (island.Width / 2 + offset),
                island.Top + island.Height / 2 + direction.Y * (island.Height / 2 + offset));
            var area = new Rect(center.X - 10, center.Y - 10, 20, 20);
            if (desktop.Contains(area) && !exclusions.Any(r => { r.Inflate(14, 14); return r.IntersectsWith(area); })) return area;
        }
        return null;
    }

    public static Result? Capture(Rect island, Rect desktop, Rect[] exclusions, Rect? surfaceProbe = null, Rect? handleProbe = null)
    {
        if (island.IsEmpty || desktop.IsEmpty || island.Width < 2 || island.Height < 2) return null;
        var started = Stopwatch.GetTimestamp();
        IntPtr screen = IntPtr.Zero, memory = IntPtr.Zero, bitmap = IntPtr.Zero, old = IntPtr.Zero;
        var previousDpi = SetThreadDpiAwarenessContext(new IntPtr(-4));
        try
        {
            screen = GetDC(IntPtr.Zero); if (screen == IntPtr.Zero) return null;
            memory = CreateCompatibleDC(screen); if (memory == IntPtr.Zero) return null;
            int slots = handleProbe.HasValue ? 6 : surfaceProbe.HasValue ? 5 : 4;
            var info = new BitmapInfo { Size = (uint)Marshal.SizeOf<BitmapInfo>(), Width = Patch * slots, Height = -Patch, Planes = 1, Bits = 32 };
            bitmap = CreateDIBSection(screen, ref info, 0, out IntPtr bits, IntPtr.Zero, 0);
            if (bitmap == IntPtr.Zero || bits == IntPtr.Zero) return null;
            old = SelectObject(memory, bitmap);
            var samples = new List<GlassLightSample>(4);
            var captured = new List<(int Slot, Vector Direction)>(4);
            var directions = new[] { new Vector(-1, 0), new Vector(0, -1), new Vector(1, 0), new Vector(0, 1) };
            for (int i = 0; i < directions.Length; i++)
            {
                var area = ProbeArea(island, directions[i], desktop, exclusions);
                if (area is not Rect region || !StretchBlt(memory, i * Patch, 0, Patch, Patch, screen,
                    (int)Math.Round(region.X), (int)Math.Round(region.Y), 20, 20, 0x00CC0020)) continue;
                captured.Add((i, directions[i]));
            }
            bool surfaceCaptured = surfaceProbe is Rect surface && !surface.IsEmpty && surface.Width >= 1 && surface.Height >= 1
                && island.Contains(surface) && desktop.Contains(surface)
                && StretchBlt(memory, 4 * Patch, 0, Patch, Patch, screen, (int)Math.Round(surface.X), (int)Math.Round(surface.Y),
                    Math.Max(1, (int)Math.Round(surface.Width)), Math.Max(1, (int)Math.Round(surface.Height)), 0x00CC0020);
            bool handleCaptured = handleProbe is Rect handle && !handle.IsEmpty && handle.Width >= 1 && handle.Height >= 1
                && desktop.Contains(handle) && !exclusions.Any(r => r.IntersectsWith(handle))
                && StretchBlt(memory, 5 * Patch, 0, Patch, Patch, screen, (int)Math.Round(handle.X), (int)Math.Round(handle.Y),
                    Math.Max(1, (int)Math.Round(handle.Width)), Math.Max(1, (int)Math.Round(handle.Height)), 0x00CC0020);
            GdiFlush(); // Finish this thread's GDI batch before reading DIB memory.
            Color ReadPatch(int slot)
            {
                int red = 0, green = 0, blue = 0;
                for (int y = 0; y < Patch; y++) for (int x = 0; x < Patch; x++)
                {
                    uint pixel = unchecked((uint)Marshal.ReadInt32(bits, (y * Patch * slots + slot * Patch + x) * 4));
                    blue += (int)(pixel & 255); green += (int)((pixel >> 8) & 255); red += (int)((pixel >> 16) & 255);
                }
                return Color.FromRgb((byte)(red / 64), (byte)(green / 64), (byte)(blue / 64));
            }
            foreach (var item in captured) samples.Add(new(item.Direction, ReadPatch(item.Slot)));
            double? surfaceBrightness = surfaceCaptured ? GlassLighting.Estimate([new(default, ReadPatch(4))]).Brightness : null;
            double? handleBrightness = handleCaptured ? GlassLighting.Estimate([new(default, ReadPatch(5))]).Brightness : null;
            return samples.Count == 0 && !surfaceBrightness.HasValue && !handleBrightness.HasValue ? null
                : new(samples.Count > 0 ? GlassLighting.Estimate(samples) : GlassLight.Default,
                    Stopwatch.GetElapsedTime(started).TotalMilliseconds, samples.Count, surfaceBrightness, handleBrightness);
        }
        finally
        {
            if (old != IntPtr.Zero && old != new IntPtr(-1)) SelectObject(memory, old);
            if (bitmap != IntPtr.Zero) DeleteObject(bitmap);
            if (memory != IntPtr.Zero) DeleteDC(memory);
            if (screen != IntPtr.Zero) ReleaseDC(IntPtr.Zero, screen);
            if (previousDpi != IntPtr.Zero) SetThreadDpiAwarenessContext(previousDpi);
        }
    }
}

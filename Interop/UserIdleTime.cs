using System.Runtime.InteropServices;

namespace alpha_island.Interop;

/// <summary>One timestamp query; does not hook or record keyboard/mouse content.</summary>
internal static class UserIdleTime
{
    [StructLayout(LayoutKind.Sequential)] private struct LastInput { internal uint Size, Time; }
    [DllImport("user32.dll")] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool GetLastInputInfo(ref LastInput info);
    internal static double? ReadSeconds()
    {
        var info = new LastInput { Size = (uint)Marshal.SizeOf<LastInput>() };
        return GetLastInputInfo(ref info) ? unchecked((uint)Environment.TickCount - info.Time) / 1000d : null;
    }
}

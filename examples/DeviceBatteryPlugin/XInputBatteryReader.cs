using System.Runtime.InteropServices;
using AlphaIsland.PluginSdk;

namespace AlphaIsland.Plugins.DeviceBattery;

internal static class XInputBatteryReader
{
    [StructLayout(LayoutKind.Sequential)] private struct Gamepad { internal ushort Buttons; internal byte LeftTrigger, RightTrigger; internal short LX, LY, RX, RY; }
    [StructLayout(LayoutKind.Sequential)] private struct Capabilities { internal byte Type, SubType; internal ushort Flags; internal Gamepad Gamepad; internal ushort LeftMotor, RightMotor; }
    [StructLayout(LayoutKind.Sequential)] private struct Battery { internal byte Type, Level; }
    [DllImport("xinput1_4.dll")] private static extern uint XInputGetCapabilities(uint index, uint flags, out Capabilities value);
    [DllImport("xinput1_4.dll")] private static extern uint XInputGetBatteryInformation(uint index, byte type, out Battery value);
    internal static string Level(byte type, byte value) => type is 2 or 3 ? value switch { 0 => "极低", 1 => "低", 2 => "中", 3 => "充足", _ => "" } : type == 1 ? "外接供电" : "";
    internal static IReadOnlyList<PluginDeviceBattery> Read()
    {
        var result = new List<PluginDeviceBattery>();
        for (uint index = 0; index < 4; index++)
        {
            if (XInputGetCapabilities(index, 0, out var caps) != 0) continue;
            string level = XInputGetBatteryInformation(index, 0, out var battery) == 0 ? Level(battery.Type, battery.Level) : "";
            result.Add(new("xinput/" + index, "游戏手柄 " + (index + 1), (caps.Flags & 2) != 0 ? DeviceBatteryTransport.WirelessReceiver : DeviceBatteryTransport.UsbOrReceiver,
                DeviceBatteryKind.Gamepad, null, true, Detail: level == "外接供电" ? "XInput 驱动返回外接供电，未提供电池电量" : "XInput 电量档位，非精确百分比", PhysicalId: "xinput/" + index, Level: level));
        }
        return result;
    }
}

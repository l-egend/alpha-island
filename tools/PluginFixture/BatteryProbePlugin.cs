using AlphaIsland.PluginSdk;

namespace AlphaIsland.PluginFixture;

public sealed class BatteryProbePlugin : IIslandPlugin
{
    public void Initialize(IPluginContext context)
    {
        context.RegisterService(new("battery-probe", _ => context.ReportDeviceBatteries([
            new("zero","Zero",DeviceBatteryTransport.WirelessReceiver,DeviceBatteryKind.Mouse,0,true),
            new("level","Controller",DeviceBatteryTransport.UsbOrReceiver,DeviceBatteryKind.Gamepad,null,true,Level:"低"),
            new("invalid","Bad",DeviceBatteryTransport.WirelessReceiver,DeviceBatteryKind.Mouse,101,true)
        ]),()=>{}));
        context.RegisterAction(new("battery-empty","Clear",()=>context.ReportDeviceBatteries([])));
    }
    public void Dispose() { }
}

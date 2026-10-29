using L = AlphaIsland.PluginSdk.Localization.UiText;
using AlphaIsland.PluginSdk;
using Windows.Gaming.Input;
using Windows.System.Power;

namespace AlphaIsland.Plugins.DeviceBattery;

internal static class BatteryScanner
{
    internal static async Task<IReadOnlyList<PluginDeviceBattery>> ReadAsync(CancellationToken token, IReadOnlyList<HidInterface>? inventory = null)
    {
        var controllers = ReadControllers().ToList();
        var groups = (inventory ?? HidInventory.Enumerate()).GroupBy(d => d.PhysicalId).ToArray();
        // XInput exposes no VID/PID. Bind identity only for one unambiguous Xbox-compatible
        // HID controller and one XInput slot; otherwise keep the independent identities.
        var xboxHid = groups.Where(g => g.Any(d => d.Vid == 0x045E && d.Pid == 0x028E
            && d.Caps.UsagePage == 1 && d.Caps.Usage == 5)).ToArray();
        if (xboxHid.Length == 1 && controllers.Count == 1 && controllers[0].Vid == 0
            && controllers[0].Battery.Id.StartsWith("xinput/", StringComparison.Ordinal))
        {
            var hid = xboxHid[0].First(d => d.Caps.UsagePage == 1 && d.Caps.Usage == 5);
            controllers[0] = new(hid.Vid, hid.Pid, controllers[0].Battery with
                { Name = hid.Name.Length > 0 ? hid.Name : controllers[0].Battery.Name, PhysicalId = hid.PhysicalId });
        }
        using var parallel = new SemaphoreSlim(2);
        var readings = await Task.WhenAll(groups.Select(async group =>
        {
            await parallel.WaitAsync(token);
            try { return await ReadGroupAsync(group); }
            finally { parallel.Release(); }
        }));
        return readings.SelectMany(reading => reading).Concat(controllers.Select(c => c.Battery)).ToArray();

        async Task<IReadOnlyList<PluginDeviceBattery>> ReadGroupAsync(IGrouping<string, HidInterface> group)
        {
            token.ThrowIfCancellationRequested(); var result = new List<PluginDeviceBattery>();
            var items = group.ToArray(); var primary = items.OrderByDescending(d => d.Name.Length).First();
            if (items.FirstOrDefault(MchoseBatteryReader.Supports) is { } mchose)
            {
                try
                {
                    if (await MchoseBatteryReader.ReadAsync(mchose, token) is { } battery) { result.Add(battery); return result; }
                }
                catch (Exception ex) when (ex is not OperationCanceledException) { }
            }
            if (items.FirstOrDefault(FlydigiBatteryReader.Supports) is { } flydigi)
            {
                try
                {
                    if (await FlydigiBatteryReader.ReadAsync(flydigi, token) is { } battery)
                    {
                        lock (controllers)
                        {
                        var matches = controllers.Where(c => c.Vid == flydigi.Vid && c.Pid == flydigi.Pid).ToArray();
                        // Merge only an unambiguous single physical controller. Never merge two pads by model name.
                        if (matches.Length == 1 && groups.Count(g => g.Any(d => d.Vid == flydigi.Vid && d.Pid == flydigi.Pid)) == 1)
                        {
                            var prior = matches[0]; int index = controllers.IndexOf(prior);
                            controllers[index] = prior with { Battery = battery with { Id = prior.Battery.Id, PhysicalId = prior.Battery.PhysicalId } };
                        }
                        else result.Add(battery);
                        }
                        return result;
                    }
                }
                catch (Exception ex) when (ex is not OperationCanceledException) { }
            }
            HidInterface? query = items.FirstOrDefault(d => d.Vid == 0x373B && d.Pid == 0x1216 && d.Caps.UsagePage == 0xff05 && d.Caps.OutputBytes == 64)
                ?? items.FirstOrDefault(d => d.Vid == 0x3654 && d.Pid == 0x6901 && d.Caps.UsagePage == 0xff02 && d.Caps.OutputBytes == 65)
                ?? items.FirstOrDefault(IsLogitechQueryInterface);
            if (query != null)
            {
                try
                {
                    if (query.Vid == 0x046D)
                    {
                        var logi = await BatteryProtocols.ReadLogitech(query, token);
                        if (logi.Count > 0) { result.AddRange(logi); return result; }
                    }
                    else
                    {
                        using var session = new HidSession(query); int? percent;
                        if (query.Vid == 0x373B)
                        {
                            byte[] request = new byte[64]; new byte[] {8,0x7D,0x72,2,0,1,7,1}.CopyTo(request,0);
                            var reply = await session.Query(request, r => BatteryProtocols.ParseAtk(r).HasValue, token);
                            percent = reply == null ? null : BatteryProtocols.ParseAtk(reply);
                        }
                        else
                        {
                            byte sequence = (byte)Environment.TickCount;
                            var reply = await session.Query(BatteryProtocols.DarkrimQuery(sequence), r => BatteryProtocols.ParseDarkrim(r,sequence).HasValue, token);
                            percent = reply == null ? null : BatteryProtocols.ParseDarkrim(reply,sequence);
                        }
                        result.Add(new(group.Key, query.Vid == 0x373B ? L.T("ATK / VXE 无线鼠标") : "DARKRIM SHADOW", DeviceBatteryTransport.WirelessReceiver,
                            query.Vid == 0x373B ? DeviceBatteryKind.Mouse : DeviceBatteryKind.Audio, percent, true,
                            Detail: percent.HasValue ? L.T("设备协议上报电量") : L.T("接收器在线；外设休眠、未连接或尚未上报电量"), PhysicalId: group.Key)); return result;
                    }
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                { result.Add(Receiver(primary, L.T("接收器在线，暂时无法读取：") + ex.Message)); return result; }
                result.Add(Receiver(primary, L.T("接收器在线；等待外设上报"))); return result;
            }
            int? standard = null;
            foreach (var item in items) { standard = HidInventory.ReadStandardBattery(item); if (standard.HasValue) break; }
            bool gamepad = items.Any(d => d.Caps.UsagePage == 1 && d.Caps.Usage is 4 or 5);
            lock (controllers) if (gamepad && controllers.Any(c => c.Vid == primary.Vid && c.Pid == primary.Pid)) return result;
            bool inputPeripheral = primary.Name.Length > 3 && !primary.Name.Equals("HIDI2C Device", StringComparison.OrdinalIgnoreCase) && items.Any(d => d.Caps.UsagePage >= 0xff00)
                && items.Any(d => d.Caps.UsagePage == 1 && d.Caps.Usage is 2 or 6);
            if (standard.HasValue || gamepad || inputPeripheral)
                result.Add(new(group.Key, primary.Name.Length > 0 ? primary.Name : gamepad ? L.T("手柄 / 接收器") : L.T("HID 电池设备"),
                    DeviceBatteryTransport.UsbOrReceiver, KindFor(items),
                    standard, true, Detail: standard.HasValue ? L.T("标准 HID 电池字段") : L.T("设备在线，驱动未提供电量"), PhysicalId: group.Key));
            return result;
        }
    }
    internal static bool IsLogitechQueryInterface(HidInterface device) => device.Vid == 0x046D
        && device.Pid is 0xC539 or 0xC547 or 0xC53F or 0xC52B or 0xC548 or 0xC545 or 0xC543 or 0xC546
        && device.Caps.UsagePage == 0xFF00 && device.Caps.Usage == 2 && device.Caps.InputBytes >= 20 && device.Caps.OutputBytes == 20;

    internal static DeviceBatteryKind KindFor(IEnumerable<HidInterface> interfaces)
    {
        var items = interfaces.ToArray();
        if (items.Any(d => d.Caps.UsagePage == 1 && d.Caps.Usage is 4 or 5)) return DeviceBatteryKind.Gamepad;
        // Gaming mice also expose a keyboard collection for programmable keys.
        if (items.Any(d => d.Caps.UsagePage == 1 && d.Caps.Usage == 2)) return DeviceBatteryKind.Mouse;
        return items.Any(d => d.Caps.UsagePage == 1 && d.Caps.Usage == 6) ? DeviceBatteryKind.Keyboard : DeviceBatteryKind.Other;
    }
    private static PluginDeviceBattery Receiver(HidInterface device, string detail) => new(device.PhysicalId + "/receiver",
        device.Vid == 0x046D ? L.T("罗技 LIGHTSPEED 接收器") : device.Name.Length > 0 ? device.Name : L.T("2.4G 接收器"),
        DeviceBatteryTransport.WirelessReceiver, device.Vid == 0x046D && device.Pid == 0xC539 ? DeviceBatteryKind.Mouse : DeviceBatteryKind.Other,
        null, true, Detail: detail, PhysicalId: device.PhysicalId + "/receiver");

    private sealed record Controller(ushort Vid, ushort Pid, PluginDeviceBattery Battery);
    private static IReadOnlyList<Controller> ReadControllers()
    {
        var result = new List<Controller>();
        try
        {
            foreach (var controller in RawGameController.RawGameControllers.ToArray())
            {
                try
                {
                    var report = controller.TryGetBatteryReport();
                    int? percent = null; string level = "", detail = L.T("手柄在线，驱动未提供电量"); bool charging = false;
                    if (report != null && report.Status != BatteryStatus.NotPresent)
                    {
                        charging = report.Status == BatteryStatus.Charging;
                        if (report.FullChargeCapacityInMilliwattHours is int full && full > 0 && report.RemainingCapacityInMilliwattHours is int remaining && remaining >= 0 && remaining <= full)
                        {
                            if (full >= 100) { percent = (int)Math.Round(remaining * 100d / full); detail = L.T("Windows 手柄电池容量报告"); }
                            else { level = remaining == 0 ? L.T("极低") : remaining * 3 <= full ? L.T("低") : remaining * 3 <= full * 2 ? L.T("中") : L.T("充足"); detail = L.T("Windows 手柄电量档位，非精确百分比"); }
                        }
                    }
                    string id = controller.NonRoamableId;
                    if (string.IsNullOrEmpty(id)) id = $"{controller.HardwareVendorId:X4}:{controller.HardwareProductId:X4}/{result.Count}";
                    result.Add(new(controller.HardwareVendorId, controller.HardwareProductId, new(id, string.IsNullOrWhiteSpace(controller.DisplayName) ? L.T("游戏手柄") : controller.DisplayName,
                        DeviceBatteryTransport.UsbOrReceiver, DeviceBatteryKind.Gamepad, percent, true, charging, detail, "controller/" + id, level)));
                }
                catch (Exception) { }
            }
        }
        catch (Exception) { }
        try
        {
            var xinput = XInputBatteryReader.Read();
            if (result.Count == 0) result.AddRange(xinput.Select(d=>new Controller(0,0,d)));
            else if (result.Count == 1 && xinput.Count == 1 && result[0].Battery.Percent == null && result[0].Battery.Level.Length == 0)
                result[0] = result[0] with { Battery = result[0].Battery with { Level=xinput[0].Level, Detail=xinput[0].Detail, Transport=xinput[0].Transport } };
        }
        catch (Exception) { }
        return result;
    }
}

using AlphaIsland.Hardware;
using System.Diagnostics;
using System.IO.Pipes;

// The worker only writes sensor readings to a parent-created, current-user pipe.
// No installation, file operations, privileged commands or persistent service are exposed.
if (args.Length != 3 || !args[0].StartsWith("AlphaIsland.Hardware.", StringComparison.Ordinal) ||
    !Guid.TryParseExact(args[0]["AlphaIsland.Hardware.".Length..], "N", out _) ||
    !int.TryParse(args[1], out int parentId) || !long.TryParse(args[2], out long started)) return 2;
try
{
    using var parent = Process.GetProcessById(parentId);
    if (parent.StartTime.ToUniversalTime().Ticks != started) return 2;
    using var pipe = HardwarePowerPipe.CreateWriter(args[0]);
    using var connect = new CancellationTokenSource(TimeSpan.FromSeconds(30));
    await pipe.ConnectAsync(connect.Token);
    HardwarePowerPipe.ValidateReader(pipe, parentId);
    using var hardware = new LibreHardwarePowerSource();
    using var timer = new PeriodicTimer(TimeSpan.FromSeconds(2));
    do
    {
        if (parent.HasExited || parent.StartTime.ToUniversalTime().Ticks != started) break;
        var reading = hardware.Sample() with { Source = "Alpha Island 独立硬件读取进程" };
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        await HardwarePowerProtocol.WriteAsync(pipe, reading, deadline.Token);
    } while (await timer.WaitForNextTickAsync());
    return 0;
}
catch (Exception) { return 1; }


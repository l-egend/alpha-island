using System.Windows.Controls;
using AlphaIsland.PluginSdk;

namespace AlphaIsland.PluginFixture;

public sealed class TextInputProbePlugin : IIslandPlugin, IPluginSuspendGuard
{
    public bool CanSuspend => true;
    public void Initialize(IPluginContext context)
    {
        context.RegisterPage(new("chat", "Text input probe", () => new InputView()));
        context.RegisterAction(new("text-ping", "Ping", () => context.ReportDeviceBatteries([
            new("probe", "IPC probe", DeviceBatteryTransport.UsbOrReceiver, DeviceBatteryKind.Other, 7, true)])));
    }
    private sealed class InputView : StackPanel, IPluginTextInput
    {
        public async Task<PluginTextInputResult> SubmitTextAsync(string requestId, string text, CancellationToken cancellationToken)
        {
            if (text == "wait-throw") await Task.Delay(Timeout.Infinite, cancellationToken);
            try { if (text == "wait") await Task.Delay(Timeout.Infinite, cancellationToken); return new(true, text); }
            catch (OperationCanceledException) { return new(true, "cancelled"); }
        }
    }
    public void Dispose() { }
}

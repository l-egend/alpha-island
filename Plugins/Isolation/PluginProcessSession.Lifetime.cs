using System.IO;

namespace alpha_island.Plugins.Isolation;
internal sealed partial class PluginProcessSession
{
    internal Task Completion { get; private set; } = Task.CompletedTask;
    private async Task StopWorkerAsync()
    {
        try
        {
            if (_connected && IsAlive && !_lifetime.IsCancellationRequested)
            {
                using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(1));
                await PluginWire.Write(_pipe, new(0, "shutdown", PluginWire.Element(true)), _write, deadline.Token).ConfigureAwait(false);
                if (_process != null) await _process.WaitForExitAsync(deadline.Token).ConfigureAwait(false);
            }
        }
        catch (Exception ex) when (ex is IOException or OperationCanceledException or InvalidOperationException or System.ComponentModel.Win32Exception) { }
        finally
        {
            _lifetime.Cancel(); _pipe.Dispose(); _job?.Dispose();
            try
            {
                if (_process?.HasExited == false) _process.Kill(entireProcessTree: true);
                _process?.WaitForExit(1000);
            }
            catch (Exception ex) when (ex is InvalidOperationException or System.ComponentModel.Win32Exception) { }
            _process?.Dispose();
        }
    }
}

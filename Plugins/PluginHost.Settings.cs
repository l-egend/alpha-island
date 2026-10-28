using System.Text.Json;
using alpha_island.Core;
using alpha_island.Plugins.Isolation;

namespace alpha_island.Plugins;

internal sealed partial class PluginHost
{
    private readonly HashSet<SettingsSession> _settingsSessions = new();
    private readonly List<Task> _settingsTeardowns = new();

    private Task CloseSettingsSessions(Entry entry)
    {
        var sessions = _settingsSessions.Where(session => ReferenceEquals(session.Entry, entry)).ToArray();
        foreach (var session in sessions) session.Dispose();
        return Task.WhenAll(sessions.Select(session => session.ShutdownCompletion));
    }

    internal SettingsSession OpenSettingsSession(Entry entry)
    {
        _dispatcher.VerifyAccess();
        if (_disposed || !_entries.Contains(entry) || entry.Manifest == null || entry.UninstallPending)
            throw new InvalidOperationException("插件当前不可配置");
        var session = new SettingsSession(this, entry);
        _settingsSessions.Add(session);
        return session;
    }

    internal sealed class SettingsSession : IDisposable
    {
        private readonly PluginHost _host;
        private readonly IsolatedPlugin _proxy;
        private bool _disposed;
        internal Entry Entry { get; }
        internal Context Context { get; }
        internal bool Loading { get; private set; } = true;
        internal string Error { get; private set; } = "";
        internal bool Ready => !_disposed && !Loading && Error.Length == 0 && _proxy.IsAlive;
        internal bool IsDisposed => _disposed;
        internal int ProcessId => _proxy.ProcessId;
        internal Task ShutdownCompletion => _proxy.ShutdownCompletion;
        internal event Action? Changed;

        internal SettingsSession(PluginHost host, Entry entry)
        {
            _host = host; Entry = entry; Context = new Context(host, entry);
            _proxy = new IsolatedPlugin(host._dispatcher,
                () => host._options.Extras.Where(pair => pair.Value.ValueKind != JsonValueKind.Undefined)
                    .ToDictionary(pair => pair.Key, pair => pair.Value.Clone()), SetError);
        }

        internal async Task InitializeAsync()
        {
            try
            {
                await Entry.Teardown.ConfigureAwait(false);
                await _host._dispatcher.InvokeAsync(() => _disposed ? Task.CompletedTask
                    : _proxy.InitializeAsync(Context, Entry.Manifest!, Entry.Directory, settingsOnly: true)).Task.Unwrap();
                await _host._dispatcher.InvokeAsync(() =>
                {
                    if (_disposed) return;
                    Context.Sealed = true; Loading = false; Changed?.Invoke();
                });
            }
            catch (Exception error)
            {
                if (!_host._dispatcher.HasShutdownStarted)
                    await _host._dispatcher.InvokeAsync(() => SetError(error));
            }
        }

        private void SetError(Exception error)
        {
            if (_disposed) return;
            Error = DiagnosticRedactor.Redact(error.GetBaseException().Message);
            Loading = false; _proxy.Dispose(); Changed?.Invoke();
        }

        internal void Synchronize() { if (Ready) _proxy.OnSettingsChanged(); }

        public void Dispose()
        {
            if (_disposed) return;
            _disposed = true; Changed = null;
            Context.Cancellation.Cancel(); _proxy.Dispose(); Context.Cancellation.Dispose();
            _host._settingsSessions.Remove(this);
            _host._settingsTeardowns.RemoveAll(task => task.IsCompleted);
            _host._settingsTeardowns.Add(_proxy.ShutdownCompletion);
        }
    }
}

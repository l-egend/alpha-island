using System.Windows;
using System.Windows.Threading;
using AlphaIsland.PluginSdk;
using alpha_island.Core;
internal sealed class ConversationPluginFixture(IslandOptions options,string root) : IPluginContext
{
    public string PluginId => "ai.api-chat";
    public string PluginDirectory=>root;
    public string DataDirectory=>root;
    public string ConfigDirectory=>System.IO.Path.Combine(root,"config");
    public Dispatcher Dispatcher=>Dispatcher.CurrentDispatcher;
    public CancellationToken Lifetime=>CancellationToken.None;
    internal Action? RefreshRequested;
    internal Action<string>? PageRequested;
    internal Func<string, DesktopContextSnapshot>? ContextRequested;
    internal IToolDispatcher ToolDispatcher = UnavailableTools.Instance;
    public IToolDispatcher Tools => ToolDispatcher;
    public ValueTask<DesktopContextSnapshot> GetDesktopContextAsync(string prompt, CancellationToken cancellationToken = default)
        => ValueTask.FromResult(ContextRequested?.Invoke(prompt) ?? DesktopContextSnapshot.Empty);
    public void RegisterPage(PluginPage page){}
    public void RegisterSetting(PluginSetting setting){}
    public void RegisterService(PluginService service){}
    public T GetSetting<T>(string key,T fallback)=>fallback;
    public T ReadConfiguration<T>(string key,T fallback)
    {try{return options.Extras.TryGetValue(key,out var value)?System.Text.Json.JsonSerializer.Deserialize<T>(value)!:fallback;}catch{return fallback;}}
    public void WriteConfiguration(string key,object value)=>SettingsRegistry.SetExtra(options,key,value);
    public void OpenPage(string key)=>PageRequested?.Invoke(key);
    public void Refresh()=>RefreshRequested?.Invoke();
    public IModelAvatar CreateModelAvatar() => new alpha_island.Plugins.PluginModelAvatar(true);
    public void Log(string message){}
}

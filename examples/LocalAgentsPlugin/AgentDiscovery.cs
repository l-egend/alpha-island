using System.IO;
using System.Text.Json;
using System.Text.RegularExpressions;
namespace AlphaIsland.Plugins.LocalAgents;
internal sealed record LocalAgent(string Id, string Name, string Executable, string Entry = "", string Home = "")
{ public override string ToString() => Name; }
internal static class AgentDiscovery
{
    internal static IReadOnlyList<LocalAgent> Find(string configDirectory)
    {
        var found = new List<LocalAgent>();
        string local = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        string user = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        string? codex = OnPath("codex.exe") ?? Children(Path.Combine(local,"OpenAI","Codex","bin"), "codex.exe");
        if (codex != null) found.Add(new("codex","Codex",codex));
        string? agy = OnPath("agy.exe") ?? Existing(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles),"Google","antigravity-cli","agy.exe"))
            ?? Existing(Path.Combine(local,"Programs","antigravity-cli","agy.exe")) ?? Existing(Path.Combine(user,".local","bin","agy.exe"));
        if (agy != null) found.Add(new("antigravity","Antigravity CLI",agy));
        string? node = Existing(Path.Combine(AppContext.BaseDirectory,"agent-integrations","runtime","node.exe")) ?? OnPath("node.exe");
        string? entry = null; string? wrapper = OnPath("dsh.cmd");
        if (wrapper != null)
        {
            try { var match = Regex.Match(File.ReadAllText(wrapper), "(?:node|node.exe)\\s+\"([^\"]+[/\\\\](?:bin|cli)\\.js)\"", RegexOptions.IgnoreCase); if (match.Success) entry = Existing(match.Groups[1].Value); }
            catch (Exception ex) when(ex is IOException or UnauthorizedAccessException) { }
        }
        entry ??= CachedHarness(local);
        string home = Environment.GetEnvironmentVariable("DSH_HOME") ?? Path.Combine(user,".dsh");
        try
        {
            using var registration = JsonDocument.Parse(File.ReadAllText(Path.Combine(configDirectory,"deepseek-harness.json")));
            if (registration.RootElement.TryGetProperty("home",out var value) && value.GetString() is { } path && Directory.Exists(path)) home = path;
        }
        catch (Exception ex) when (ex is IOException or JsonException or UnauthorizedAccessException) { }
        if (node != null && entry != null) found.Add(new("dsh","DeepSeek Harness",node,entry,home));
        return found;
    }
    private static string? CachedHarness(string local)
    {
        try
        {
            foreach (var folder in Directory.EnumerateDirectories(Path.Combine(local,"npm-cache","_npx")).OrderByDescending(Directory.GetLastWriteTimeUtc).Take(64))
                if (Existing(Path.Combine(folder,"node_modules","@deepseek-ai","dsh","lib","bin.js")) is { } entry) return entry;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
        return null;
    }
    private static string? Children(string root,string name)
    {
        try { return Directory.EnumerateDirectories(root).OrderByDescending(Directory.GetLastWriteTimeUtc).Select(p=>Existing(Path.Combine(p,name))).FirstOrDefault(p=>p!=null); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { return null; }
    }
    internal static string? OnPath(string name)
    {
        foreach(string folder in (Environment.GetEnvironmentVariable("PATH")??"").Split(';'))
        {
            try { if(folder.Length!=0 && Existing(Path.Combine(folder.Trim().Trim('"'),name)) is {} path)return path; }
            catch(Exception ex) when(ex is ArgumentException or NotSupportedException or IOException) { }
        }
        return null;
    }
    private static string? Existing(string path) => File.Exists(path) ? Path.GetFullPath(path) : null;
}

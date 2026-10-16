using CommunityToolkit.Mvvm.ComponentModel;
using alpha_island.Core;

namespace alpha_island.ViewModels;

internal sealed class AgentSessionViewModel(string key, string owner, string source, int number) : ObservableObject
{
    internal string Key { get; } = key;
    internal string Owner { get; } = owner;
    internal string Source { get; } = source;
    internal int Number { get; } = number;
    private AgentActivity? _activity;
    internal AgentActivity? Activity
    {
        get => _activity;
        set { if (SetProperty(ref _activity, value) && value?.ArtworkFamily is { Length: > 0 } family) ArtworkFamily = family; }
    }
    internal string ArtworkFamily { get; set; } = "";
}
internal sealed class AgentSessionsViewModel
{
    private readonly Dictionary<string, AgentSessionViewModel> _sessions = new(StringComparer.Ordinal);
    private readonly Dictionary<string, int> _sourceNumbers = new(StringComparer.Ordinal);
    private readonly Dictionary<string, int> _ownerNumbers = new(StringComparer.Ordinal);
    private readonly Queue<string> _owners = new();
    internal IReadOnlyCollection<AgentSessionViewModel> Sessions => _sessions.Values;
    internal AgentSessionViewModel GetOrCreate(AgentActivity activity, string primaryKey)
    {
        if (_sessions.TryGetValue(activity.Owner, out var existing)) return existing;
        string source = activity.Owner.StartsWith("external:", StringComparison.Ordinal) ? activity.Owner[9..].Split('/')[0] : "internal";
        if (!_ownerNumbers.TryGetValue(activity.Owner, out int number))
        {
            number = _sourceNumbers.GetValueOrDefault(source) + 1;
            _sourceNumbers[source] = number; _ownerNumbers[activity.Owner] = number; _owners.Enqueue(activity.Owner);
            while (_owners.Count > 2048) _ownerNumbers.Remove(_owners.Dequeue());
        }
        string key = _sessions.Values.Any(session => session.Key == primaryKey) ? "agentstatus:" + Convert.ToHexString(
            System.Security.Cryptography.SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(activity.Owner)))[..20].ToLowerInvariant() : primaryKey;
        var session = new AgentSessionViewModel(key, activity.Owner, source, number) { Activity = activity };
        _sessions.Add(activity.Owner, session); return session;
    }
    internal void Remove(string owner) => _sessions.Remove(owner);
}

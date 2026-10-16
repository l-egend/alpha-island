namespace alpha_island.Core;

internal enum LauncherKind { Application, Component, Agent, AiPrompt }
internal sealed record LauncherEntry(string Id, string Title, string Detail, LauncherKind Kind, string Target);
internal static class LauncherSearch
{
    internal static LauncherEntry[] Search(IEnumerable<LauncherEntry> entries, string query, int maximum = 30)
    {
        query = query.Trim();
        var words = query.Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        int Score(LauncherEntry item)
        {
            if (query.Length == 0) return item.Kind == LauncherKind.Application ? 1 : 20;
            if (!words.All(word => (item.Title + " " + item.Detail).Contains(word, StringComparison.OrdinalIgnoreCase))) return -1;
            return item.Title.Equals(query, StringComparison.OrdinalIgnoreCase) ? 200 : item.Title.StartsWith(query, StringComparison.OrdinalIgnoreCase) ? 120
                : item.Title.Contains(query, StringComparison.OrdinalIgnoreCase) ? 80 : 30;
        }
        return entries.GroupBy(item => item.Id, StringComparer.OrdinalIgnoreCase).Select(group => group.First())
            .Select(item => (Item: item, Score: Score(item))).Where(pair => pair.Score >= 0)
            .OrderByDescending(pair => pair.Score).ThenBy(pair => pair.Item.Title, StringComparer.CurrentCultureIgnoreCase)
            .Take(Math.Clamp(maximum, 1, 100)).Select(pair => pair.Item).ToArray();
    }
}

namespace Hhub.Core;

public sealed record DiagnosticEntry(string PluginId, PluginNotice Notice, bool Active, DateTimeOffset ChangedAt);

// A missing/stale frame is not recovery. Call Observe only for a complete, fresh snapshot.
public sealed class DiagnosticJournal
{
    private const int MaxEntriesPerPlugin = 64;
    private readonly Dictionary<string, Dictionary<string, DiagnosticEntry>> _entries = [];
    public IReadOnlyList<DiagnosticEntry> ForPlugin(string id) => _entries.TryGetValue(id, out var entries)
        ? entries.Values.OrderByDescending(entry => entry.Active).ThenBy(entry => entry.Notice.Id == "monitor.status" ? 0 : 1)
            .ThenBy(entry => entry.Notice.Id, StringComparer.Ordinal).ToArray()
        : [];

    public void Observe(string pluginId, IEnumerable<PluginNotice> notices, DateTimeOffset now)
    {
        var current = new Dictionary<string, PluginNotice>(StringComparer.Ordinal);
        foreach (var notice in notices)
        {
            current.TryAdd(notice.Id, notice);
            if (current.Count == MaxEntriesPerPlugin) break;
        }
        if (!_entries.TryGetValue(pluginId, out var entries)) _entries[pluginId] = entries = new(StringComparer.Ordinal);
        foreach (string key in entries.Keys.ToArray())
        {
            var previous = entries[key];
            if (!current.ContainsKey(key) && previous.Active)
                entries[key] = previous with { Active = false, ChangedAt = now };
        }
        foreach (var notice in current.Values)
        {
            if (!entries.TryGetValue(notice.Id, out var previous) || previous.Notice != notice || !previous.Active)
                entries[notice.Id] = new(pluginId, notice, true, now);
        }
        if (entries.Count > MaxEntriesPerPlugin)
            foreach (var entry in entries.Where(pair => !pair.Value.Active).OrderBy(pair => pair.Value.ChangedAt)
                .Take(entries.Count - MaxEntriesPerPlugin))
                entries.Remove(entry.Key);
    }

    public void Forget(string pluginId) => _entries.Remove(pluginId);
}

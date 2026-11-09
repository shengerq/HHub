namespace Hhub.Core;

public sealed record TrayBatteryReading(string InstanceId, string Name, double? Battery);

public sealed class TrayBatteryCache
{
    private sealed record Reading(string PluginId, string Name, double Value, DateTimeOffset Received, DateTimeOffset Expires);
    private readonly Dictionary<string, Reading> _readings = new(StringComparer.OrdinalIgnoreCase);

    public void Prune(IEnumerable<DeviceRecord> devices)
    {
        var ids = devices.Select(device => device.InstanceId).ToHashSet(StringComparer.OrdinalIgnoreCase);
        foreach (string removed in _readings.Keys.Where(id => !ids.Contains(id)).ToArray()) _readings.Remove(removed);
    }

    public void Observe(InstalledPlugin plugin, IEnumerable<DeviceRecord> devices, SnapshotFrame frame)
    {
        foreach (var device in devices.Where(device => PluginPolicies.MatchesDevice(plugin.Manifest, device)))
        {
            if (_readings.TryGetValue(device.InstanceId, out var newer) && newer.Received > frame.ReceivedAtUtc) continue;
            var metadata = DeviceMetadata.Resolve(plugin, device, frame.Values, fresh: true);
            if (metadata.Battery is { } battery)
                _readings[device.InstanceId] = new(plugin.Manifest.Id, metadata.Name, battery, frame.ReceivedAtUtc,
                    frame.ReceivedAtUtc.AddSeconds(plugin.Manifest.BatteryTelemetry?.CacheSeconds ?? 120));
            else if (_readings.TryGetValue(device.InstanceId, out var old) && old.PluginId == plugin.Manifest.Id)
                _readings.Remove(device.InstanceId);
        }
    }

    public IReadOnlyList<TrayBatteryReading> Read(IEnumerable<DeviceRecord> devices, IReadOnlySet<string> enabledPlugins,
        Func<DeviceRecord, string> name, DateTimeOffset now)
    {
        var connected = devices.ToArray();
        Prune(connected);
        return connected.Select(device =>
        {
            bool valid = _readings.TryGetValue(device.InstanceId, out var reading) && reading.Expires > now && enabledPlugins.Contains(reading.PluginId);
            return new TrayBatteryReading(device.InstanceId, valid ? reading!.Name : name(device), valid ? reading!.Value : null);
        }).ToArray();
    }
}

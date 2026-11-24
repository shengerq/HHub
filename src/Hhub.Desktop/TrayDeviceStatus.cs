using System.Diagnostics;
using System.Text.Json;
using Hhub.PluginContracts;

namespace Hhub.Desktop;

internal static class TrayDeviceStatus
{
    public static async Task PublishAsync(PluginLab lab)
    {
        var (_, definitions) = await lab.DiscoverManifestsAsync();
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(14));
        var token = deadline.Token;
        foreach (var candidate in definitions.Where(candidate => candidate.Definition.Kind == "device" && PluginActivation.Enabled(candidate.Definition.Id)))
        {
            if (token.IsCancellationRequested) break;
            var definition = candidate.Definition;
            var capabilities = Capabilities(definition);
            bool hasBattery = definition.Permissions.Contains("device.battery.read") && capabilities.Contains(DeviceOverview.BatteryPercent);
            bool hasVariables = capabilities.Contains(DeviceOverview.TrayVariables);
            if (!hasBattery && !hasVariables) continue;
            try
            {
                var (store, plugins) = await lab.RegisterAsync(item => item.Id == definition.Id).WaitAsync(token);
                await using var session = await lab.StartSessionAsync(store, plugins.Single(), token,
                    TimeSpan.FromSeconds(3), overviewOnly: true);
                var entries = new Dictionary<string, string>(StringComparer.Ordinal);
                var superseded = new HashSet<string>(StringComparer.Ordinal);
                foreach (var target in session.Targets.Where(target => target.Online && !target.Simulated))
                {
                    if (token.IsCancellationRequested || entries.Count >= 24) break;
                    if (superseded.Contains(target.Id)) continue;
                    var values = (await session.ReadAsync(target.Id, token)).Values;
                    if (values is null) continue;
                    entries[target.Id] = Row(target.Id, target.Name, definition, capabilities, values);
                    if (capabilities.Contains(DeviceOverview.AlternateConnections)
                        && values.TryGetValue(DeviceOverview.AlternateConnections, out var links) && links.ValueKind == JsonValueKind.Array)
                        foreach (var link in links.EnumerateArray())
                            if (link.ValueKind == JsonValueKind.String && link.GetString() is { } other && other != target.Id)
                                superseded.Add(other);
                }
                if (!token.IsCancellationRequested)
                    TrayBattery.PublishStatus(definition.Id, entries.Where(entry => !superseded.Contains(entry.Key)).Select(entry => entry.Value));
            }
            catch (OperationCanceledException) when (token.IsCancellationRequested) { break; }
            catch (Exception error) { Trace.WriteLine(error); }
        }
    }

    internal static HashSet<string> Capabilities(PluginDefinition definition) => definition.Provides
        .Where(service => service.Contract == DeviceOverview.Service
            && Version.TryParse(service.Version, out var version) && version.Major == 1)
        .SelectMany(service => service.CapabilityIds).ToHashSet(StringComparer.Ordinal);

    internal static string Row(string target, string name, PluginDefinition definition, HashSet<string> capabilities,
        IReadOnlyDictionary<string, JsonElement> values)
    {
        string battery = "";
        if (definition.Permissions.Contains("device.battery.read") && capabilities.Contains(DeviceOverview.BatteryPercent)
            && values.TryGetValue(DeviceOverview.BatteryPercent, out var percent)
            && percent.ValueKind == JsonValueKind.Array && percent.GetArrayLength() == 1
            && percent[0].ValueKind == JsonValueKind.Number && percent[0].TryGetInt32(out int number)
            && number is >= 0 and <= 100)
        {
            bool charging = capabilities.Contains(DeviceOverview.BatteryCharging)
                && values.TryGetValue(DeviceOverview.BatteryCharging, out var state)
                && state.ValueKind == JsonValueKind.Array && state.GetArrayLength() == 1 && state[0].ValueKind == JsonValueKind.True;
            battery = $"{number}%" + (charging ? "（充电中）" : "");
        }
        string variables = capabilities.Contains(DeviceOverview.TrayVariables) && values.TryGetValue(DeviceOverview.TrayVariables, out var raw)
            ? FormatVariables(raw) : "";
        string id = Clean(target, 256);
        return battery.Length > 0 || variables.Length > 0 ? string.Join('\t', id, Clean(name, 96), battery, variables) : id;
    }

    private static string FormatVariables(JsonElement raw)
    {
        if (raw.ValueKind != JsonValueKind.Array) return "";
        var fields = new List<string>();
        foreach (var entry in raw.EnumerateArray().Take(4))
        {
            if (entry.ValueKind != JsonValueKind.Object || !entry.TryGetProperty("label", out var label)
                || label.ValueKind != JsonValueKind.String || !entry.TryGetProperty("value", out var value)
                || value.ValueKind != JsonValueKind.String) continue;
            string name = Clean(label.GetString()!, 24);
            string text = Clean(value.GetString()!, 48);
            if (name.Length > 0 && text.Length > 0) fields.Add($"{name} {text}");
        }
        return string.Join(" · ", fields);
    }

    private static string Clean(string value, int limit) =>
        new string(value.Take(limit).Select(character => char.IsControl(character) ? ' ' : character).ToArray()).Trim();
}

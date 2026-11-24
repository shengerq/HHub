using System.Text.Json;

namespace Hhub.Core;

public sealed record DeviceMetadata(string Name, string Connection, double? Battery, string? ImagePath, string? Number = null)
{
    public string? Category { get; init; }
    public DeviceOverviewState? Overview { get; init; }
    public static DeviceMetadata Resolve(InstalledPlugin plugin, DeviceRecord device,
        IReadOnlyDictionary<string, JsonElement> snapshot, bool fresh)
    {
        var presentation = plugin.Manifest.DevicePresentations.FirstOrDefault(p =>
            p.Vid.Equals(device.Vid, StringComparison.OrdinalIgnoreCase) && p.Pid.Equals(device.Pid, StringComparison.OrdinalIgnoreCase));
        var declaration = plugin.Manifest.DeviceOverview;
        string name = presentation?.Name ?? declaration?.Name ?? device.Name;
        string connection = declaration?.Connection ?? device.Connection;
        string? image = presentation?.Image; double? battery = null;
        string number = DeviceNumber(device);
        string? category = presentation?.Category ?? declaration?.Category;
        DeviceOverviewState? overview = null;
        if (snapshot.TryGetValue("devices", out var devices) && devices.ValueKind == JsonValueKind.Array)
        {
            foreach (var item in devices.EnumerateArray())
            {
                if (item.ValueKind != JsonValueKind.Object || Read(item, "instanceId") is not { } id || !device.ContainsInstance(id)) continue;
                name = Read(item, "name") ?? name; connection = Read(item, "connection") ?? connection; image = Read(item, "image") ?? image;
                if (Read(item, "number") is { Length: <= 256 } providedNumber) number = providedNumber;
                category = Read(item, "category") ?? category;
                if (fresh && item.TryGetProperty("battery", out var value) && value.ValueKind == JsonValueKind.Number
                    && value.TryGetDouble(out var batteryLevel) && double.IsFinite(batteryLevel) && batteryLevel is >= 0 and <= 100) battery = batteryLevel;
                if (fresh && declaration is not null)
                    overview = new(ReadRows(item), Read(item, "currentConfiguration"),
                        declaration.ApplyCommand is not null && declaration.IntegrationOptionsKey is null
                            ? DeviceOverviewState.Options(item.TryGetProperty("configurations", out var options) ? options : default) : []);
                break;
            }
        }
        string? path = null;
        if (image is not null)
        {
            try
            {
                string candidate = PluginCatalog.ResolveInside(plugin.Directory, image);
                if (SupportedImage(candidate) && File.Exists(candidate)) path = candidate;
            }
            catch (Exception ex) when (ex is InvalidDataException or ArgumentException or IOException) { }
        }
        return new(name, connection, battery, path, number) { Category = category, Overview = overview };
    }
    private static DeviceOverviewRow[] ReadRows(JsonElement item) =>
        item.TryGetProperty("overview", out var rows) && rows.ValueKind == JsonValueKind.Array
            ? rows.EnumerateArray().Take(24).Where(row => row.ValueKind == JsonValueKind.Object)
                .Select(row => (Label: Read(row, "label"), Value: Read(row, "value")))
                .Where(row => row.Label is { Length: <= 40 } && row.Value is { Length: <= 128 })
                .Select(row => new DeviceOverviewRow(row.Label!, row.Value!)).ToArray()
            : [];
    public static string DeviceNumber(DeviceRecord device) => (device.Vid is not null && device.Pid is not null ? device.Vid + ":" + device.Pid + " · " : "") + device.InstanceId.Split('\\').Last();
    public static bool SupportedImage(string path) => Path.GetExtension(path).ToLowerInvariant() is ".png" or ".jpg" or ".jpeg" or ".webp" or ".svg";
    private static string? Read(JsonElement item, string key) => item.TryGetProperty(key, out var value)
        && value.ValueKind == JsonValueKind.String && !string.IsNullOrWhiteSpace(value.GetString()) ? value.GetString() : null;
}

public sealed record DeviceOverviewRow(string Label, string Value);
public sealed record DeviceOverviewState(DeviceOverviewRow[] Rows, string? Current, string[] Configurations)
{
    public static string[] Options(JsonElement values) => values.ValueKind == JsonValueKind.Array
        ? values.EnumerateArray().Take(128).Select(item => item.ValueKind == JsonValueKind.Object
            && item.TryGetProperty("value", out var value) ? value : item)
            .Where(item => item.ValueKind == JsonValueKind.String)
            .Select(item => item.GetString()!).Where(name => !string.IsNullOrWhiteSpace(name) && name.Length <= 128)
            .Distinct(StringComparer.Ordinal).ToArray()
        : [];
}

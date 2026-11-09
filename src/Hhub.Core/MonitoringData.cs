using System.Text.Json;

namespace Hhub.Core;

public sealed record DiskReading(string Id, string Name, double? Temperature, double? Total, bool IsPhysical = true, double? Used = null);

public static class MonitoringData
{
    public static IReadOnlyList<DiskReading> Disks(IReadOnlyDictionary<string, JsonElement> values)
    {
        if (values.TryGetValue("storage.disks", out var disks) && disks.ValueKind == JsonValueKind.Array && disks.GetArrayLength() > 0)
            return disks.EnumerateArray().Select(item => new DiskReading(
                item.GetProperty("id").GetString()!, item.GetProperty("name").GetString()!,
                Number(item, "temperature"), Number(item, "total"), Used: Number(item, "used"))).ToArray();
        // Old monitoring packages can still supply volume capacities, but not physical temperatures.
        if (!values.TryGetValue("storage.volumes", out var volumes) || volumes.ValueKind != JsonValueKind.Array) return [];
        return volumes.EnumerateArray().Select(item => new DiskReading(
            "volume/" + item.GetProperty("id").GetString(), "卷 " + item.GetProperty("name").GetString(),
            null, Number(item, "total"), false, Number(item, "used"))).ToArray();
    }

    private static double? Number(JsonElement item, string key) => item.TryGetProperty(key, out var value)
        && value.ValueKind == JsonValueKind.Number && value.TryGetDouble(out var number) && double.IsFinite(number) ? number : null;
}

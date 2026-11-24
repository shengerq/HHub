using System.Text.Json;
using Hhub.Core;
using Hhub.PluginContracts;

namespace Hhub.Desktop.Pages;

internal sealed record ManagedDevice(string Id, string Name, string Category, string Connection);

internal sealed record RemovedDevice(ConnectedPeripheral Device, int? CustomIndex);

internal static class ManagedDevices
{
    private const string FileName = "devices.json";
    private const int SchemaVersion = 1;
    private static readonly object Gate = new();
    private static readonly List<ManagedDevice> CustomDevices = [];
    private static readonly HashSet<string> HiddenIds = new(StringComparer.Ordinal);
    private static bool _loaded;
    private static string Prefix => "custom:";

    public static event Action? Changed;

    public static ConnectedPeripheral[] Apply(ConnectedPeripheral[] detected)
    {
        EnsureLoaded();
        lock (Gate)
        {
            var list = detected.Where(device => !HiddenIds.Contains(device.Id)).ToList();
            foreach (var custom in CustomDevices)
            {
                if (HiddenIds.Contains(custom.Id) || list.Any(device => device.Id == custom.Id)) continue;
                list.Add(new ConnectedPeripheral(custom.Id, custom.Name, custom.Category, custom.Connection)
                {
                    IsPreciselyIdentified = true
                });
            }
            return list.ToArray();
        }
    }

    public static ConnectedPeripheral Add(string name)
    {
        EnsureLoaded();
        var device = new ManagedDevice(Prefix + Guid.NewGuid().ToString("N"), name.Trim(), "自定义", "手动");
        lock (Gate)
        {
            CustomDevices.Add(device);
            Save();
        }
        NotifyChanged();
        return new ConnectedPeripheral(device.Id, device.Name, device.Category, device.Connection)
        {
            IsPreciselyIdentified = true
        };
    }

    public static RemovedDevice Remove(ConnectedPeripheral device)
    {
        EnsureLoaded();
        int? customIndex = null;
        lock (Gate)
        {
            if (device.Id.StartsWith(Prefix, StringComparison.Ordinal))
            {
                customIndex = CustomDevices.FindIndex(item => item.Id == device.Id);
                if (customIndex >= 0) CustomDevices.RemoveAt(customIndex.Value);
                else customIndex = null;
            }
            HiddenIds.Add(device.Id);
            Save();
        }
        NotifyChanged();
        return new RemovedDevice(device, customIndex);
    }

    public static void Undo(RemovedDevice removed)
    {
        EnsureLoaded();
        lock (Gate)
        {
            HiddenIds.Remove(removed.Device.Id);
            if (removed.CustomIndex is int index && index >= 0 && index <= CustomDevices.Count
                && CustomDevices.All(item => item.Id != removed.Device.Id))
            {
                CustomDevices.Insert(index, new ManagedDevice(removed.Device.Id, removed.Device.Name,
                    removed.Device.Category, removed.Device.Connection));
            }
            Save();
        }
        NotifyChanged();
    }

    public static bool IsCustom(ConnectedPeripheral device) =>
        device.Id.StartsWith(Prefix, StringComparison.Ordinal);

    private static void NotifyChanged()
    {
        try { Changed?.Invoke(); }
        catch (Exception error) { System.Diagnostics.Trace.WriteLine(error); }
    }

    private static void EnsureLoaded()
    {
        if (_loaded) return;
        lock (Gate)
        {
            if (_loaded) return;
            _loaded = true;
            try
            {
                string path = Path.Combine(PluginPreferences.Root, FileName);
                if (!File.Exists(path)) return;
                var document = JsonSerializer.Deserialize<Document>(File.ReadAllText(path));
                if (document is not { SchemaVersion: SchemaVersion }) return;
                HiddenIds.UnionWith(document.Hidden ?? []);
                foreach (var item in document.Custom ?? [])
                {
                    if (!string.IsNullOrWhiteSpace(item.Id) && !string.IsNullOrWhiteSpace(item.Name)
                        && item.Id.StartsWith(Prefix, StringComparison.Ordinal))
                        CustomDevices.Add(item);
                }
            }
            catch (Exception error) when (error is IOException or UnauthorizedAccessException
                or JsonException or InvalidDataException)
            {
                System.Diagnostics.Trace.WriteLine(error);
            }
        }
    }

    private static void Save()
    {
        try
        {
            Directory.CreateDirectory(PluginPreferences.Root);
            var document = new Document(SchemaVersion, HiddenIds.ToArray(), CustomDevices.ToArray());
            string path = Path.Combine(PluginPreferences.Root, FileName);
            string temporary = path + ".tmp";
            File.WriteAllText(temporary, JsonSerializer.Serialize(document));
            File.Move(temporary, path, true);
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException)
        {
            System.Diagnostics.Trace.WriteLine(error);
        }
    }

    private sealed record Document(int SchemaVersion, string[]? Hidden, ManagedDevice[]? Custom);
}

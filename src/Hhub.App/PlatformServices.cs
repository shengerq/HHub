using System.Diagnostics;
using System.Runtime.InteropServices;
using Hhub.Core;
using Windows.Devices.Enumeration;

namespace Hhub.App;

public static partial class PlatformServices
{
    public const string DeviceSelector = "System.Devices.Present:=System.StructuredQueryType.Boolean#True";
    public const string LocationProperty = "{A45C254E-DF1C-4EFD-8020-67D146A850E0} 15";
    public const string LocationPathsProperty = "{A45C254E-DF1C-4EFD-8020-67D146A850E0} 37";
    public const string BusDescriptionProperty = "{540B947E-8B40-45BC-A8A2-6A0B894CBDA2} 4";
    public const string CompatibleIdsProperty = "{A45C254E-DF1C-4EFD-8020-67D146A850E0} 4";
    public const string CapabilitiesProperty = "{A45C254E-DF1C-4EFD-8020-67D146A850E0} 17";
    public static readonly string[] DeviceProperties = ["System.Devices.ClassGuid", "System.Devices.DeviceInstanceId", "System.Devices.Parent", LocationProperty, LocationPathsProperty, BusDescriptionProperty, CompatibleIdsProperty, CapabilitiesProperty, "System.Devices.ContainerId"];
    public static IReadOnlyList<DeviceNode> LastDeviceNodes { get; private set; } = [];
    public static IReadOnlyList<string> DeviceEnumerationIssues { get; private set; } = [];
    public static DeviceWatcher CreateDeviceWatcher() => DeviceInformation.CreateWatcher(DeviceSelector, DeviceProperties, DeviceInformationKind.Device);
    [DllImport("user32.dll")]
    public static extern uint GetDpiForWindow(nint window);
    [DllImport("user32.dll")]
    public static extern uint GetDoubleClickTime();
    [DllImport("user32.dll")]
    private static extern nint GetForegroundWindow();
    [DllImport("user32.dll")]
    private static extern uint GetWindowThreadProcessId(nint window, out uint processId);

    public static string? CaptureForegroundApplication()
    {
        GetWindowThreadProcessId(GetForegroundWindow(), out uint foregroundId);
        if (foregroundId == 0) return null;
        try
        {
            using var process = Process.GetProcessById((int)foregroundId);
            return process.ProcessName;
        }
        catch (Exception error) when (error is ArgumentException or InvalidOperationException or System.ComponentModel.Win32Exception)
        { return null; }
    }

    public static ActivitySnapshot CaptureActivity(bool includeRunningApplications = true)
    {
        if (!includeRunningApplications)
            return new(CaptureForegroundApplication(), System.Collections.Frozen.FrozenSet<string>.Empty);
        string? foreground = null;
        GetWindowThreadProcessId(GetForegroundWindow(), out uint foregroundId);
        HashSet<string> applications = new(StringComparer.OrdinalIgnoreCase);
        foreach (var process in Process.GetProcesses())
        {
            using (process)
            {
                try
                {
                    string name = process.ProcessName;
                    applications.Add(name);
                    if (process.Id == foregroundId) foreground = name;
                }
                catch (InvalidOperationException) { /* A process can exit during enumeration. */ }
            }
        }
        return new(foreground, applications);
    }

    public static async Task<IReadOnlyList<DeviceRecord>> EnumerateDevicesAsync()
    {
        var devices = await DeviceInformation.FindAllAsync(DeviceSelector, DeviceProperties, DeviceInformationKind.Device);
        var interfaces = await DeviceInformation.FindAllAsync("", ["System.Devices.DeviceInstanceId"], DeviceInformationKind.DeviceInterface);
        var paths = interfaces.Where(item => item.Properties.GetValueOrDefault("System.Devices.DeviceInstanceId") is string)
            .GroupBy(item => (string)item.Properties["System.Devices.DeviceInstanceId"], StringComparer.OrdinalIgnoreCase)
            .ToDictionary(group => group.Key, group => group.Select(item => item.Id).ToArray(), StringComparer.OrdinalIgnoreCase);
        var nodes = devices.Select(device =>
        {
            string instance = device.Properties.GetValueOrDefault("System.Devices.DeviceInstanceId")?.ToString() ?? device.Id;
            string classId = device.Properties.GetValueOrDefault("System.Devices.ClassGuid")?.ToString()?.Trim('{', '}').ToLowerInvariant() ?? "";
            string category = classId switch
            {
                "4d36e96f-e325-11ce-bfc1-08002be10318" => "鼠标",
                "4d36e96b-e325-11ce-bfc1-08002be10318" => "键盘",
                "745a17a0-74d3-11d0-b6fe-00a0c90f57da" => "HID",
                "4d36e96c-e325-11ce-bfc1-08002be10318" => "音频",
                "c166523c-fe0c-4a94-a586-f1a80cfbbf3e" => "音频",
                "e0cbf06c-cd8b-4647-bb8a-263b43f0f974" => "蓝牙",
                "36fc9e60-c465-11cf-8056-444553540000" => "USB",
                "ca3e7ab9-b4c3-4ae6-8251-579ef933890f" => "摄像头",
                "6bdd1fc6-810f-11d0-bec7-08002be2092f" => "摄像头",
                "4d36e967-e325-11ce-bfc1-08002be10318" => "磁盘",
                "4d36e979-e325-11ce-bfc1-08002be10318" => "打印机",
                "4658ee7e-f050-11d1-b6bd-00c04fa372a7" => "打印机",
                _ => "其他"
            };
            string? locationPath = device.Properties.GetValueOrDefault(LocationPathsProperty) switch
            {
                string value => value,
                IEnumerable<string> entries => entries.FirstOrDefault(),
                _ => null
            };
            return new DeviceNode(instance, device.Name, category,
                device.Properties.GetValueOrDefault("System.Devices.Parent")?.ToString(),
                device.Properties.GetValueOrDefault("System.Devices.ContainerId")?.ToString(),
                device.Properties.GetValueOrDefault(LocationProperty)?.ToString(), locationPath,
                device.Properties.GetValueOrDefault(BusDescriptionProperty)?.ToString(), paths.GetValueOrDefault(instance),
                device.Properties.GetValueOrDefault(CompatibleIdsProperty) is IEnumerable<string> compatible ? compatible.ToArray() : [],
                Removable: device.Properties.GetValueOrDefault(CapabilitiesProperty) is { } capabilities
                    ? (Convert.ToUInt32(capabilities) & 4) != 0 : null);
        }).ToDictionary(node => node.Id, StringComparer.OrdinalIgnoreCase);
        var issues = new List<string>();
        foreach (var endpoint in await Task.Run(() => AudioDeviceInventory.Capture(issues)))
        {
            if (nodes.TryGetValue(endpoint.Id, out var existing))
                nodes[endpoint.Id] = endpoint with { ParentId = endpoint.ParentId ?? existing.ParentId,
                    ContainerId = endpoint.ContainerId ?? existing.ContainerId,
                    Interfaces = (existing.Interfaces ?? []).Concat(endpoint.Interfaces ?? []).Distinct().ToArray() };
            else nodes[endpoint.Id] = endpoint;
        }
        LastDeviceNodes = nodes.Values.ToArray(); DeviceEnumerationIssues = issues;
        return DeviceInventory.Build(LastDeviceNodes);
    }

}

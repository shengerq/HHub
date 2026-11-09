using System.Text.Json;

namespace Hhub.Core;

public static class PluginPolicies
{
    public static bool HasNavigation(PluginManifest manifest) => manifest.Category != "device"
        && (manifest.Category == "utility" || manifest.Navigation is not null);

    public static InstalledPlugin? DevicePage(IEnumerable<InstalledPlugin> plugins, DeviceRecord device)
    {
        var candidates = plugins.Where(p => p.Manifest.Category == "device" && MatchesDevice(p.Manifest, device)
            && p.Manifest.Page is not null && p.Manifest.UiAssembly is not null).Take(2).ToArray();
        return candidates.Length == 1 ? candidates[0] : null;
    }

    public static DeviceSceneBinding[] SaveDeviceConfiguration(IEnumerable<DeviceSceneBinding> bindings, DeviceSceneBinding incoming, PluginManifest manifest)
    {
        string Resource(string commandId)
        {
            var command = manifest.Commands.FirstOrDefault(command => command.Id == commandId);
            return command?.ConflictResource ?? command?.Resource ?? commandId;
        }
        string resource = Resource(incoming.Command);
        return bindings.Where(binding => binding.DeviceId != incoming.DeviceId || binding.SceneId != incoming.SceneId
            || binding.PluginId != incoming.PluginId || Resource(binding.Command) != resource).Append(incoming).ToArray();
    }

    public static bool ShouldRun(string mode, bool demand) => mode switch
    {
        "disabled" => false, "always" => true, _ => demand
    };

    public static string UnifiedMode(string? mode) => mode == "disabled" ? "disabled" : "onDemand";

    public static bool ShouldAutoStart(string mode, bool demand, PluginRunState state)
        => state is not (PluginRunState.Faulted or PluginRunState.Stopping) && ShouldRun(mode, demand);

    public static bool HasDemand(PluginManifest manifest, string page, string? openPluginId, string? monitorId,
        DeviceRecord? selectedDevice, bool executingCommand, bool backgroundActivity = false) => executingCommand
        || manifest.SupportsBackgroundActivity && (backgroundActivity || !string.IsNullOrWhiteSpace(manifest.PersistentTaskReason))
        || NeedsPage(manifest, page, openPluginId, monitorId)
        || selectedDevice is not null && MatchesDevice(manifest, selectedDevice);

    public static IEnumerable<PluginCommand> DeviceConfigurations(PluginManifest manifest, DeviceRecord device) =>
        manifest.Transport == "stdio-v1" && MatchesDevice(manifest, device)
            ? manifest.Commands.Where(command => command.SceneConfigurable) : [];

    public static bool NeedsPage(PluginManifest manifest, string page, string? openPluginId, string? monitorId)
    {
        if (manifest.Id == openPluginId) return true;
        if (!manifest.ActivationPages.Contains(page)) return false;
        return manifest.Category != "monitor" || manifest.Id == monitorId;
    }

    public static bool MatchesDevice(PluginManifest manifest, DeviceRecord device) => manifest.DeviceMatches.Any(match =>
        (match.Vid is not null || match.Pid is not null || match.Category is not null || match.Connection is not null || match.NameContains is not null || match.AudioRenderEndpointId is not null)
        && (match.AudioRenderEndpointId is null || device.AudioRenderEndpointIds.Contains(match.AudioRenderEndpointId, StringComparer.OrdinalIgnoreCase))
        && Matches(match.Vid, device.Vid) && Matches(match.Pid, device.Pid)
        && (Matches(match.Category, device.Category) || device.Categories.Any(category => Matches(match.Category, category)))
        && Matches(match.Connection, device.Connection)
        && (match.NameContains is null || device.Name.Contains(match.NameContains, StringComparison.OrdinalIgnoreCase)
            || device.ReportedName?.Contains(match.NameContains, StringComparison.OrdinalIgnoreCase) == true));

    private static bool Matches(string? expected, string? actual) => expected is null
        || string.Equals(expected, actual, StringComparison.OrdinalIgnoreCase);

    public static Dictionary<string, JsonElement> DeviceArguments(DeviceSceneBinding binding)
    {
        var values = new Dictionary<string, JsonElement>(binding.Values);
        values["deviceInstanceId"] = JsonSerializer.SerializeToElement(binding.DeviceId);
        return values;
    }
}

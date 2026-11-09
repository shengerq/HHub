using System.Text.Json;

namespace Hhub.Core;

public sealed record ScenePlanItem(string Key, string PluginId, string Command, Dictionary<string, JsonElement> Values, string? DeviceId);

public static class ScenePlan
{
    public static IReadOnlyList<ScenePlanItem> Build(SceneProfile profile, IEnumerable<DeviceSceneBinding> bindings, IReadOnlySet<string> connected)
    {
        List<ScenePlanItem> plan = [];
        foreach (var action in profile.Actions)
            plan.Add(new($"{profile.Id}/{action.PluginId}/{action.Resource}/{action.Command}/{JsonSerializer.Serialize(action.Values, JsonFormat.Wire)}",
                action.PluginId, action.Command, action.Values, null));
        foreach (var binding in bindings.Where(binding => binding.SceneId == profile.Id && connected.Contains(binding.DeviceId)))
        {
            var values = PluginPolicies.DeviceArguments(binding);
            plan.Add(new($"{profile.Id}/{binding.PluginId}/{binding.DeviceId}/{binding.Command}/{JsonSerializer.Serialize(values, JsonFormat.Wire)}",
                binding.PluginId, binding.Command, values, binding.DeviceId));
        }
        return plan;
    }
}

namespace Hhub.Core;

public static class SceneEngine
{
    public static IReadOnlyList<SceneRule> Evaluate(IEnumerable<SceneRule> rules, ActivitySnapshot activity)
    {
        return rules.Where(rule => rule.Enabled && Matches(rule, activity))
            .OrderByDescending(rule => rule.Priority).ThenBy(rule => rule.Id, StringComparer.Ordinal)
            .GroupBy(rule => rule.SceneId is null ? $"{rule.PluginId}/{rule.Resource}" : "scene.active", StringComparer.Ordinal)
            .Select(group => group.First()).ToArray();
    }

    public static string EffectiveScene(IEnumerable<SceneRule> rules, ActivitySnapshot activity, string manualSceneId, bool locked)
        => locked ? manualSceneId : Evaluate(rules, activity).FirstOrDefault(rule => rule.SceneId is not null)?.SceneId ?? manualSceneId;

    public static bool Matches(SceneRule rule, ActivitySnapshot activity)
    {
        string application = Path.GetFileName(rule.Application.Trim().Trim('"'));
        if (application.EndsWith(".exe", StringComparison.OrdinalIgnoreCase)) application = application[..^4];
        return rule.Condition switch
        {
            "foreground" => string.Equals(application, activity.ForegroundApplication, StringComparison.OrdinalIgnoreCase),
            "running" => activity.RunningApplications.Contains(application),
            _ => false
        };
    }
}

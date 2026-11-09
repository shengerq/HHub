namespace Hhub.Core;

public sealed record PresetApplication(string SceneId, string Name, string ProcessName);
public sealed record ScenePresetPlan(List<SceneProfile> Profiles, List<SceneRule> Rules, string DailySceneId);

public static class ScenePresets
{
    public const int Version = 1;
    public static IReadOnlyList<PresetApplication> Applications { get; } = Array.AsReadOnly(new[]
    {
        new PresetApplication("music", "QQ 音乐", "QQMusic"),
        new PresetApplication("music", "网易云音乐", "cloudmusic"),
        new PresetApplication("gaming", "Counter-Strike 2", "cs2"),
        new PresetApplication("gaming", "Apex Legends", "r5apex"),
        new PresetApplication("gaming", "Apex Legends (DX12)", "r5apex_dx12")
    });

    public static string ProcessName(string application)
    {
        string name = Path.GetFileName(application.Trim().Trim('"'));
        return name.EndsWith(".exe", StringComparison.OrdinalIgnoreCase) ? name[..^4] : name;
    }

    public static string DisplayName(string application) => Applications.FirstOrDefault(item =>
        item.ProcessName.Equals(ProcessName(application), StringComparison.OrdinalIgnoreCase))?.Name ?? application;

    public static ScenePresetPlan Create(IEnumerable<SceneProfile> profiles, IEnumerable<SceneRule> rules)
    {
        var existing = profiles.ToList();
        var result = new List<SceneProfile>();
        var targets = new Dictionary<string, string>();
        foreach (var (id, name) in new[] { ("daily", "日常"), ("music", "音乐"), ("gaming", "游戏") })
        {
            var available = existing.Where(item => !result.Any(chosen => chosen.Id == item.Id));
            var profile = available.FirstOrDefault(item => item.Id == id) ?? available.FirstOrDefault(item => item.Name == name);
            profile ??= new SceneProfile(existing.Concat(result).Any(item => item.Id == id) ? Guid.NewGuid().ToString("N") : id, name);
            targets[id] = profile.Id; result.Add(profile);
        }
        result.AddRange(existing.Where(item => !result.Any(preset => preset.Id == item.Id)));
        var configured = rules.ToList();
        foreach (var app in Applications)
        {
            string id = "preset.v1." + app.SceneId + "." + app.ProcessName.ToLowerInvariant();
            if (configured.Any(rule => rule.Id == id || rule.SceneId is not null
                && ProcessName(rule.Application).Equals(app.ProcessName, StringComparison.OrdinalIgnoreCase))) continue;
            configured.Add(new SceneRule { Id = id, Name = app.Name + " → " + result.First(item => item.Id == targets[app.SceneId]).Name,
                Application = app.ProcessName, Condition = "foreground", Priority = 0, SceneId = targets[app.SceneId] });
        }
        return new(result, configured, targets["daily"]);
    }
}

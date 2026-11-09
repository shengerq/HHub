using Hhub.Core;
using System.Text.Json;

internal static class ScenePresetChecks
{
    public static void Run(Action<bool, string> check)
    {
        var plan = ScenePresets.Create([], []);
        check(plan.Profiles.Select(profile => profile.Name).SequenceEqual(new[] { "日常", "音乐", "游戏" }), "new installations receive three ordered, named scene presets");
        check(plan.Profiles.All(profile => profile.Actions.Length == 0), "scene presets do not invent device commands or hardware settings");
        string Effective(string? foreground, params string[] running) => SceneEngine.EffectiveScene(plan.Rules,
            new(foreground, running.ToHashSet(StringComparer.OrdinalIgnoreCase)), plan.DailySceneId, false);
        check(Effective("QQMusic") == "music" && Effective("CLOUDMUSIC") == "music", "QQ Music and NetEase foreground processes activate the music preset");
        check(Effective("cs2", "QQMusic") == "gaming", "foreground CS2 activates gaming even when a music player remains running");
        check(Effective("r5apex") == "gaming" && Effective("r5apex_dx12") == "gaming", "both Apex process variants activate the gaming preset");
        check(Effective("editor", "QQMusic", "cs2") == "daily" && Effective(null) == "daily", "background-only preset apps and unknown foreground windows return to the daily baseline");
        check(SceneEngine.EffectiveScene(plan.Rules, new("cs2", new HashSet<string>()), "music", true) == "music", "manual scene locking continues to override preset automation");
        var action = new SceneAction("custom.plugin", "apply", "custom", new() { ["value"] = JsonSerializer.SerializeToElement(7) });
        var existingGame = new SceneProfile("user-game", "游戏") { Actions = [action] };
        var custom = new SceneRule { Id = "custom.qq", Name = "Custom QQ", Application = "QQMusic.exe", SceneId = existingGame.Id, Enabled = false };
        var migrated = ScenePresets.Create([existingGame], [custom]);
        check(migrated.Profiles.Single(profile => profile.Name == "游戏").Id == "user-game"
            && migrated.Profiles.Single(profile => profile.Id == "user-game").Actions.Single() == action,
            "migration reuses existing scene identities and preserves their actions and external binding references");
        check(migrated.Rules.Count(rule => ScenePresets.ProcessName(rule.Application).Equals("QQMusic", StringComparison.OrdinalIgnoreCase)) == 1
            && migrated.Rules.Contains(custom), "existing app assignments and disabled rules are not overridden by presets");
        var repeated = ScenePresets.Create(migrated.Profiles, migrated.Rules);
        check(repeated.Profiles.Count == migrated.Profiles.Count && repeated.Rules.Count == migrated.Rules.Count, "retrying a preset migration does not duplicate scenes or rules");
        var renamed = ScenePresets.Create([new SceneProfile("music", "日常")], []);
        check(renamed.Profiles.Select(profile => profile.Id).Distinct().Count() == 3, "renamed legacy scenes cannot create duplicate preset identifiers");
    }
}

using Hhub.Core;
using System.Text.Json;

internal static class SceneCreationChecks
{
    public static void Run(Action<bool, string> check)
    {
        var legacy = JsonSerializer.Deserialize<SceneProfile>("{\"id\":\"music\",\"name\":\"音乐\",\"glyph\":\"old-icon\"}", JsonFormat.Options)!;
        check(legacy.Id == "music" && legacy.Name == "音乐" && legacy.Glyph == "old-icon",
            "legacy scene files remain readable after scene icons are retired");
        var namesOnly = JsonSerializer.Deserialize<SceneProfile>("{\"id\":\"new\",\"name\":\"新场景\"}", JsonFormat.Options)!;
        check(namesOnly.Name == "新场景" && namesOnly.Glyph == "",
            "new scene files do not require a legacy icon field");
        var source = new DeviceSceneBinding("mouse-a", "original", "mouse", "setDpi",
            new() { ["dpi"] = JsonSerializer.SerializeToElement(1600) });
        var plan = SceneCreationPlan.Create("  FPS  ", ["game", "GAME", " ", "editor"], [source]);
        check(plan.Profile.Name == "FPS" && plan.Profile.Id != "original", "new scene gets a trimmed name and independent identity");
        check(plan.Rules.Length == 2 && plan.Rules.All(rule => rule.Condition == "foreground" && rule.SceneId == plan.Profile.Id),
            "new scene foreground apps are deduplicated and assigned to the new profile");
        check(plan.Bindings.Single().SceneId == plan.Profile.Id && plan.Bindings[0].DeviceId == "mouse-a"
            && plan.Bindings[0].Command == "setDpi", "copied device configuration targets the new scene and original device");
        plan.Bindings[0].Values["dpi"] = JsonSerializer.SerializeToElement(800);
        check(source.SceneId == "original" && source.Values["dpi"].GetInt32() == 1600,
            "editing a copied configuration cannot mutate its source scene");
        var activity = new ActivitySnapshot("game", new HashSet<string> { "game" });
        check(SceneEngine.Evaluate(plan.Rules, activity).Single().SceneId == plan.Profile.Id,
            "a created foreground rule can activate its scene through the existing engine");
        var empty = SceneCreationPlan.Create("Manual", [], []);
        check(empty.Rules.Length == 0 && empty.Bindings.Length == 0, "manual scene can be created without apps or device bindings");
        foreach (string invalid in new[] { " ", new string('x', 41) })
        {
            bool rejected = false;
            try { SceneCreationPlan.Create(invalid, [], []); } catch (InvalidDataException) { rejected = true; }
            check(rejected, "invalid scene name is rejected before creating a profile or rules");
        }
    }
}

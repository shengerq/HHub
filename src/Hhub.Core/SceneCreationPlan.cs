namespace Hhub.Core;

public sealed record SceneCreationPlan(SceneProfile Profile, SceneRule[] Rules, DeviceSceneBinding[] Bindings)
{
    public static SceneCreationPlan Create(string name, IEnumerable<string> applications, IEnumerable<DeviceSceneBinding> configurations)
    {
        name = name.Trim();
        if (name.Length is 0 or > 40) throw new InvalidDataException("场景名称应为 1 到 40 个字符");
        var profile = new SceneProfile(Guid.NewGuid().ToString("N"), name);
        var rules = applications.Select(application => application.Trim()).Where(application => application.Length > 0)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .Select(application => new SceneRule { Name = application + " → " + name, Application = application, SceneId = profile.Id }).ToArray();
        var bindings = configurations.Select(binding => binding with
        {
            SceneId = profile.Id,
            Values = binding.Values.ToDictionary(pair => pair.Key, pair => pair.Value.Clone())
        }).ToArray();
        return new(profile, rules, bindings);
    }
}

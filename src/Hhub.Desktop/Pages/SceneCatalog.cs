using System.Text.Json;

namespace Hhub.Desktop.Pages;

internal sealed record ScenePreview(string Name, string[] Applications, Dictionary<string, string> Bindings,
    Dictionary<string, SceneDeviceBinding>? DeviceConfigurations = null);

internal sealed record SceneDeviceBinding(string PluginId, string DeviceName, SceneConfiguration Configuration);
internal sealed record SceneConfiguration(string Name, string? PresetId = null, string? Selector = null);

internal sealed class SceneCatalog
{
    public const string DefaultScene = "日常";
    private static readonly HashSet<string> FormerPreviewDeviceIds = new(StringComparer.Ordinal)
    {
        "mouse", "keyboard", "headset", "controller", "camera", "microphone", "speaker", "keypad"
    };

    private readonly string _path = Path.Combine(
        PluginPreferences.Root, "scenes.json");
    private const string CurrentFile = "current-scene.json";

    public static bool IsSampleApplication(string application) =>
        application is "虚拟浏览器" or "虚拟游戏" or "虚拟音乐播放器";

    public string? ReadCurrent()
    {
        try { return new Hhub.Core.AppStorage(PluginPreferences.Root).Load<string?>(CurrentFile, null); }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or JsonException or InvalidDataException)
        {
            System.Diagnostics.Trace.WriteLine(error);
            return null;
        }
    }

    public void SetCurrent(string name) => new Hhub.Core.AppStorage(PluginPreferences.Root).Save(CurrentFile, name);

    public async Task<List<ScenePreview>> LoadAsync(CancellationToken cancellationToken)
    {
        if (File.Exists(_path))
        {
            string json = await File.ReadAllTextAsync(_path, cancellationToken).ConfigureAwait(false);
            cancellationToken.ThrowIfCancellationRequested();
            var scenes = JsonSerializer.Deserialize<List<ScenePreview>>(json)
                ?? throw new InvalidDataException("场景文件为空。");
            foreach (var scene in scenes)
                foreach (string id in FormerPreviewDeviceIds)
                    scene.Bindings.Remove(id);
            return scenes;
        }

        return [new(DefaultScene, [], new()), new("游戏", [], new()), new("音乐", [], new())];
    }

    public void Save(List<ScenePreview> scenes)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(_path)!);
        string temporary = _path + ".tmp";
        File.WriteAllText(temporary, JsonSerializer.Serialize(scenes));
        File.Move(temporary, _path, overwrite: true);
    }
}

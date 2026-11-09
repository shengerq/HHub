using System.Text.Json;
using Hhub.Core;

namespace Hhub.App;

public sealed partial class MainWindow
{
    private DateTimeOffset _remoteMonitorUntil;
    private static readonly string[] RemoteMetricKeys =
    ["cpu.name", "cpu.load", "cpu.cores", "cpu.threads", "cpu.clock", "cpu.power", "cpu.temperature",
        "gpu.name", "gpu.load", "gpu.memoryUsed", "gpu.memoryTotal", "gpu.clock", "gpu.power", "gpu.temperature", "gpu.memoryTemperature",
        "memory.load", "memory.used", "memory.total", "memory.clock", "memory.temperature", "network.download", "network.upload"];

    private Task<JsonElement> HandleRemoteHostRequestAsync(InstalledPlugin plugin, string action, JsonElement values)
    {
        var completion = new TaskCompletionSource<JsonElement>(TaskCreationOptions.RunContinuationsAsynchronously);
        if (!DispatcherQueue.TryEnqueue(async () =>
        {
            try
            {
                if (_closing || _localPackageInstallBusy || _installingPlugins.Contains(plugin.Manifest.Id)
                    || !_installed.Plugins.Any(item => item.Manifest.Id == plugin.Manifest.Id && item.Manifest.Version == plugin.Manifest.Version
                        && item.Directory == plugin.Directory) || PluginMode(plugin.Manifest.Id) == "disabled")
                    throw new InvalidOperationException("主体正在退出或更新");
                if (action == "state.read" && plugin.Manifest.Permissions.Contains("host.remote.state"))
                {
                    if (values.TryGetProperty("monitor", out var monitor) && monitor.ValueKind == JsonValueKind.True)
                    {
                        _remoteMonitorUntil = DateTimeOffset.UtcNow.AddSeconds(6);
                        QueueActivation();
                    }
                    completion.SetResult(RemoteState());
                    return;
                }
                if (action == "scene.select" && plugin.Manifest.Permissions.Contains("host.remote.scenes"))
                {
                    if (_scenePreview) throw new InvalidOperationException("请先退出桌面的场景预览");
                    string? id = values.GetProperty("id").GetString();
                    var scene = _profiles.FirstOrDefault(item => item.Id == id) ?? throw new ArgumentException("该场景已移除");
                    await SelectSceneAsync(scene, refreshPage: false);
                    completion.SetResult(JsonSerializer.SerializeToElement(new { currentScene = CurrentScene?.Id, applied = _sceneErrors.Count == 0 }, JsonFormat.Wire));
                    return;
                }
                throw new UnauthorizedAccessException("未获准的远控操作");
            }
            catch (Exception error) when (error is IOException or InvalidOperationException or UnauthorizedAccessException or ArgumentException or JsonException or KeyNotFoundException)
            { completion.SetException(error is KeyNotFoundException ? new ArgumentException("缺少场景参数") : error); }
        })) completion.SetException(new InvalidOperationException("主体已退出"));
        return completion.Task;
    }

    private JsonElement RemoteState()
    {
        var monitor = MonitorPlugin;
        var data = monitor is not null && _runtime.IsFresh(monitor.Manifest) ? _runtime.Snapshot(monitor.Manifest.Id) : null;
        var metrics = new Dictionary<string, JsonElement>();
        foreach (string key in RemoteMetricKeys)
            if (data?.TryGetValue(key, out var value) == true) metrics[key] = value;
        return JsonSerializer.SerializeToElement(new
        {
            metrics,
            disks = data is null ? [] : MonitoringData.Disks(data).Take(12).Select(disk => new { disk.Name, disk.Used, disk.Total, disk.Temperature }).ToArray(),
            devices = _devices.Take(60).Select(device =>
            {
                var display = Describe(device);
                return new { display.Name, display.Connection, Category = display.Category ?? device.Category, display.Battery };
            }),
            scenes = _profiles.Take(60).Select(scene => new { scene.Id, scene.Name }),
            currentScene = CurrentScene?.Id,
            sceneConfigurations = CurrentScene is { } current ? _bindings.Where(binding => binding.SceneId == current.Id).Take(60)
                .Select(binding => new { device = _devices.FirstOrDefault(device => device.ContainsInstance(binding.DeviceId))?.Name,
                    plugin = _installed.Plugins.FirstOrDefault(item => item.Manifest.Id == binding.PluginId)?.Manifest.Name,
                    preset = binding.Values.TryGetValue("name", out var preset) && preset.ValueKind == JsonValueKind.String ? preset.GetString() : null }).ToArray() : [],
            plugins = _installed.Plugins.Take(100).Select(item => new { item.Manifest.Name, item.Manifest.Version, mode = PluginMode(item.Manifest.Id), running = _runtime.IsRunning(item.Manifest.Id) }),
            market = _remoteMarket.Take(100).Select(item => new { item.Name, item.Version })
        }, JsonFormat.Wire);
    }
}

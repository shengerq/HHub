using Hhub.Core;
using Hhub.PluginUi;
using Microsoft.UI.Xaml;
using System.Text.Json;
using Windows.ApplicationModel.DataTransfer;

namespace Hhub.App;

public sealed partial class MainWindow
{
    private readonly HashSet<string> _embeddedPluginIds = [];
    private readonly SemaphoreSlim _compositionGate = new(1, 1);
    private readonly Dictionary<(string Plugin, string Device), string> _integrationOutputs = [];

    private InstalledPlugin? IntegrationTarget(InstalledPlugin source, string id)
    {
        var integration = PluginComposition.Integration(source.Manifest, id);
        return _installed.Plugins.FirstOrDefault(plugin => plugin.Manifest.Id == integration.PluginId);
    }

    private IPluginPageView CreateEmbeddedPluginPage(InstalledPlugin source, string id, PluginPage page, DeviceRecord? device)
    {
        var target = IntegrationTarget(source, id) ?? throw new InvalidOperationException("尚未安装音频提供插件");
        if (PluginMode(target.Manifest.Id) == "disabled") throw new InvalidOperationException("音频提供插件已停用");
        if (target.Manifest.UiAssembly is null) throw new InvalidOperationException("音频提供插件未提供可嵌入界面");
        _embeddedPluginIds.Add(target.Manifest.Id); QueueActivation();
        return LoadPluginPage(target, page, (command, values) => SendIntegrationAsync(source, id, command, values, device), embedded: true);
    }

    private async Task SendIntegrationAsync(InstalledPlugin source, string id, string command,
        IReadOnlyDictionary<string, JsonElement> values, DeviceRecord? device)
    {
        if (PluginMode(source.Manifest.Id) == "disabled") throw new InvalidOperationException("设备插件已停用");
        var integration = PluginComposition.Integration(source.Manifest, id);
        var target = IntegrationTarget(source, id) ?? throw new InvalidOperationException("尚未安装音频提供插件");
        _ = PluginComposition.Command(integration, target.Manifest, command);
        await _compositionGate.WaitAsync();
        try
        {
            if (integration.DeviceCommands.Contains(command))
            {
                var connected = device is null ? null : _devices.FirstOrDefault(item => item.ContainsInstance(device.InstanceId)
                    || device.MemberInstanceIds.Any(item.ContainsInstance));
                if (connected is null || !PluginPolicies.MatchesDevice(source.Manifest, connected))
                    throw new InvalidOperationException("耳机已断开，未向其他输出发送设置");
                var outputCommand = integration.DeviceOutputCommand ?? throw new InvalidOperationException("协作入口未声明设备输出定位方式");
                _ = PluginComposition.Command(integration, target.Manifest, outputCommand);
                var key = (source.Manifest.Id, connected.InstanceId);
                var playback = AudioDeviceInventory.ReadPlaybackTargets(connected.AudioRenderEndpointIds);
                string endpoint = PluginComposition.RenderEndpoint(connected, source.Manifest, playback.Compatible,
                    playback.Default, _integrationOutputs.GetValueOrDefault(key));
                await SendPluginCommandAsync(target, outputCommand, new Dictionary<string, JsonElement>
                    { [integration.DeviceOutputParameter] = JsonSerializer.SerializeToElement(endpoint) });
                _integrationOutputs[key] = endpoint;
            }
            var parameters = values.Where(pair => pair.Key != "deviceInstanceId").ToDictionary();
            await SendPluginCommandAsync(target, command, parameters);
        }
        finally { _compositionGate.Release(); }
    }

    private async Task BindPluginConfigurationAsync(string sceneId, PluginConfiguration configuration)
    {
        if (!_profiles.Any(profile => profile.Id == sceneId)) throw new InvalidOperationException("场景已删除");
        var plugin = _installed.Plugins.FirstOrDefault(plugin => plugin.Manifest.Id == configuration.PluginId)
            ?? throw new InvalidOperationException("配置所属插件已卸载");
        var device = _devices.FirstOrDefault(device => device.ContainsInstance(configuration.DeviceId))
            ?? throw new InvalidOperationException("配置所属设备已断开");
        var binding = PluginComposition.Binding(configuration, sceneId, plugin.Manifest, device);
        _bindings = PluginPolicies.SaveDeviceConfiguration(_bindings, binding, plugin.Manifest).ToList();
        _storage.Save("device-scenes.json", _bindings);
        ResetSceneActionsForPlugin(plugin.Manifest.Id);
        await EvaluateScenesAsync();
    }

    private void EnableConfigurationDrop(FrameworkElement element, string sceneId)
    {
        element.AllowDrop = true;
        element.DragOver += (_, args) =>
        {
            if (!args.DataView.Contains(PluginComposition.DragFormat)) return;
            args.AcceptedOperation = DataPackageOperation.Copy; args.Handled = true;
            args.DragUIOverride.Caption = "绑定到此场景";
        };
        element.Drop += async (_, args) =>
        {
            if (!args.DataView.Contains(PluginComposition.DragFormat)) return;
            args.Handled = true; var deferral = args.GetDeferral();
            await RunUiActionAsync(async () =>
            {
                try
                {
                    string json = (string)await args.DataView.GetDataAsync(PluginComposition.DragFormat);
                    if (json.Length > 16384) throw new InvalidDataException("配置数据过大");
                    var configuration = JsonSerializer.Deserialize<PluginConfiguration>(json, JsonFormat.Wire)
                        ?? throw new InvalidDataException("配置无效");
                    await BindPluginConfigurationAsync(sceneId, configuration);
                }
                finally { deferral.Complete(); }
            });
        };
    }
}

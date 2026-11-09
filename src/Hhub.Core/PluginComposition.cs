using System.Text.Json;

namespace Hhub.Core;

public static class PluginComposition
{
    public const string DragFormat = "Hhub.DeviceConfiguration.v1";
    public static PluginIntegration Integration(PluginManifest source, string id) => source.Integrations.SingleOrDefault(item => item.Id == id)
        ?? throw new InvalidOperationException("插件未声明此协作入口");

    public static PluginCommand Command(PluginIntegration integration, PluginManifest target, string command)
    {
        if (target.Id != integration.PluginId || !integration.Commands.Contains(command))
            throw new InvalidOperationException("插件未声明此协作命令");
        return target.Commands.SingleOrDefault(item => item.Id == command && item.Relay is null)
            ?? throw new InvalidOperationException("音频提供方不支持此命令");
    }

    public static string RenderEndpoint(DeviceRecord device, PluginManifest source,
        IEnumerable<string> compatibleEndpoints, string? defaultEndpoint, string? previousEndpoint)
    {
        string[] ids = device.AudioRenderEndpointIds.Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
        if (ids.Length == 0)
            throw new InvalidOperationException("这副耳机尚无已关联的播放端点，不能定位音频处理目标");

        var boundIds = ids.Intersect(source.DeviceMatches.Select(match => match.AudioRenderEndpointId).OfType<string>(),
            StringComparer.OrdinalIgnoreCase).ToArray();
        var candidates = (boundIds.Length > 0 ? boundIds : ids)
            .Intersect(compatibleEndpoints, StringComparer.OrdinalIgnoreCase).ToArray();
        if (candidates.Length == 0)
            throw new InvalidOperationException("这副耳机当前没有可用的立体声播放端点，请检查连接及 Windows 输出");

        return candidates.FirstOrDefault(id => StringComparer.OrdinalIgnoreCase.Equals(id, defaultEndpoint))
            ?? candidates.FirstOrDefault(id => StringComparer.OrdinalIgnoreCase.Equals(id, previousEndpoint))
            ?? (candidates.Length == 1 ? candidates[0]
                : throw new InvalidOperationException("这副耳机有多个可用的立体声输出，请在 Windows 中选择要使用的输出后重试"));
    }

    public static DeviceSceneBinding Binding(PluginConfiguration configuration, string sceneId, PluginManifest manifest, DeviceRecord device)
    {
        if (configuration.PluginId != manifest.Id || !device.ContainsInstance(configuration.DeviceId)
            || string.IsNullOrWhiteSpace(configuration.Name) || configuration.Name.Length > 128
            || !PluginPolicies.DeviceConfigurations(manifest, device).Any(command => command.Id == configuration.Command))
            throw new InvalidDataException("配置与当前设备或插件能力不匹配");
        var command = manifest.Commands.Single(command => command.Id == configuration.Command);
        if (configuration.Values.Keys.Any(key => !command.Parameters.Any(parameter => parameter.Key == key)))
            throw new InvalidDataException("配置含有未声明的参数");
        foreach (var parameter in command.Parameters)
        {
            if (!configuration.Values.TryGetValue(parameter.Key, out var value)
                || parameter.Type == "string" && (value.ValueKind != JsonValueKind.String || string.IsNullOrWhiteSpace(value.GetString()) || value.GetString()!.Length > 256)
                || parameter.Type == "boolean" && value.ValueKind is not (JsonValueKind.True or JsonValueKind.False)
                || parameter.Type == "number" && (!value.TryGetDouble(out var number) || !double.IsFinite(number)
                    || parameter.Minimum is { } min && number < min || parameter.Maximum is { } max && number > max))
                throw new InvalidDataException("配置参数无效：" + parameter.Name);
        }
        return new(device.InstanceId, sceneId, manifest.Id, command.Id, configuration.Values);
    }
}

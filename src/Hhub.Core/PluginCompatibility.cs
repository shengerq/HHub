namespace Hhub.Core;

public static class PluginCompatibility
{
    public static Version HostVersion { get; } = new(0, 5, 0);
    public const int UiApiVersion = 1;
    public static IReadOnlySet<string> Capabilities { get; } = new HashSet<string>(StringComparer.Ordinal)
    {
        "transport.standalone.v1", "transport.stdio.v1", "runtime.dotnet10.shared.v1", "page.tiles.v1", "page.module.v1",
        "device.battery.v1", "settings.page.v1", "settings.embedded.v1", "navigation.v1", "diagnostics.v1", "background-activity.v1", "background-persistent.v1", "device-scenes.v1",
        "page.web.v1", "device-presentation.v1", "page.full-content.v1", "device-entry.v1", "device-artwork.svg.v1", "device-classification.v1", "device.overview.v1", "plugin.disclosure.v1", "navigation.path.v1",
        "plugin.composition.v1", "device.name-match.v1", "scene.configuration-drag.v1", "device.audio-endpoint-match.v1",
        "navigation.service-switch.v1", "host.remote.v1"
    };

    public static void Validate(PluginManifest manifest)
    {
        if (!Version.TryParse(manifest.MinimumHostVersion, out var minimum))
            throw new InvalidDataException("插件最低主体版本格式不正确");
        if (minimum > HostVersion)
            throw new InvalidDataException($"此插件需要 Hhub {minimum} 或更高版本，当前为 {HostVersion}");
        var missing = manifest.RequiredCapabilities.Where(capability => !Capabilities.Contains(capability)).ToArray();
        if (missing.Length > 0) throw new InvalidDataException("当前主体不支持插件所需能力：" + string.Join("、", missing));
        if (manifest.EntryPoint.EndsWith(".dll", StringComparison.OrdinalIgnoreCase)
            && (manifest.Transport != "stdio-v1" || !manifest.RequiredCapabilities.Contains("runtime.dotnet10.shared.v1")))
            throw new InvalidDataException("托管 DLL 入口必须使用 stdio-v1 并声明 runtime.dotnet10.shared.v1");
        if (manifest.UiAssembly is not null && manifest.UiApiVersion != UiApiVersion)
            throw new InvalidDataException($"不支持此插件界面 API：{manifest.UiApiVersion}，当前为 {UiApiVersion}");
        if (new[] { manifest.Page, manifest.SettingsPage }.Any(page => page?.Layout == "declarative") && manifest.UiAssembly is null)
            throw new InvalidDataException("声明式插件页面缺少 uiAssembly 界面模块");
        foreach (var page in new[] { manifest.Page, manifest.SettingsPage }.OfType<PluginPage>().Where(page => page.Layout == "tiles"))
        {
            if (page.HeaderElements.Length > 0 || page.Elements.Any(element => element is null || element.Type is not ("metric" or "command") || element.Children.Length > 0))
                throw new InvalidDataException("tiles 页面仅支持指标和命令；复杂页面需要界面模块");
        }
    }
}

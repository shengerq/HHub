using System.Text.Json;
using System.Text.RegularExpressions;
using Hhub.PluginContracts;
using Hhub.UnifiedHost;

namespace Hhub.Desktop.Pages;

internal sealed class SceneDeviceConfigurations(PluginLab? lab)
{
    private const string NamedSelector = "device.configuration";
    private const string SaveNamedConfiguration = "device.save-configuration";
    private static readonly (string Selector, string List)[] Selectors =
        [(NamedSelector, "device.configurations"), ("keyboard.profile", "keyboard.profiles"),
         ("mouse.onboard.active", "mouse.onboard.slots")];

    internal sealed record Options(SceneConfiguration[] Configurations, bool CanCapture, string? Notice);

    private static bool SupportsCapture(PluginDefinition definition) =>
        SupportsNamedSave(definition) || definition.Capabilities.Any(CanCapture);

    internal static bool CanConfigure(PluginDefinition definition) => definition.Kind == "device"
        && (HasDeviceSelector(definition) || SupportsCapture(definition));

    internal static bool HasDeviceSelector(PluginDefinition definition) => Selectors.Any(ids => SupportsSelector(definition, ids));

    internal static bool IsDeviceSelector(PluginDefinition definition, string id) =>
        Selectors.Any(ids => ids.Selector == id && SupportsSelector(definition, ids));

    internal static bool CanSaveFromMenu(PluginDefinition definition) =>
        CanConfigure(definition) && SupportsCapture(definition);

    internal static string? SceneLabel(SceneConfiguration configuration)
    {
        if (configuration.Selector == "mouse.onboard.active")
        {
            var prefix = Regex.Match(configuration.Name, @"^配置\s+\d+\s+·\s+");
            if (prefix.Success) return configuration.Name[prefix.Length..];
        }
        if (configuration.Selector == "keyboard.profile"
            && (configuration.Name == "默认配置" || Regex.IsMatch(configuration.Name, @"^配置\s+\d+$")))
            return null;
        return configuration.Name;
    }

    internal static string MenuLabel(SceneConfiguration configuration) =>
        SceneLabel(configuration) ?? (configuration.Name == "默认配置" ? "默认" : configuration.Name["配置".Length..].Trim());

    public async Task<Options> ListAsync(string provider, string target, CancellationToken token)
    {
        var (store, plugin) = await ResolveAsync(provider, token);
        if (!CanConfigure(plugin.Definition)) return new([], false, null);
        var configurations = store.ListPresets(provider, target)
            .Select(preset => new SceneConfiguration(preset.Name, PresetId: preset.PresetId)).ToList();
        string? notice = null;
        if (Selectors.Any(ids => SupportsSelector(plugin.Definition, ids)))
        {
            try
            {
                await WithDeviceAsync(store, plugin, target, (_, values) =>
                {
                    configurations.AddRange(ReadSelectors(plugin.Definition, values).Select(item => item.Configuration));
                    return Task.CompletedTask;
                }, token);
            }
            catch (Exception error) when (IsDeviceError(error))
            {
                System.Diagnostics.Trace.WriteLine(error);
                notice = "暂时无法读取插件配置。";
            }
        }
        return new(configurations.ToArray(), CanSaveFromMenu(plugin.Definition), notice);
    }

    public async Task<SceneConfiguration> CaptureAsync(string provider, string target, string name, CancellationToken token)
    {
        var (store, plugin) = await ResolveAsync(provider, token);
        SceneConfiguration? saved = null;
        await WithDeviceAsync(store, plugin, target, async (session, values) =>
            saved = await CaptureAsync(store, plugin, session, target, name, values, token), token);
        return saved!;
    }

    internal static async Task<SceneConfiguration> CaptureAsync(PluginStore store, InstalledPackage plugin,
        PluginSession session, string target, string name, IReadOnlyDictionary<string, JsonElement> values, CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        if (SupportsNamedSave(plugin.Definition))
        {
            var saved = new SceneConfiguration(name, Selector: NamedSelector);
            if (ReadSelectors(plugin.Definition, values).Any(item => item.Configuration.Selector == NamedSelector
                && string.Equals(item.Configuration.Name, name, StringComparison.OrdinalIgnoreCase)))
                throw new InvalidDataException("preset.name-already-exists");
            var confirmed = RequireValues(await session.SetAsync(SaveNamedConfiguration, JsonSerializer.SerializeToElement(name), target));
            if (!ReadSelectors(plugin.Definition, confirmed).Any(item => item.Configuration == saved))
                throw new InvalidDataException("preset.not-found");
            return saved;
        }
        string id = Guid.NewGuid().ToString("N");
        var snapshot = plugin.Definition.Capabilities.Where(CanCapture)
            .Where(capability => values.TryGetValue(capability.Id, out var value)
                && (value.ValueKind != JsonValueKind.Array || value.GetArrayLength() > 0))
            .ToDictionary(capability => capability.Id, capability => values[capability.Id]);
        if (snapshot.Count == 0) throw new InvalidDataException("preset.no-readable-settings");
        store.SavePreset(new(1, plugin.Definition.Id, name, snapshot, id, target));
        return new(name, PresetId: id);
    }

    public async Task ApplyAsync(string target, SceneDeviceBinding binding, CancellationToken token)
    {
        var (store, plugin) = await ResolveAsync(binding.PluginId, token);
        await WithDeviceAsync(store, plugin, target,
            (session, values) => ApplyAsync(store, plugin, session, target, binding.Configuration, values), token);
    }

    internal static async Task<IReadOnlyDictionary<string, JsonElement>> ApplyAsync(PluginStore store, InstalledPackage plugin,
        PluginSession session, string target, SceneConfiguration configuration, IReadOnlyDictionary<string, JsonElement> values)
    {
        if (configuration.PresetId is { } id)
        {
            var preset = store.LoadPreset(plugin.Definition.Id, id, target) ?? throw new InvalidDataException("preset.not-found");
            var changed = preset.Values.Where(pair => !values.TryGetValue(pair.Key, out var current)
                    || !JsonElement.DeepEquals(pair.Value, current))
                .ToDictionary(pair => pair.Key, pair => pair.Value, StringComparer.Ordinal);
            return changed.Count == 0 ? values : RequireValues(await session.ApplyPresetAsync(preset with { Values = changed }, target));
        }
        foreach (var item in ReadSelectors(plugin.Definition, values))
        {
            if (item.Configuration != configuration) continue;
            string selector = item.Configuration.Selector!;
            var selected = JsonSerializer.SerializeToElement(new[] { item.Index });
            return JsonElement.DeepEquals(values[selector], selected) ? values : RequireValues(await session.SetAsync(selector, selected, target));
        }
        throw new InvalidDataException("preset.not-found");
    }

    private async Task<(PluginStore Store, InstalledPackage Plugin)> ResolveAsync(string provider, CancellationToken token)
    {
        if (lab is null) throw new InvalidDataException("plugin.not-installed");
        var (store, plugins) = await lab.RegisterAsync(definition => definition.Kind == "device" && definition.Id == provider)
            .WaitAsync(token);
        return (store, plugins.SingleOrDefault() ?? throw new InvalidDataException("plugin.not-installed"));
    }

    private async Task WithDeviceAsync(PluginStore store, InstalledPackage plugin, string target,
        Func<PluginSession, Dictionary<string, JsonElement>, Task> action, CancellationToken token)
    {
        await lab!.RunPageAsync(plugin, async () =>
        {
            var session = await lab.OpenInteractiveSessionAsync(store, plugin, retain: true, token, TimeSpan.FromSeconds(20));
            try
            {
                token.ThrowIfCancellationRequested();
                if (!session.Targets.Any(item => item.Id == target && item.Online && !item.Simulated))
                    throw new InvalidDataException("target.not-available");
                var values = RequireValues(await session.ReadAsync(target));
                token.ThrowIfCancellationRequested();
                await action(session, values);
            }
            finally { await lab.ReleaseInteractiveSessionAsync(plugin, session); }
        }, token);
        token.ThrowIfCancellationRequested();
    }

    internal static Dictionary<string, JsonElement> RequireValues(RpcResponse response) =>
        response.Success && response.Values is { } values ? values
            : throw new InvalidDataException(response.ErrorCode ?? "plugin.response-incomplete");

    private static bool CanCapture(CapabilityDefinition capability) =>
        capability.Writable && capability.Persistence == "device"
        && !Selectors.Any(ids => ids.Selector == capability.Id);

    private static bool SupportsSelector(PluginDefinition definition, (string Selector, string List) ids) =>
        definition.Capabilities.Any(item => item.Id == ids.Selector && item.Writable && item.Value.Type == "array")
        && definition.Capabilities.Any(item => item.Id == ids.List && item.Value.Type == "array");

    private static bool SupportsNamedSave(PluginDefinition definition) =>
        SupportsSelector(definition, Selectors[0])
        && definition.Capabilities.Any(item => item.Id == SaveNamedConfiguration && item.Writable && item.Value.Type == "string");

    internal static IEnumerable<(SceneConfiguration Configuration, int Index)> ReadSelectors(PluginDefinition definition,
        IReadOnlyDictionary<string, JsonElement> values)
    {
        foreach (var ids in Selectors)
        {
            if (!SupportsSelector(definition, ids) || !values.ContainsKey(ids.Selector)
                || !values.TryGetValue(ids.List, out var list) || list.ValueKind != JsonValueKind.Array) continue;
            foreach (var option in list.EnumerateArray())
                if (option.ValueKind == JsonValueKind.Object
                    && option.TryGetProperty("value", out var value) && value.ValueKind == JsonValueKind.Number
                    && value.TryGetInt32(out int index) && index > 0
                    && option.TryGetProperty("name", out var label) && label.ValueKind == JsonValueKind.String
                    && label.GetString() is { Length: > 0 } name)
                    yield return (new(name, Selector: ids.Selector), index);
        }
    }

    public static bool IsDeviceError(Exception error) => error is IOException or UnauthorizedAccessException
        or JsonException or InvalidDataException or TimeoutException or InvalidOperationException
        or System.ComponentModel.Win32Exception or System.Security.Cryptography.CryptographicException;
}

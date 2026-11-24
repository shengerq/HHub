using Hhub.Core;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using System.Text.Json;

namespace Hhub.App;

public sealed partial class MainWindow
{
    private (InstalledPlugin plugin, PluginCommand command)[] DeviceConfigurationCommands(DeviceRecord device) =>
        MatchingPlugins(device).SelectMany(plugin =>
        {
            var available = PluginPolicies.DeviceConfigurations(plugin.Manifest, device);
            if (OverviewCommand(plugin) is { SceneConfigurable: true } overview)
                available = available.Where(command => command.Id == overview.Id);
            return available.Select(command => (plugin, command));
        }).ToArray();

    private CommandForm CreateDeviceConfigurationForm(DeviceRecord device, InstalledPlugin plugin, PluginCommand command,
        IReadOnlyDictionary<string, JsonElement>? initial)
    {
        if (command.Id != plugin.Manifest.DeviceOverview?.ApplyCommand || command.Parameters.Count(parameter => parameter.Key != "deviceInstanceId") != 1)
            return CreateCommandForm(command with
            { Parameters = command.Parameters.Where(parameter => parameter.Key != "deviceInstanceId").ToArray() }, initial, showAdvanced: false);

        var parameter = command.Parameters.Single(item => item.Key != "deviceInstanceId");
        var overview = ReadOverview(plugin, device);
        string? saved = initial?.GetValueOrDefault(parameter.Key) is { ValueKind: JsonValueKind.String } value ? value.GetString() : null;
        var options = overview.Configurations.AsEnumerable();
        if (!string.IsNullOrEmpty(saved) && !overview.Configurations.Contains(saved, StringComparer.Ordinal))
            options = options.Append(saved);
        var picker = new ComboBox
        {
            Header = "设备配置",
            ItemsSource = options.ToArray(),
            SelectedItem = saved ?? overview.Current,
            PlaceholderText = overview.Configurations.Length > 0 ? "选择配置" : "请先在主页读取设备配置",
            HorizontalAlignment = HorizontalAlignment.Stretch
        };
        return new(picker, () => picker.SelectedItem is string name
            ? new() { [parameter.Key] = JsonSerializer.SerializeToElement(name) }
            : throw new InvalidDataException("请选择设备配置"));
    }

    private FrameworkElement DeviceConfigurationControls(DeviceRecord device)
    {
        var panel = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8 };
        var available = DeviceConfigurationCommands(device);
        if (OverviewPlugin(device) is { } provider && OverviewCommand(provider) is { SceneConfigurable: true })
        {
            var overview = ReadOverview(provider, device);
            var current = Text(overview.Current ?? "配置暂不可用", 13);
            current.Foreground = Muted;
            panel.Children.Add(current);
            panel.Children.Add(AsyncButton("绑定场景", () => AddDeviceConfigurationAsync(device)));
            return panel;
        }
        var configured = _bindings.Where(binding => device.ContainsInstance(binding.DeviceId))
            .Where(binding => available.Any(item => item.plugin.Manifest.Id == binding.PluginId && item.command.Id == binding.Command))
            .Select(binding => binding.SceneId).Distinct().ToArray();
        if (configured.Length == 0)
        {
            var empty = Text(available.Length > 0 ? "未配置" : "—", 13); empty.Foreground = Muted;
            if (available.Length == 0) ToolTipService.SetToolTip(empty, "此设备暂无可用的场景配置");
            panel.Children.Add(empty);
        }
        else
        {
            var selector = new ComboBox
            {
                ItemsSource = _profiles.Where(profile => configured.Contains(profile.Id)).ToArray(),
                DisplayMemberPath = "Name", SelectedItem = _profiles.FirstOrDefault(profile => profile.Id == CurrentScene?.Id && configured.Contains(profile.Id)),
                PlaceholderText = "选择配置", MinWidth = 110, MaxWidth = 160
            };
            selector.SelectionChanged += async (_, _) =>
            {
                if (selector.SelectedItem is SceneProfile scene && scene.Id != CurrentScene?.Id)
                    await RunUiActionAsync(() => SelectSceneAsync(scene));
            };
            panel.Children.Add(selector);
        }
        if (available.Length > 0) panel.Children.Add(AsyncButton("配置", () => AddDeviceConfigurationAsync(device)));
        return panel;
    }
}

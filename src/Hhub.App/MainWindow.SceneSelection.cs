using Hhub.Core;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace Hhub.App;

public sealed partial class MainWindow
{
    private (InstalledPlugin plugin, PluginCommand command)[] DeviceConfigurationCommands(DeviceRecord device) =>
        MatchingPlugins(device).SelectMany(plugin => PluginPolicies.DeviceConfigurations(plugin.Manifest, device).Select(command => (plugin, command))).ToArray();

    private FrameworkElement DeviceConfigurationControls(DeviceRecord device)
    {
        var panel = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8 };
        var available = DeviceConfigurationCommands(device);
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

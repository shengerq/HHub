using System.Text.Json;
using Hhub.Core;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace Hhub.App;

public sealed partial class MainWindow
{
    private string? _sceneDetailId;
    private TextBlock? _sceneForeground;
    private bool _sceneApplicationsExpanded;

    private void UpdateSceneTileStates()
    {
        foreach (var (id, view) in _sceneTiles)
        {
            bool current = id == CurrentScene?.Id;
            SetSceneTileAppearance(view, current, current && _sceneErrors.Count > 0);
            ToolTipService.SetToolTip(view, current ? "当前场景" : _profiles.FirstOrDefault(profile => profile.Id == id)?.Name);
        }
    }

    private void OpenSceneDetail(SceneProfile profile)
    {
        if (_sceneDetailId != profile.Id) _sceneApplicationsExpanded = false;
        _sceneDetailId = profile.Id; _page = "sceneDetail";
        _openDevice = null; _openPlugin = null;
        RefreshNavigation(); Render();
    }

    private string ConfigurationValues(string pluginId, string commandId, IReadOnlyDictionary<string, JsonElement> values)
    {
        var command = _installed.Plugins.FirstOrDefault(plugin => plugin.Manifest.Id == pluginId)?.Manifest.Commands.FirstOrDefault(command => command.Id == commandId);
        return string.Join("   ·   ", values.Where(value => value.Key != "deviceInstanceId").Select(value =>
        {
            string name = command?.Parameters.FirstOrDefault(parameter => parameter.Key == value.Key)?.Name ?? value.Key;
            string text = value.Value.ValueKind switch
            {
                JsonValueKind.String => value.Value.GetString() ?? "", JsonValueKind.True => "开", JsonValueKind.False => "关",
                JsonValueKind.Null => "—", _ => value.Value.GetRawText()
            };
            return name + "  " + text;
        }));
    }

    private Border SceneConfigurationRow(string title, string pluginId, string commandId, IReadOnlyDictionary<string, JsonElement> values, bool offline = false)
    {
        var plugin = _installed.Plugins.FirstOrDefault(plugin => plugin.Manifest.Id == pluginId);
        var command = plugin?.Manifest.Commands.FirstOrDefault(command => command.Id == commandId);
        var panel = new StackPanel { Spacing = 8 };
        panel.Children.Add(Text(title, BodyTextSize));
        string identity = (plugin?.Manifest.Name ?? pluginId) + "  /  " + (command?.Name ?? commandId);
        if (offline) identity += "  ·  设备已断开";
        var action = Text(identity, BodyTextSize); action.Foreground = Muted; panel.Children.Add(action);
        string parameters = ConfigurationValues(pluginId, commandId, values);
        if (parameters.Length > 0)
        {
            var text = Text(parameters, BodyTextSize); text.TextWrapping = TextWrapping.Wrap; panel.Children.Add(text);
        }
        return Card(panel, plugin is null || command is null || offline, new(16));
    }

    private void RenderSceneDetail()
    {
        var profile = _profiles.First(scene => scene.Id == _sceneDetailId);
        _title.Text = "场景 · " + profile.Name;
        _actions.Children.Add(IconButton("\uE72B", "返回场景", () => Navigate("scenes")));
        _actions.Children.Add(ActionMenu(("删除场景", () => DeleteSceneAsync(profile))));
        var layout = new Grid { RowSpacing = 16 };
        layout.RowDefinitions.Add(new() { Height = GridLength.Auto });
        layout.RowDefinitions.Add(new() { Height = new(1, GridUnitType.Star) });

        var applicationRow = new Grid { ColumnSpacing = 16 };
        applicationRow.ColumnDefinitions.Add(new() { Width = GridLength.Auto });
        applicationRow.ColumnDefinitions.Add(new() { Width = new(1, GridUnitType.Star) });
        applicationRow.Children.Add(Text("应用识别", SectionTextSize, true));
        _sceneForeground = Text("", BodyTextSize);
        _sceneForeground.Foreground = Muted;
        _sceneForeground.TextTrimming = TextTrimming.CharacterEllipsis;
        Grid.SetColumn(_sceneForeground, 1); applicationRow.Children.Add(_sceneForeground);
        var applications = new Expander
        {
            Header = applicationRow,
            HorizontalAlignment = HorizontalAlignment.Stretch,
            HorizontalContentAlignment = HorizontalAlignment.Stretch
        };
        applications.Expanding += (_, _) =>
        {
            _sceneApplicationsExpanded = true;
            applications.Content = SceneApplicationPicker(profile);
        };
        applications.Collapsed += (_, _) => _sceneApplicationsExpanded = false;
        applications.IsExpanded = _sceneApplicationsExpanded;
        Microsoft.UI.Xaml.Automation.AutomationProperties.SetName(applications, "应用识别：选择前台应用激活" + profile.Name);
        layout.Children.Add(applications);

        var configurations = new StackPanel { Spacing = 8 };
        configurations.Children.Add(Text("设备", SectionTextSize, true));
        var bindings = _bindings.Where(binding => binding.SceneId == profile.Id).ToArray();
        foreach (var binding in bindings)
        {
            var device = _devices.FirstOrDefault(device => device.ContainsInstance(binding.DeviceId));
            string title = device is null ? binding.DeviceId : Describe(device).Name;
            configurations.Children.Add(SceneConfigurationRow(title, binding.PluginId, binding.Command, binding.Values, device is null));
        }
        foreach (var device in _devices.Where(device => !bindings.Any(binding => device.ContainsInstance(binding.DeviceId))))
        {
            var row = new Grid { ColumnSpacing = 16 };
            row.ColumnDefinitions.Add(new() { Width = new(1, GridUnitType.Star) });
            row.ColumnDefinitions.Add(new() { Width = GridLength.Auto });
            row.Children.Add(Text(Describe(device).Name, BodyTextSize));
            var empty = Text("无配置", BodyTextSize); empty.Foreground = Muted;
            Grid.SetColumn(empty, 1); row.Children.Add(empty);
            configurations.Children.Add(Card(row, padding: new(16)));
        }
        if (bindings.Length == 0 && _devices.Count == 0) configurations.Children.Add(Text("暂无设备", BodyTextSize));
        if (profile.Actions.Length > 0)
        {
            var title = Text("插件配置", SectionTextSize, true); title.Margin = new(0, 16, 0, 0); configurations.Children.Add(title);
            foreach (var action in profile.Actions)
                configurations.Children.Add(SceneConfigurationRow(_installed.Plugins.FirstOrDefault(plugin => plugin.Manifest.Id == action.PluginId)?.Manifest.Name ?? action.PluginId,
                    action.PluginId, action.Command, action.Values));
        }
        var deviceView = Scroll(configurations);
        Grid.SetRow(deviceView, 1); layout.Children.Add(deviceView);
        _content.Children.Add(layout);
        UpdateSceneDetailApplications();
    }

    private static string SceneApplicationName(string application) => ScenePresets.ProcessName(application);

    private FrameworkElement SceneApplicationPicker(SceneProfile profile)
    {
        var saved = _rules.Where(rule => rule.SceneId == profile.Id && rule.Condition == "foreground").ToArray();
        var selected = saved.Where(rule => rule.Enabled).Select(rule => SceneApplicationName(rule.Application)).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var names = PlatformServices.CaptureActivity().RunningApplications
            .Concat(saved.Select(rule => SceneApplicationName(rule.Application)))
            .Distinct(StringComparer.OrdinalIgnoreCase).OrderBy(name => name, StringComparer.OrdinalIgnoreCase).ToArray();
        var search = new TextBox { PlaceholderText = "搜索应用", HorizontalAlignment = HorizontalAlignment.Stretch };
        var list = new StackPanel { Spacing = 4 };
        var rows = new List<(string Name, CheckBox View)>();
        foreach (string name in names)
        {
            var target = _rules.FirstOrDefault(rule => rule.Enabled && rule.Condition == "foreground" && rule.SceneId != null
                && rule.SceneId != profile.Id && SceneApplicationName(rule.Application).Equals(name, StringComparison.OrdinalIgnoreCase));
            string? targetName = _profiles.FirstOrDefault(scene => scene.Id == target?.SceneId)?.Name;
            var checkbox = new CheckBox { Content = ScenePresets.DisplayName(name) + (targetName is null ? "" : "  →  " + targetName), IsChecked = selected.Contains(name), HorizontalAlignment = HorizontalAlignment.Stretch };
            if (targetName is not null) ToolTipService.SetToolTip(checkbox, "选中后将前台触发场景改为" + profile.Name);
            checkbox.Checked += async (_, _) =>
            {
                selected.Add(name);
                await RunUiActionAsync(() => SaveSceneApplicationsAsync(profile, selected));
            };
            checkbox.Unchecked += async (_, _) =>
            {
                selected.Remove(name);
                await RunUiActionAsync(() => SaveSceneApplicationsAsync(profile, selected));
            };
            rows.Add((name, checkbox)); list.Children.Add(checkbox);
        }
        search.TextChanged += (_, _) =>
        {
            foreach (var row in rows) row.View.Visibility = (row.Name + " " + ScenePresets.DisplayName(row.Name)).Contains(search.Text.Trim(), StringComparison.OrdinalIgnoreCase) ? Visibility.Visible : Visibility.Collapsed;
        };
        var fields = new StackPanel { Spacing = 8, HorizontalAlignment = HorizontalAlignment.Stretch };
        fields.Children.Add(search);
        fields.Children.Add(new ScrollViewer { Content = list, MaxHeight = 200, VerticalScrollBarVisibility = ScrollBarVisibility.Auto });
        return fields;
    }

    private async Task SaveSceneApplicationsAsync(SceneProfile profile, HashSet<string> selected)
    {
        var saved = _rules.Where(rule => rule.SceneId == profile.Id && rule.Condition == "foreground").ToArray();
        // One foreground application has one scene assignment; background rules are preserved.
        _rules.RemoveAll(rule => rule.Condition == "foreground" && rule.SceneId != null
            && (rule.SceneId == profile.Id || selected.Contains(SceneApplicationName(rule.Application))));
        foreach (string name in selected)
        {
            var previous = saved.FirstOrDefault(rule => SceneApplicationName(rule.Application).Equals(name, StringComparison.OrdinalIgnoreCase));
            _rules.Add(previous is null
                ? new SceneRule { Name = name + " → " + profile.Name, Application = name, SceneId = profile.Id }
                : previous with { Enabled = true });
        }
        _storage.Save("scenes.json", _rules);
        _preferences = _preferences with { AutomaticScenes = true }; SavePreferences();
        _scenesPaused = false; _automaticSceneId = null; _appliedScenes.Clear();
        await EvaluateScenesAsync(); RefreshNavigation(); UpdateSceneDetailApplications();
    }

    private void UpdateSceneDetailApplications()
    {
        if (_hiddenToTray || _page != "sceneDetail" || _sceneForeground is null) return;
        var applications = _rules.Where(rule => rule.SceneId == _sceneDetailId && rule.Enabled && rule.Condition == "foreground")
            .Select(rule => SceneApplicationName(rule.Application)).Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
        _sceneForeground.Text = applications.Length == 0 ? "选择应用" : string.Join("、", applications.Select(ScenePresets.DisplayName));
        ToolTipService.SetToolTip(_sceneForeground, "当前前台：" + (PlatformServices.CaptureForegroundApplication() ?? "—"));
    }
}

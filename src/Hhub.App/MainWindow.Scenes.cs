using System.Text.Json;
using Hhub.Core;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Controls.Primitives;
using Microsoft.UI.Xaml.Markup;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Input;

namespace Hhub.App;

public sealed partial class MainWindow
{
    private const double SceneTileSize = ShellLayout.SceneTileSize;
    private readonly Dictionary<string, string> _requestedDeviceScenes = [];
    private readonly HashSet<string> _sceneActionAttempts = [];
    private readonly Dictionary<string, string> _sceneErrors = [];
    private readonly SemaphoreSlim _sceneGate = new(1, 1);
    private string? _automaticSceneId;
    private string? _lastAppliedSceneId;
    private bool _sceneViewDirty;
    private DateTimeOffset _sceneTapDeadline;
    private readonly Dictionary<string, Button> _sceneTiles = [];
    private readonly Dictionary<Button, Border[]> _sceneGlows = [];

    private void ResetSceneActionsForPlugin(string pluginId)
    {
        _sceneActionAttempts.RemoveWhere(key => key.Split('/').ElementAtOrDefault(1) == pluginId);
        foreach (string key in _sceneErrors.Keys.Where(key => key.Split('/').ElementAtOrDefault(1) == pluginId).ToArray()) _sceneErrors.Remove(key);
    }

    private void RenderScenes()
    {
        _sceneViewDirty = false;
        _sceneTiles.Clear();
        _title.Text = "场景";
        if (_scenePreview) { RenderScenePreview(); return; }
        _content.RowDefinitions.Add(new() { Height = GridLength.Auto });
        _content.RowDefinitions.Add(new() { Height = GridLength.Auto });
        _content.RowDefinitions.Add(new() { Height = new(1, GridUnitType.Star) });
        var rail = new Grid { ColumnSpacing = 12, HorizontalAlignment = HorizontalAlignment.Left, Margin = new(6) };
        for (int i = 0; i < _profiles.Count + 1; i++) rail.ColumnDefinitions.Add(new() { Width = new(SceneTileSize) });
        foreach (var profile in _profiles)
        { var tile = SceneTile(profile); Grid.SetColumn(tile, rail.Children.Count); rail.Children.Add(tile); }
        var addButton = CreateAddSceneButton();
        Grid.SetColumn(addButton, _profiles.Count); rail.Children.Add(addButton);
        var railScroll = new ScrollViewer
        {
            Margin = new(-6, 0, -6, 0),
            Content = rail, HorizontalContentAlignment = HorizontalAlignment.Left, HorizontalScrollMode = ScrollMode.Enabled, HorizontalScrollBarVisibility = ScrollBarVisibility.Auto,
            VerticalScrollMode = ScrollMode.Disabled, VerticalScrollBarVisibility = ScrollBarVisibility.Disabled
        };
        _content.Children.Add(railScroll);
        var heading = Text("设备", SectionTextSize, true); heading.Margin = new(0, 20, 0, 8);
        Grid.SetRow(heading, 1); _content.Children.Add(heading);
        FrameworkElement body;
        if (_devices.Count == 0)
            body = (FrameworkElement)Empty("\uE772", "尚无设备信息", AsyncButton("刷新设备", RefreshDevicesAsync, true));
        else
        {
            var rows = new StackPanel { Spacing = 8 };
            foreach (var device in _devices) rows.Children.Add(DeviceSceneRow(device));
            body = Scroll(rows);
        }
        Grid.SetRow(body, 2); _content.Children.Add(body);
    }

    private FrameworkElement SceneTile(SceneProfile profile)
    {
        bool current = profile.Id == CurrentScene?.Id;
        var panel = SceneTileContent(profile.Name);
        var button = Button("", () => { });
        button.IsDoubleTapEnabled = true;
        button.Click += async (_, _) =>
        {
            _sceneTapDeadline = DateTimeOffset.UtcNow.AddMilliseconds(PlatformServices.GetDoubleClickTime());
            await RunUiActionAsync(() => SelectSceneAsync(profile, refreshPage: false));
        };
        button.AddHandler(UIElement.DoubleTappedEvent,
            new DoubleTappedEventHandler((_, args) => { args.Handled = true; OpenSceneDetail(profile); }), handledEventsToo: true);
        _sceneTiles[profile.Id] = button;
        button.Content = panel;
        button.HorizontalAlignment = HorizontalAlignment.Left; button.Width = SceneTileSize; button.Height = ShellLayout.SceneTileHeight;
        button.Padding = new(16);
        button.HorizontalContentAlignment = HorizontalAlignment.Stretch;
        button.VerticalContentAlignment = VerticalAlignment.Stretch;
        button.BorderThickness = new(1); button.CornerRadius = new(4);
        var framed = FrameSceneTile(button);
        EnableConfigurationDrop(framed, profile.Id);
        SetSceneTileAppearance(button, current, current && _sceneErrors.Count > 0);
        ToolTipService.SetToolTip(button, current ? "当前场景" : profile.Name);
        Microsoft.UI.Xaml.Automation.AutomationProperties.SetName(button, profile.Name);
        var menu = new MenuFlyout();
        var configure = new MenuFlyoutItem { Text = "场景设置", Icon = new SymbolIcon(Symbol.Setting) };
        configure.Click += (_, _) => OpenSceneDetail(profile);
        menu.Items.Add(configure);
        var delete = new MenuFlyoutItem { Text = "删除场景", Icon = new SymbolIcon(Symbol.Delete), IsEnabled = _profiles.Count > 1 };
        delete.Click += async (_, _) => await RunUiActionAsync(() => DeleteSceneAsync(profile));
        menu.Items.Add(delete); button.ContextFlyout = menu;
        return framed;
    }

    private Grid SceneTileContent(string name)
    {
        var panel = new Grid();
        var label = Text(name, 16, true);
        label.HorizontalAlignment = HorizontalAlignment.Center;
        label.MaxWidth = SceneTileSize - 32;
        ToolTipService.SetToolTip(label, name);
        panel.Children.Add(label);
        return panel;
    }

    private void SetSceneTileAppearance(Button button, bool selected, bool error = false)
    {
        button.Style = (Style)Application.Current.Resources["AccentButtonStyle"];
        button.ClearValue(Control.BackgroundProperty);
        button.ClearValue(Control.ForegroundProperty);
        button.ClearValue(Control.BorderBrushProperty);
        if (_sceneGlows.TryGetValue(button, out var glows))
        {
            for (int index = 0; index < glows.Length; index++)
            {
                glows[index].Visibility = selected ? Visibility.Visible : Visibility.Collapsed;
                glows[index].BorderBrush = ColorBrush(_accentColor.R, _accentColor.G, _accentColor.B, (byte)(index == 0 ? 210 : index == 1 ? 90 : 35));
            }
        }
        if (error) button.BorderBrush = ErrorBrush;
    }

    private Grid FrameSceneTile(Button button)
    {
        var frame = new Grid { Width = SceneTileSize, Height = ShellLayout.SceneTileHeight };
        var glows = new Border[3];
        for (int index = 0; index < glows.Length; index++)
        {
            double spread = (index + 1) * 1.5;
            var ring = new Border { Margin = new(-spread), BorderThickness = new(1.5), CornerRadius = new(4 + spread),
                IsHitTestVisible = false, Visibility = Visibility.Collapsed };
            glows[index] = ring; frame.Children.Add(ring);
        }
        frame.Children.Add(button); _sceneGlows[button] = glows;
        return frame;
    }

    private Button CreateAddSceneButton()
    {
        var button = AsyncButton("", AddSceneAsync, true);
        button.Content = Icon("\uE710", 24);
        button.Width = SceneTileSize; button.Height = ShellLayout.SceneTileHeight;
        button.HorizontalAlignment = HorizontalAlignment.Left;
        button.HorizontalContentAlignment = HorizontalAlignment.Center;
        button.BorderThickness = new(1);
        ToolTipService.SetToolTip(button, "添加场景");
        Microsoft.UI.Xaml.Automation.AutomationProperties.SetName(button, "添加场景");
        return button;
    }

    private Task AddSceneAsync()
    {
        _sceneCreation = new();
        Navigate("sceneCreate"); return Task.CompletedTask;
    }

    private async Task SelectSceneAsync(SceneProfile profile, bool refreshPage = true)
    {
        if (_scenePreview) return;
        if (_scenesPaused && CurrentScene?.Id == profile.Id)
        {
            if (refreshPage) Render(); else UpdateSceneTileStates();
            return;
        }
        _scenesPaused = true;
        _automaticSceneId = null;
        _preferences = _preferences with { CurrentSceneId = profile.Id, AutomaticScenes = false };
        SavePreferences();
        RefreshNavigation();
        _sceneActionAttempts.Clear();
        await ApplySceneAsync(profile);
        _sceneViewDirty = true;
        if (refreshPage) Render(); else UpdateSceneTileStates();
    }

    private async Task ApplySceneAsync(SceneProfile profile)
    {
        await _sceneGate.WaitAsync();
        try
        {
            if (_lastAppliedSceneId != profile.Id)
            {
                _lastAppliedSceneId = profile.Id;
                _sceneActionAttempts.Clear(); _sceneErrors.Clear(); _requestedDeviceScenes.Clear();
            }
            var connected = _devices.SelectMany(device => device.MemberInstanceIds.Append(device.InstanceId)).ToHashSet(StringComparer.OrdinalIgnoreCase);
            var plan = ScenePlan.Build(profile, _bindings, connected);
            var validKeys = plan.Select(item => item.Key).ToHashSet();
            _sceneActionAttempts.RemoveWhere(key => !validKeys.Contains(key));
            foreach (string key in _sceneErrors.Keys.Where(key => !validKeys.Contains(key)).ToArray()) _sceneErrors.Remove(key);
            foreach (var item in plan)
            {
                if (item.DeviceId is not null && !_devices.Any(device => device.ContainsInstance(item.DeviceId))) continue;
                if (!_sceneActionAttempts.Add(item.Key)) continue;
                _sceneViewDirty = true;
                try
                {
                    var plugin = _installed.Plugins.FirstOrDefault(plugin => plugin.Manifest.Id == item.PluginId)
                        ?? throw new InvalidOperationException("缺少插件：" + item.PluginId);
                    if (item.DeviceId is not null && !PluginPolicies.DeviceConfigurations(plugin.Manifest, _devices.First(device => device.ContainsInstance(item.DeviceId))).Any(command => command.Id == item.Command))
                        throw new InvalidOperationException("插件未声明此设备配置能力");
                    await SendPluginCommandAsync(plugin, item.Command, item.Values);
                    _sceneErrors.Remove(item.Key);
                    if (item.DeviceId is not null) _requestedDeviceScenes[item.DeviceId] = profile.Id;
                }
                catch (Exception ex) when (ex is IOException or InvalidOperationException or System.ComponentModel.Win32Exception)
                { _sceneErrors[item.Key] = ex.Message; ShowError(ex.Message); }
            }
        }
        finally { _sceneGate.Release(); }
    }

    private Border DeviceSceneRow(DeviceRecord device)
    {
        var display = Describe(device);
        var row = new Grid { ColumnSpacing = 16, MinHeight = 56 };
        row.ColumnDefinitions.Add(new() { Width = new(40) });
        row.ColumnDefinitions.Add(new() { Width = new(1, GridUnitType.Star) });
        row.ColumnDefinitions.Add(new() { Width = GridLength.Auto });
        row.Children.Add(DeviceArtwork(device, 40));
        var details = new StackPanel { Spacing = 4, VerticalAlignment = VerticalAlignment.Center };
        details.Children.Add(Text(display.Name, BodyTextSize));
        var description = Text(HomeDeviceConnection(device, display)
            + (display.Battery is { } battery ? $" · {battery:0}%" : ""), 13);
        description.Foreground = Muted;
        details.Children.Add(description);
        Grid.SetColumn(details, 1); row.Children.Add(details);
        var configurations = DeviceConfigurationControls(device);
        configurations.VerticalAlignment = VerticalAlignment.Center;
        Grid.SetColumn(configurations, 2); row.Children.Add(configurations);
        return Card(row, padding: new(20, 12, 20, 12));
    }

    private async Task AddDeviceConfigurationAsync(DeviceRecord device)
    {
        var available = DeviceConfigurationCommands(device);
        if (available.Length == 0) { ShowError("此设备尚无可配置的适配插件"); return; }
        foreach (var (plugin, command) in available)
            if (plugin.Manifest.DeviceOverview is { } overview && command.Id == overview.ApplyCommand &&
                ReadOverview(plugin, device).Configurations.Length == 0 && overview.RefreshCommand is { } refresh)
                await SendPluginCommandAsync(plugin, refresh, new Dictionary<string, JsonElement>
                { ["deviceInstanceId"] = JsonSerializer.SerializeToElement(device.InstanceId) });
        var scenes = new ComboBox { Header = "场景", ItemsSource = _profiles.Select(scene => scene.Name).ToArray(), SelectedIndex = Math.Max(0, _profiles.FindIndex(profile => profile.Id == CurrentScene?.Id)), HorizontalAlignment = HorizontalAlignment.Stretch };
        var commands = new ComboBox { Header = "插件动作", ItemsSource = available.Select(item => $"{item.plugin.Manifest.Name} / {item.command.Name}").ToArray(), SelectedIndex = 0, HorizontalAlignment = HorizontalAlignment.Stretch };
        var values = new ContentControl { HorizontalContentAlignment = HorizontalAlignment.Stretch };
        CommandForm? form = null;
        void LoadConfiguration()
        {
            if (commands.SelectedIndex < 0 || scenes.SelectedIndex < 0) return;
            var selected = available[commands.SelectedIndex];
            var saved = _bindings.FirstOrDefault(binding => device.ContainsInstance(binding.DeviceId) && binding.SceneId == _profiles[scenes.SelectedIndex].Id
                && binding.PluginId == selected.plugin.Manifest.Id && binding.Command == selected.command.Id);
            form = CreateDeviceConfigurationForm(device, selected.plugin, selected.command, saved?.Values);
            values.Content = form.View;
        }
        LoadConfiguration();
        commands.SelectionChanged += (_, _) => LoadConfiguration();
        scenes.SelectionChanged += (_, _) => LoadConfiguration();
        var fields = new StackPanel { Spacing = 18, MinWidth = 360 };
        fields.Children.Add(scenes);
        if (available.Length > 1) fields.Children.Add(commands);
        fields.Children.Add(values);
        Dictionary<string, JsonElement>? parameters = null;
        var dialog = Dialog("添加配置", fields, "保存");
        dialog.PrimaryButtonClick += (_, args) =>
        {
            try { parameters = form?.Read() ?? throw new InvalidDataException(); }
            catch (InvalidDataException) { args.Cancel = true; }
        };
        if (await dialog.ShowAsync() != ContentDialogResult.Primary) return;
        string sceneId = _profiles[scenes.SelectedIndex].Id;
        var chosen = available[commands.SelectedIndex];
        var binding = new DeviceSceneBinding(device.InstanceId, sceneId, chosen.plugin.Manifest.Id, chosen.command.Id, parameters!);
        _bindings = PluginPolicies.SaveDeviceConfiguration(_bindings, binding, chosen.plugin.Manifest).ToList();
        _storage.Save("device-scenes.json", _bindings);
        _sceneActionAttempts.Clear();
        await EvaluateScenesAsync();
        Render();
    }

    private void RenderRules()
    {
        _title.Text = "自动规则";
        _actions.Children.Add(Button("返回场景", () => Navigate("scenes")));
        _actions.Children.Add(AsyncButton("新建规则", AddRuleAsync, true));
        if (_rules.Count == 0) { _content.Children.Add(Empty("\uE945", "还没有自动规则")); return; }
        var list = new StackPanel { Spacing = 12 };
        foreach (var rule in _rules)
        {
            var row = new Grid { ColumnSpacing = 20 };
            row.ColumnDefinitions.Add(new() { Width = new(1, GridUnitType.Star) });
            row.ColumnDefinitions.Add(new() { Width = GridLength.Auto });
            var details = new StackPanel { Spacing = 10 };
            var toggle = new ToggleSwitch { Header = rule.Name, IsOn = rule.Enabled, OnContent = "已启用", OffContent = "已停用" };
            toggle.Toggled += (_, _) =>
            {
                int index = _rules.FindIndex(item => item.Id == rule.Id);
                _rules[index] = rule with { Enabled = toggle.IsOn };
                _storage.Save("scenes.json", _rules);
                _appliedScenes.Remove(rule.Id);
            };
            details.Children.Add(toggle);
            string target = rule.SceneId is null ? rule.Command : _profiles.FirstOrDefault(scene => scene.Id == rule.SceneId)?.Name ?? "场景已移除";
            details.Children.Add(Text($"{rule.Application}  ·  {(rule.Condition == "foreground" ? "前台" : "运行中")}  →  {target}", BodyTextSize));
            row.Children.Add(details);
            var remove = Button("删除", () =>
            {
                _rules.RemoveAll(item => item.Id == rule.Id);
                _appliedScenes.Remove(rule.Id);
                _storage.Save("scenes.json", _rules); Render();
            });
            remove.VerticalAlignment = VerticalAlignment.Center;
            Grid.SetColumn(remove, 1); row.Children.Add(remove);
            list.Children.Add(Card(row));
        }
        _content.Children.Add(Scroll(list));
    }

    private async Task AddRuleAsync()
    {
        var name = new TextBox { Header = "名称", PlaceholderText = "规则名称" };
        var application = new TextBox { Header = "应用", PlaceholderText = "应用进程名，例如 notepad" };
        var condition = new ComboBox { Header = "条件", HorizontalAlignment = HorizontalAlignment.Stretch, ItemsSource = new[] { "应用处于前台", "应用正在运行" }, SelectedIndex = 0 };
        var action = new ComboBox { Header = "目标场景", HorizontalAlignment = HorizontalAlignment.Stretch, ItemsSource = _profiles.Select(scene => scene.Name).ToArray(), SelectedIndex = 0 };
        var priority = new NumberBox { Header = "优先级", Value = 10, Minimum = 0, Maximum = 100, SpinButtonPlacementMode = NumberBoxSpinButtonPlacementMode.Inline };
        var fields = new StackPanel { Spacing = 16, MinWidth = 360 };
        foreach (var field in new UIElement[] { name, application, condition, action, priority }) fields.Children.Add(field);
        var dialog = Dialog("新建规则", fields, "创建");
        dialog.PrimaryButtonClick += (_, args) =>
        {
            if (string.IsNullOrWhiteSpace(name.Text)) { args.Cancel = true; name.BorderBrush = ErrorBrush; }
            if (string.IsNullOrWhiteSpace(application.Text)) { args.Cancel = true; application.BorderBrush = ErrorBrush; }
        };
        if (await dialog.ShowAsync() != ContentDialogResult.Primary) return;
        _rules.Add(new SceneRule { Name = name.Text.Trim(), Application = application.Text.Trim(), Condition = condition.SelectedIndex == 0 ? "foreground" : "running", Priority = (int)priority.Value, SceneId = _profiles[action.SelectedIndex].Id });
        _storage.Save("scenes.json", _rules);
        Render();
    }

    private async Task EvaluateScenesAsync()
    {
        if (_scenePreview || _sceneBusy || _closing) return;
        if (_rules.Count == 0 && _bindings.Count == 0 && !_profiles.Any(scene => scene.Actions.Length > 0)) return;
        _sceneBusy = true;
        try
        {
            var selected = !_scenesPaused && _rules.Any(rule => rule.Enabled)
                ? SceneEngine.Evaluate(_rules, PlatformServices.CaptureActivity(
                    includeRunningApplications: _rules.Any(rule => rule.Enabled && rule.Condition == "running")))
                : Array.Empty<SceneRule>();
            string? next = selected.FirstOrDefault(rule => rule.SceneId is not null)?.SceneId;
            bool changed = next != _automaticSceneId;
            _automaticSceneId = next;
            if (CurrentScene is { } profile) await ApplySceneAsync(profile);
            _appliedScenes.IntersectWith(selected.Select(rule => rule.Id));
            foreach (var rule in selected.Where(rule => rule.SceneId is null && !_appliedScenes.Contains(rule.Id)))
            {
                _appliedScenes.Add(rule.Id);
                var plugin = _installed.Plugins.FirstOrDefault(item => item.Manifest.Id == rule.PluginId);
                if (plugin is null) continue;
                await SendPluginCommandAsync(plugin, rule.Command, rule.Values);
            }
            if (changed) { RefreshNavigation(); if (_page == "home" && _primaryDevice is { } device && _homeDeviceHost is not null) _homeDeviceHost.Content = PrimaryDevice(device); }
            QueueActivation();
            if ((changed || _sceneViewDirty) && _page is "scenes" or "rules")
            {
                if (_page == "scenes" && DateTimeOffset.UtcNow < _sceneTapDeadline) UpdateSceneTileStates();
                else Render();
            }
        }
        catch (Exception ex) when (ex is IOException or InvalidOperationException or System.ComponentModel.Win32Exception)
        { ShowError(ex.Message); }
        finally { _sceneBusy = false; }
    }
}

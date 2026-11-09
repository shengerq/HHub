using System.Text.Json;
using Hhub.Core;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace Hhub.App;

public sealed partial class MainWindow
{
    private string _settingsSection = Environment.GetCommandLineArgs().Contains("--sponsor") ? "赞助"
        : Environment.GetCommandLineArgs().Contains("--about") ? "关于"
        : Environment.GetCommandLineArgs().Contains("--plugin-settings") ? "插件" : "常规";
    private ContentControl? _settingsBody;
    private SelectorBar? _settingsTabs;
    private readonly List<TextBlock> _settingsMutedViews = [];
    private readonly Dictionary<FrameworkElement, TextBlock> _settingsRowNotices = [];

    private void SetSettingNotice(FrameworkElement control, string? message)
    {
        if (!_settingsRowNotices.TryGetValue(control, out var notice)) return;
        notice.Text = message ?? "";
        notice.Visibility = string.IsNullOrWhiteSpace(message) ? Visibility.Collapsed : Visibility.Visible;
    }

    private TextBlock SettingsMutedText(string value, double size = 13)
    {
        var text = Text(value, size); text.Foreground = Muted;
        _settingsMutedViews.Add(text); return text;
    }

    private void ShowSettingsSection(string section)
    {
        _settingsSection = section;
        if (_settingsTabs is not null) _settingsTabs.SelectedItem = null;
        RenderSettingsSection();
    }

    private FrameworkElement SettingRow(string name, FrameworkElement control, string? reason = null)
    {
        var row = new Grid { ColumnSpacing = 16 };
        row.ColumnDefinitions.Add(new() { Width = new(20) });
        row.ColumnDefinitions.Add(new() { Width = new(1, GridUnitType.Star) });
        row.ColumnDefinitions.Add(new() { Width = GridLength.Auto });
        string glyph = name switch
        {
            "开机自启动" or "启动后进入托盘" => "\uE7E8", "最小化到托盘" or "关闭窗口" => "\uE8BB",
            "托盘效率模式" => "\uE945", "托盘显示设备电量" => "\uE83F",
            "主题" => "\uE790", "透明背景" => "\uE7F4", "收起侧边栏" => "\uE700",
            "软件更新" or "自动更新" => "\uE895", "更新渠道" => "\uE8AB",
            "监测来源" or "系统详情" => "\uE9D9",
            "采样间隔（秒）" or "应用识别间隔" => "\uE917",
            "自动识别设备插拔" or "重新识别设备" or "清除主页设备选择" => "\uE772",
            "插件目录" => "\uE8B7", "自动切换场景" or "自动规则" => "\uECA5", _ => "\uEA86"
        };
        row.Children.Add(Icon(glyph, 20));
        var labels = new StackPanel { Spacing = 3, VerticalAlignment = VerticalAlignment.Center };
        var label = Text(name, BodyTextSize); label.TextWrapping = TextWrapping.Wrap; label.TextTrimming = TextTrimming.None;
        labels.Children.Add(label);
        var notice = SettingsMutedText(""); notice.TextWrapping = TextWrapping.Wrap; notice.Visibility = Visibility.Collapsed;
        labels.Children.Add(notice); _settingsRowNotices[control] = notice;
        if (control is Panel group)
            foreach (var child in group.Children.OfType<FrameworkElement>()) _settingsRowNotices[child] = notice;
        SetSettingNotice(control, reason);
        Grid.SetColumn(labels, 1); row.Children.Add(labels);
        Microsoft.UI.Xaml.Automation.AutomationProperties.SetName(control, name);
        if (control is Button action)
        {
            action.MinWidth = 88;
            action.Style = (Style)Application.Current.Resources["AccentButtonStyle"];
        }
        if (control is Panel actions)
            foreach (var button in actions.Children.OfType<Button>()) button.Style = (Style)Application.Current.Resources["AccentButtonStyle"];
        control.VerticalAlignment = VerticalAlignment.Center; control.HorizontalAlignment = HorizontalAlignment.Right;
        Grid.SetColumn(control, 2); row.Children.Add(control);
        var card = Card(row, padding: new(16, 10, 16, 10));
        card.MinHeight = 64;
        return card;
    }

    private ToggleSwitch SettingToggle(bool value, Action<bool> update)
    {
        var toggle = new ToggleSwitch { IsOn = value, OnContent = null, OffContent = null, MinWidth = 0, Width = 40,
            HorizontalAlignment = HorizontalAlignment.Right, Padding = new(0) };
        toggle.Toggled += (_, _) =>
        {
            try { SetSettingNotice(toggle, null); update(toggle.IsOn); }
            catch (Exception error) when (error is IOException or InvalidOperationException or UnauthorizedAccessException
                or System.ComponentModel.Win32Exception or ArgumentException)
            { SetSettingNotice(toggle, error.Message); }
        };
        return toggle;
    }

    private ToggleSwitch SettingAsyncToggle(bool value, Func<bool, Task> update)
    {
        var toggle = SettingToggle(value, _ => { });
        toggle.Toggled += async (_, _) =>
        {
            toggle.IsEnabled = false;
            try { await update(toggle.IsOn); }
            catch (Exception error) when (error is IOException or InvalidOperationException or UnauthorizedAccessException
                or System.ComponentModel.Win32Exception or ArgumentException or System.Text.Json.JsonException)
            { SetSettingNotice(toggle, error.Message); }
            finally { toggle.IsEnabled = true; }
        };
        return toggle;
    }

    private void RenderSettings()
    {
        _title.Text = "设置";
        var sponsor = IconButton("\uEB51", "赞助", () => ShowSettingsSection("赞助"));
        _actions.Children.Add(sponsor);
        _content.RowDefinitions.Add(new() { Height = GridLength.Auto });
        _content.RowDefinitions.Add(new() { Height = new(1, GridUnitType.Star) });
        var tabs = SectionTabs(new[] { "常规", "外观", "监测", "设备", "场景", "插件", "关于" }.Select(name => (name, name)), _settingsSection,
            section => { _settingsSection = section; RenderSettingsSection(); });
        tabs.Margin = new(0, 0, 0, 16);
        _settingsTabs = tabs;
        _content.Children.Add(tabs);
        _settingsBody = new ContentControl
        {
            HorizontalContentAlignment = HorizontalAlignment.Stretch,
            VerticalContentAlignment = VerticalAlignment.Stretch
        };
        Grid.SetRow(_settingsBody, 1); _content.Children.Add(_settingsBody);
        RenderSettingsSection(animate: false);
    }

    private void RenderSettingsSection(bool animate = true)
    {
        if (_settingsBody is null) return;
        _settingsMutedViews.Clear();
        _settingsRowNotices.Clear();
        var panel = new StackPanel { Spacing = 8 };
        switch (_settingsSection)
        {
            case "赞助": RenderSponsorSettings(panel); break;
            case "关于": RenderAboutSettings(panel); break;
            case "更新": RenderAppUpdateSettings(panel); break;
            case "插件": RenderPluginSettings(panel); break;
            case "常规":
                var startup = new StartupRegistration();
                bool autoStart = startup.IsEnabled(Environment.ProcessPath!);
                var startToTray = SettingToggle(_preferences.StartToTray, enabled =>
                { _preferences = _preferences with { StartToTray = enabled }; SavePreferences(); });
                startToTray.IsEnabled = autoStart;
                ToggleSwitch? startupToggle = null;
                bool restoringStartup = false;
                startupToggle = SettingToggle(autoStart, enabled =>
                {
                    if (restoringStartup) return;
                    try
                    {
                        startup.SetEnabled(Environment.ProcessPath!, enabled);
                        autoStart = enabled;
                        startToTray.IsEnabled = enabled;
                        SetSettingNotice(startToTray, enabled ? null : "请先开启开机自启动");
                        SetSettingNotice(startupToggle!, null);
                        ToolTipService.SetToolTip(startupToggle!, null);
                    }
                    catch (Exception error) when (error is UnauthorizedAccessException or System.Security.SecurityException or IOException)
                    {
                        restoringStartup = true; startupToggle!.IsOn = autoStart; restoringStartup = false;
                        ToolTipService.SetToolTip(startupToggle, error.Message);
                        SetSettingNotice(startupToggle, "无法更改开机启动：" + error.Message);
                    }
                });
                panel.Children.Add(SettingRow("开机自启动", startupToggle));
                panel.Children.Add(SettingRow("启动后进入托盘", startToTray, autoStart ? null : "请先开启开机自启动"));
                panel.Children.Add(SettingRow("最小化到托盘", SettingToggle(_preferences.MinimizeToTray, enabled =>
                { _preferences = _preferences with { MinimizeToTray = enabled }; SavePreferences(); })));
                panel.Children.Add(SettingRow("关闭窗口", SettingsChoice(_preferences.CloseToTray ? "tray" : "exit",
                    [("tray", "收起到托盘"), ("exit", "退出应用")], mode =>
                    { _preferences = _preferences with { CloseToTray = mode == "tray" }; SavePreferences(); return Task.CompletedTask; })));
                panel.Children.Add(SettingRow("托盘效率模式", SettingToggle(_preferences.EfficiencyInTray, enabled =>
                { _preferences = _preferences with { EfficiencyInTray = enabled }; SavePreferences(); ApplyTrayEfficiency(); })));
                panel.Children.Add(SettingRow("托盘显示设备电量", SettingToggle(_preferences.TrayBattery, enabled =>
                { _preferences = _preferences with { TrayBattery = enabled }; SavePreferences(); UpdateTrayBatteries(); })));
                break;
            case "外观":
                panel.Children.Add(SettingRow("主题", SettingsChoice(_preferences.Theme,
                    [("system", "跟随 Windows"), ("light", "浅色"), ("dark", "深色")], theme =>
                    {
                        _preferences = _preferences with { Theme = theme }; SavePreferences(); ApplyTheme(); return Task.CompletedTask;
                    })));
                panel.Children.Add(SettingRow("透明背景", SettingToggle(_preferences.TranslucentBackground, value =>
                { _preferences = _preferences with { TranslucentBackground = value }; SavePreferences(); ApplyTheme(); })));
                panel.Children.Add(SettingRow("收起侧边栏", SettingToggle(_preferences.SidebarCollapsed, value =>
                { _preferences = _preferences with { SidebarCollapsed = value }; SavePreferences(); RefreshNavigation(); })));
                break;
            case "监测":
                var sources = _installed.Plugins.Where(plugin => plugin.Manifest.Category == "monitor").ToArray();
                var source = SettingsChoice(MonitorPlugin?.Manifest.Id ?? "", sources.Select(plugin => (plugin.Manifest.Id, plugin.Manifest.Name)), id =>
                {
                    _preferences = _preferences with { MonitorPluginId = id }; SavePreferences(); QueueActivation(); return Task.CompletedTask;
                });
                source.IsEnabled = sources.Length > 0; panel.Children.Add(SettingRow("监测来源", source, sources.Length == 0 ? "尚未安装系统监测插件" : null));
                var interval = new NumberBox { Minimum = 1, Maximum = 10, Value = 1, Width = 110, SpinButtonPlacementMode = NumberBoxSpinButtonPlacementMode.Compact };
                if (MonitorPlugin is { } monitor && _runtime.Snapshot(monitor.Manifest.Id).TryGetValue("monitor.intervalSeconds", out var current) && current.TryGetDouble(out var seconds)) interval.Value = seconds;
                var refresh = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 12 };
                refresh.Children.Add(interval);
                var apply = AsyncButton("应用", () => MonitorActionAsync("monitor.interval", new() { ["seconds"] = JsonSerializer.SerializeToElement(interval.Value) }));
                apply.IsEnabled = MonitorCommand("monitor.interval"); refresh.Children.Add(apply);
                panel.Children.Add(SettingRow("采样间隔（秒）", refresh));
                if (MonitorCommand("monitor.sensors.access") && MonitorPlugin is { } sensorPlugin)
                {
                    bool enabled = !_runtime.Snapshot(sensorPlugin.Manifest.Id).TryGetValue("monitor.sensorAccessEnabled", out var access) || access.ValueKind != JsonValueKind.False;
                    panel.Children.Add(SettingRow("硬件传感器读取", SettingAsyncToggle(enabled, value =>
                        MonitorActionAsync("monitor.sensors.access", new() { ["enabled"] = JsonSerializer.SerializeToElement(value) }))));
                }
                panel.Children.Add(SettingRow("系统详情", Button("打开", () => Navigate("system"))));
                break;
            case "设备":
                panel.Children.Add(SettingRow("自动识别设备插拔", SettingToggle(_preferences.WatchDevices, value =>
                {
                    _preferences = _preferences with { WatchDevices = value }; SavePreferences();
                    if (value) StartWatchingDevices(); else StopWatchingDevices();
                })));
                panel.Children.Add(SettingRow("重新识别设备", AsyncButton("重新识别", RefreshDevicesAsync)));
                panel.Children.Add(SettingRow("清除主页设备选择", Button("清除选择", () =>
                { _preferences = _preferences with { PrimaryDeviceId = null }; SavePreferences(); })));
                break;
            case "场景":
                var automatic = SettingAsyncToggle(!_scenesPaused, async value =>
                {
                    string baseline = CurrentScene?.Id ?? _preferences.CurrentSceneId;
                    _preferences = _preferences with { AutomaticScenes = value, CurrentSceneId = baseline }; SavePreferences();
                    _scenesPaused = !value; _automaticSceneId = null; _appliedScenes.Clear();
                    await EvaluateScenesAsync(); RefreshNavigation();
                });
                panel.Children.Add(SettingRow("自动切换场景", automatic));
                panel.Children.Add(SettingRow("应用识别间隔", SettingsChoice(_preferences.SceneIntervalSeconds.ToString(), new[] { 1, 2, 5, 10 }.Select(seconds => (seconds.ToString(), $"{seconds} 秒")), value =>
                {
                    int seconds = int.Parse(value); _preferences = _preferences with { SceneIntervalSeconds = seconds };
                    SavePreferences(); _sceneTimer.Interval = TimeSpan.FromSeconds(seconds); return Task.CompletedTask;
                })));
                panel.Children.Add(SettingRow("自动规则", Button("管理", () => Navigate("rules"))));
                break;
        }
        _settingsBody.ContentTransitions = animate ? PageEntrance() : new();
        _settingsBody.Content = Scroll(panel);
    }
}

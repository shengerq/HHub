using System.Diagnostics;
using Hhub.Core;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Windows.Storage.Pickers;
using WinRT.Interop;

namespace Hhub.App;

public sealed partial class MainWindow
{
    private readonly Dictionary<string, Border> _pluginCards = [];

    private void UpdatePluginCardStates()
    {
        foreach (var (id, card) in _pluginCards)
        {
            card.BorderBrush = _runtime.Error(id) is null ? Stroke : ErrorBrush;
            ToolTipService.SetToolTip(card, _runtime.Error(id));
        }
    }
    private void ReloadPlugins(string? changedPluginId = null)
    {
        if (changedPluginId is not null) _pluginUiPaths.Remove(changedPluginId);
        if (changedPluginId is not null && _lastMonitorFrame?.Id == changedPluginId) _lastMonitorFrame = null;
        _installed = _catalog.Discover();
        RebuildPluginNavigation();
        if (_installed.Errors.Count > 0) ShowError(string.Join("；", _installed.Errors));
    }

    private void RenderPlugins()
    {
        _title.Text = "插件";
        if (_pluginTargetDevice is not null)
            _actions.Children.Add(Button("显示全部", () => { _pluginTargetDevice = null; Render(); }));
        var install = AsyncButton("本地安装", InstallAsync, true); install.Width = 88;
        var settings = Button("插件设置", OpenPluginManagementSettings, true); settings.Width = 88;
        var actions = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8 };
        actions.Children.Add(install); actions.Children.Add(settings);
        var page = new Grid();
        page.RowDefinitions.Add(new() { Height = GridLength.Auto });
        page.RowDefinitions.Add(new() { Height = new(1, GridUnitType.Star) });
        var toolbar = new Grid { ColumnSpacing = 24, Margin = new(0, 0, 0, 8) };
        toolbar.ColumnDefinitions.Add(new() { Width = GridLength.Auto });
        toolbar.ColumnDefinitions.Add(new() { Width = new(1, GridUnitType.Star) });
        var tabs = SectionTabs([("market", "市场"), ("manage", "管理")], _pluginTab, tab => { _pluginTab = tab; Render(); });
        toolbar.Children.Add(tabs);
        var search = new AutoSuggestBox { PlaceholderText = "搜索插件", Text = _pluginSearch, QueryIcon = new SymbolIcon(Symbol.Find), MinWidth = 0, Width = 184, VerticalAlignment = VerticalAlignment.Center };
        search.CornerRadius = new(4); search.Height = 32; search.HorizontalAlignment = HorizontalAlignment.Right;
        search.TextChanged += (_, _) => { _pluginSearch = search.Text; PopulatePluginRows(); };
        _actions.Children.Add(search); _actions.Children.Add(actions);
        page.Children.Add(toolbar);
        _pluginRowsHost = new Grid();
        Grid.SetRow(_pluginRowsHost, 1); page.Children.Add(_pluginRowsHost);
        _content.Children.Add(page);
        PopulatePluginRows();
    }

    private void PopulatePluginRows()
    {
        if (_pluginRowsHost is null) return;
        _pluginCards.Clear();
        _pluginRowsHost.Children.Clear();
        if (_pluginTab == "market")
        {
            if (PluginMarkets.Configured(_preferences).Length > 0)
            {
                _pluginRowsHost.Children.Add(Scroll(RemoteMarketRows()));
                return;
            }
            if (_bundledPackages.Length > 0)
            {
                _pluginRowsHost.Children.Add(Scroll(BundledPluginRows()));
                return;
            }
            _pluginRowsHost.Children.Add(Empty("\uE7BF", "插件市场尚未连接", AsyncButton("本地安装", InstallAsync, true)));
            return;
        }
        var plugins = _installed.Plugins.Where(plugin =>
            (_pluginTargetDevice is null || PluginPolicies.MatchesDevice(plugin.Manifest, _pluginTargetDevice)) &&
            $"{plugin.Manifest.Name} {plugin.Manifest.Id} {plugin.Manifest.Author} {plugin.Manifest.Description} {string.Join(' ', plugin.Manifest.DeviceNames)} {string.Join(' ', plugin.Manifest.DeviceMatches.Select(match => match.Vid + " " + match.Pid))}"
                .Contains(_pluginSearch, StringComparison.OrdinalIgnoreCase)).ToArray();
        _pluginRowsHost.Children.Add(plugins.Length == 0
            ? Empty("\uEA86", string.IsNullOrEmpty(_pluginSearch) ? "还没有插件" : "没有匹配的插件", Button("打开插件目录", OpenPluginFolder))
            : Scroll(PluginRows(plugins)));
    }

    private StackPanel PluginRows(IEnumerable<InstalledPlugin> plugins)
    {
        var list = new StackPanel { Spacing = 8 };
        foreach (var plugin in plugins)
        {
            string id = plugin.Manifest.Id;
            var row = new Grid { ColumnSpacing = 16, MinHeight = 48 };
            foreach (var width in new[] { new GridLength(32), new GridLength(1, GridUnitType.Star), GridLength.Auto })
                row.ColumnDefinitions.Add(new() { Width = width });
            string glyph = plugin.Manifest.Category switch
            {
                "monitor" => "\uE9D9", "device" => "\uE772", "theme" => "\uE790", _ => "\uEA86"
            };
            row.Children.Add(plugin.Manifest.Navigation?.PathData is not null
                ? PluginNavigationIcon(plugin.Manifest.Navigation) : Icon(glyph, 24));
            var identity = new StackPanel { Spacing = 2, VerticalAlignment = VerticalAlignment.Center };
            identity.Children.Add(Text(plugin.Manifest.Name, BodyTextSize));
            var metadata = Text(string.IsNullOrEmpty(plugin.Manifest.Author) ? plugin.Manifest.Version : $"{plugin.Manifest.Version} · {plugin.Manifest.Author}", 12);
            metadata.Foreground = Muted; identity.Children.Add(metadata);
            var name = new Button
            {
                Content = identity, Padding = new(0), BorderThickness = new(0),
                Background = new SolidColorBrush(Microsoft.UI.Colors.Transparent), HorizontalAlignment = HorizontalAlignment.Stretch,
                HorizontalContentAlignment = HorizontalAlignment.Left
            };
            name.Click += async (_, _) => await RunUiActionAsync(() => OpenPluginNavigationAsync(plugin));
            ToolTipService.SetToolTip(name, $"{plugin.Manifest.Id}\n{plugin.Manifest.Version}\n{plugin.Manifest.Author}");
            Grid.SetColumn(name, 1); row.Children.Add(name);
            var actions = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8, VerticalAlignment = VerticalAlignment.Center };
            actions.Children.Add(PluginUpdateButton(plugin));
            actions.Children.Add(AsyncButton("卸载", () => UninstallPluginAsync(plugin), true));
            Grid.SetColumn(actions, 2); row.Children.Add(actions);
            var card = Card(row, _runtime.Error(id) is not null, new(20, 12, 20, 12));
            _pluginCards[id] = card;
            if (_runtime.Error(id) is { } error) ToolTipService.SetToolTip(card, error);
            list.Children.Add(card);
        }
        return list;
    }

    private async Task InstallAsync()
    {
        var picker = new FileOpenPicker();
        InitializeWithWindow.Initialize(picker, WindowNative.GetWindowHandle(this));
        picker.FileTypeFilter.Add(".hhub");
        picker.FileTypeFilter.Add(".zip");
        var files = await picker.PickMultipleFilesAsync();
        if (files.Count == 0) return;
        await InstallLocalPackagesAsync(files.Select(file => file.Path));
    }

    private void OpenPluginFolder() => Process.Start(new ProcessStartInfo(_storage.Plugins) { UseShellExecute = true });

    private void RenderPlugin(InstalledPlugin plugin)
    {
        var pluginPage = _pluginSettingsOpen ? plugin.Manifest.SettingsPage : plugin.Manifest.Page;
        _title.Text = pluginPage?.Title ?? plugin.Manifest.Name;
        if (plugin.Manifest.SettingsPage is not null && pluginPage?.ShowSettingsShortcut != false)
            _titleControls.Children.Add(IconButton("\uE713", "插件设置", () => { _pluginSettingsOpen = true; Render(); }));
        if (pluginPage?.ShowManagementActions != false)
        {
            _actions.Children.Add(IconButton("\uE72B", "返回", () => { _openPlugin = null; Render(); }));
            _actions.Children.Add(PluginUpdateButton(plugin));
            _actions.Children.Add(ActionMenu(("重新启动", () => SetPluginModeAsync(plugin,
                PluginMode(plugin.Manifest.Id) == "disabled" ? "onDemand" : PluginMode(plugin.Manifest.Id), restart: true))));
            _actions.Children.Add(AsyncButton("卸载", () => UninstallPluginAsync(plugin), true));
        }
        if (pluginPage?.Layout == "declarative")
        {
            var shell = new Grid();
            var loading = Text(pluginPage.Title + " · 正在加载…", BodyTextSize);
            loading.Margin = new Thickness(16);
            loading.Foreground = Muted;
            loading.VerticalAlignment = VerticalAlignment.Top;
            shell.Children.Add(loading);
            _content.Children.Add(shell);
            AfterPageFirstFrame(shell, () => _ = MountPluginPageAsync(plugin, pluginPage, shell, loading));
            return;
        }
        if (pluginPage is null || pluginPage.Elements.Length == 0)
        {
            _content.Children.Add(Empty("\uE8A7", "独立窗口插件"));
            return;
        }
        var grid = new Grid { ColumnSpacing = 16, RowSpacing = 16 };
        int columns = _compact ? 2 : 3;
        for (int i = 0; i < columns; i++) grid.ColumnDefinitions.Add(new() { Width = new(1, GridUnitType.Star) });
        int index = 0;
        foreach (var element in pluginPage.Elements)
        {
            if (index % columns == 0) grid.RowDefinitions.Add(new() { Height = new(180) });
            var panel = new StackPanel { Spacing = 18, VerticalAlignment = VerticalAlignment.Center };
            panel.Children.Add(Text(element.Label, BodyTextSize, true));
            switch (element.Type)
            {
                case "metric": panel.Children.Add(MetricText(plugin.Manifest.Id, element.Key ?? "", "", 36)); break;
                case "command":
                    var execute = AsyncButton("执行", () => ExecutePluginPageCommandAsync(plugin, element.Command ?? ""), true);
                    execute.IsEnabled = _openDevice is null || _devices.Any(device => device.ContainsInstance(_openDevice.InstanceId));
                    panel.Children.Add(execute);
                    break;
                default: panel.Children.Add(Text("不支持的控件")); break;
            }
            var card = Card(panel);
            Grid.SetRow(card, index / columns); Grid.SetColumn(card, index % columns);
            grid.Children.Add(card);
            index++;
        }
        _content.RowDefinitions.Add(new() { Height = GridLength.Auto });
        _content.RowDefinitions.Add(new() { Height = new(1, GridUnitType.Star) });
        _content.Children.Add(DiagnosticsHost(plugin.Manifest.Id));
        var page = Scroll(grid); Grid.SetRow(page, 1); _content.Children.Add(page);
    }

    private ContentDialog Dialog(string title, object content, string primary) => new()
    {
        Title = title, Content = content, PrimaryButtonText = primary, CloseButtonText = "取消",
        XamlRoot = _root.XamlRoot, RequestedTheme = _root.ActualTheme
    };

}

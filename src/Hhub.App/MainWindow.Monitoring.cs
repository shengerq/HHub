using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Text.Json;
using Hhub.Core;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Markup;

namespace Hhub.App;

public sealed partial class MainWindow
{
    private string _monitorTab = "sensors";
    private readonly ObservableCollection<MonitorDetailRow> _monitorRows = [];
    private Button? _monitorPause;
    private ComboBox? _monitorNetwork;
    private TextBlock? _monitorEmpty;
    private TextBlock? _monitorSummaryStatus;
    private (string Id, string Version, SnapshotFrame Frame)? _lastMonitorFrame;
    private bool _monitorDetailsReady;
    private bool _updatingMonitorControls;
    private string _adapterSignature = "";
    private InstalledPlugin? MonitorPlugin => _installed.Plugins.FirstOrDefault(plugin => plugin.Manifest.Category == "monitor" && plugin.Manifest.Id == _preferences.MonitorPluginId)
        ?? _installed.Plugins.FirstOrDefault(plugin => plugin.Manifest.Category == "monitor");

    public sealed class MonitorDetailRow : INotifyPropertyChanged
    {
        public required string Id { get; init; }
        public string Name { get; private set; } = "";
        public string Source { get; private set; } = "";
        public string Value { get; private set; } = "";
        public event PropertyChangedEventHandler? PropertyChanged;
        public void Update(string name, string source, string value)
        {
            if (Name == name && Source == source && Value == value) return;
            Name = name; Source = source; Value = value;
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(string.Empty));
        }
    }

    private void ClearMonitoringViews()
    {
        _monitorPause = null; _monitorNetwork = null; _monitorEmpty = null;
        _monitorSummaryStatus = null; _monitorDetailsReady = false;
        _monitorRows.Clear(); _adapterSignature = "";
    }

    private async Task EnsureBundledMonitorAsync()
    {
        try
        {
            var plugin = MonitorPlugin;
            var package = _bundledPackages.FirstOrDefault(package => plugin is null
                ? package.Manifest.Category == "monitor" && !_removedPlugins.Contains(package.Manifest.Id)
                : package.Manifest.Id == plugin.Manifest.Id && Version.Parse(package.Manifest.Version) > Version.Parse(plugin.Manifest.Version)
                    && !package.Manifest.Permissions.Except(plugin.Manifest.Permissions).Any()
                    && !(package.Manifest.Disclosure?.Dependencies ?? []).Except(plugin.Manifest.Disclosure?.Dependencies ?? []).Any()
                    && !(package.Manifest.Disclosure?.Runtimes ?? []).Except(plugin.Manifest.Disclosure?.Runtimes ?? []).Any());
            if (package is not null)
            {
                await InstallPackageAsync(package);
                Render();
            }
        }
        finally
        {
            _monitorSetupPending = false;
            QueueActivation();
        }
    }

    private async Task EnableMonitoringAsync()
    {
        var plugin = MonitorPlugin;
        if (plugin is null)
        {
            var package = _bundledPackages.FirstOrDefault(package => package.Manifest.Category == "monitor");
            if (package is null) { Navigate("plugins"); return; }
            if (!await ConfirmPluginPackagesAsync([package], "Hhub 随附包")) return;
            plugin = await InstallPackageAsync(package);
        }
        else if (_bundledPackages.FirstOrDefault(package => package.Manifest.Id == plugin.Manifest.Id && Version.Parse(package.Manifest.Version) > Version.Parse(plugin.Manifest.Version)) is { } update)
        {
            if (!await ConfirmPluginPackagesAsync([update], "Hhub 随附包")) return;
            plugin = await InstallPackageAsync(update);
        }
        if (_closing || _installingPlugins.Contains(plugin.Manifest.Id))
            throw new InvalidOperationException("当前无法启动监测，请稍后重试。");
        if (PluginMode(plugin.Manifest.Id) == "disabled") _pluginModes[plugin.Manifest.Id] = "onDemand";
        _disabledPlugins.Remove(plugin.Manifest.Id);
        _pageStartedPlugins.Add(plugin.Manifest.Id);
        SavePluginState();
        ResetSceneActionsForPlugin(plugin.Manifest.Id);
        await _runtime.EnsureStartedAsync(plugin, retry: true);
        Render();
    }

    private bool MonitorCommand(string resource) => MonitorPlugin?.Manifest.Commands.Any(command => command.Resource == resource) == true;

    private async Task MonitorActionAsync(string resource, Dictionary<string, JsonElement>? values = null)
    {
        var plugin = MonitorPlugin ?? throw new InvalidOperationException("尚未安装监测插件");
        var command = plugin.Manifest.Commands.FirstOrDefault(command => command.Resource == resource)
            ?? throw new InvalidOperationException("此监测插件不支持该操作");
        await SendPluginCommandAsync(plugin, command.Id, values);
        UpdateMonitoringData();
    }

    private bool Paused(string id) => _runtime.Snapshot(id).TryGetValue("monitor.paused", out var paused) && paused.ValueKind == JsonValueKind.True;

    private bool UsableData(string id)
    {
        var plugin = _installed.Plugins.FirstOrDefault(plugin => plugin.Manifest.Id == id);
        return plugin is not null && _runtime.IsRunning(id) && (_runtime.IsFresh(plugin.Manifest) || Paused(id));
    }

    private void RememberMonitorFrame()
    {
        if (MonitorPlugin is { Manifest: var manifest } && PluginMode(manifest.Id) != "disabled"
            && UsableData(manifest.Id) && _runtime.Latest(manifest.Id) is { } frame)
            _lastMonitorFrame = (manifest.Id, manifest.Version, frame);
    }

    private SnapshotFrame? CachedMonitorFrame(string id) =>
        _monitorSummaryStatus is not null && _lastMonitorFrame is { } previous && previous.Id == id
        && MonitorPlugin is { Manifest: var manifest } && manifest.Id == id && manifest.Version == previous.Version
        && PluginMode(id) != "disabled" ? previous.Frame : null;

    private SnapshotFrame? SummaryFrame(string? id) => id is null ? null
        : UsableData(id) ? _runtime.Latest(id) : CachedMonitorFrame(id);

    private void UpdateMonitorSummaryStatus()
    {
        if (_monitorSummaryStatus is not { } status) return;
        var cached = MonitorPlugin is { Manifest.Id: var id } && !UsableData(id) ? CachedMonitorFrame(id) : null;
        status.Visibility = cached is null ? Visibility.Collapsed : Visibility.Visible;
        if (cached is not null)
        {
            string label = cached.CapturedAtUtc.HasValue ? "上次采集" : "上次收到";
            status.Text = $"{label} {(cached.CapturedAtUtc ?? cached.ReceivedAtUtc).ToLocalTime():MM-dd HH:mm:ss}";
        }
    }

    private void RenderSystem()
    {
        _title.Text = "系统";
        _actions.Children.Add(IconButton("\uE72B", "返回主页", () => Navigate("home")));
        var sources = _installed.Plugins.Where(plugin => plugin.Manifest.Category == "monitor").ToArray();
        if (sources.Length > 1)
        {
            var source = new ComboBox { Width = 170, ItemsSource = sources, DisplayMemberPath = "Manifest.Name", SelectedItem = MonitorPlugin };
            source.SelectionChanged += (_, _) =>
            {
                if (source.SelectedItem is not InstalledPlugin selected) return;
                _preferences = _preferences with { MonitorPluginId = selected.Manifest.Id };
                SavePreferences(); Render();
            };
            _actions.Children.Add(source);
        }
        var plugin = MonitorPlugin;
        if (plugin is null || !_runtime.IsRunning(plugin.Manifest.Id))
            _actions.Children.Add(AsyncButton("启用监测", EnableMonitoringAsync, true));
        else
        {
            bool paused = Paused(plugin.Manifest.Id);
            _monitorPause = IconButton(paused ? "\uE768" : "\uE769", paused ? "继续" : "暂停", () => _ = RunUiActionAsync(() =>
                MonitorActionAsync(Paused(plugin.Manifest.Id) ? "monitor.collection.resume" : "monitor.collection.pause")));
            _monitorPause.Tag = paused;
            _monitorPause.IsEnabled = MonitorCommand("monitor.collection.pause") && MonitorCommand("monitor.collection.resume");
            _actions.Children.Add(_monitorPause);
            var monitorActions = new List<(string Name, Func<Task> Action)>
            {
                ("刷新", () => MonitorActionAsync("monitor.refresh")),
                ("采样设置", () => { _settingsSection = "监测"; Navigate("settings"); return Task.CompletedTask; })
            };
            if (MonitorCommand("monitor.sensors.authorize"))
                monitorActions.Add(("授权读取硬件传感器", () => MonitorActionAsync("monitor.sensors.authorize")));
            _actions.Children.Add(ActionMenu(monitorActions.ToArray()));
        }
        _content.RowDefinitions.Add(new() { Height = GridLength.Auto });
        _content.RowDefinitions.Add(new() { Height = new(_summaryStacked ? 440 : 216) });
        _content.RowDefinitions.Add(new() { Height = GridLength.Auto });
        _content.RowDefinitions.Add(new() { Height = new(1, GridUnitType.Star) });
        _monitorSummaryStatus = Text("", 13);
        _monitorSummaryStatus.Foreground = Muted;
        UpdateMonitorSummaryStatus();
        _titleControls.Children.Add(_monitorSummaryStatus);
        var summary = SystemSummary(); Grid.SetRow(summary, 1); _content.Children.Add(summary);

        var toolbar = new Grid { ColumnSpacing = 14, Margin = new(0, 8, 0, 8) };
        toolbar.ColumnDefinitions.Add(new() { Width = GridLength.Auto });
        toolbar.ColumnDefinitions.Add(new() { Width = new(1, GridUnitType.Star) });
        var tabs = SectionTabs([("sensors", "传感器"), ("network", "网卡"), ("storage", "磁盘"), ("reports", "采集报告")], _monitorTab,
            tab => { _monitorTab = tab; Render(); });
        toolbar.Children.Add(tabs);
        _monitorNetwork = new ComboBox { Width = _compact ? 150 : 220, PlaceholderText = "选择网卡", Visibility = _monitorTab == "network" ? Visibility.Visible : Visibility.Collapsed, DisplayMemberPath = "Name", HorizontalAlignment = HorizontalAlignment.Right };
        var networkControl = _monitorNetwork;
        _monitorNetwork.IsEnabled = MonitorCommand("monitor.network");
        networkControl.SelectionChanged += async (_, _) =>
        {
            if (_updatingMonitorControls || networkControl.SelectedItem is not NetworkChoice network) return;
            await RunUiActionAsync(() => MonitorActionAsync("monitor.network", new() { ["id"] = JsonSerializer.SerializeToElement(network.Id) }));
        };
        Grid.SetColumn(_monitorNetwork, 1); toolbar.Children.Add(_monitorNetwork);
        Grid.SetRow(toolbar, 2); _content.Children.Add(toolbar);
        var detailHost = new ContentControl
        {
            HorizontalContentAlignment = HorizontalAlignment.Stretch,
            VerticalContentAlignment = VerticalAlignment.Stretch
        };
        Grid.SetRow(detailHost, 3); _content.Children.Add(detailHost);
        AfterPageFirstFrame(detailHost, () => PopulateMonitorDetails(detailHost, plugin));
    }

    private void PopulateMonitorDetails(ContentControl host, InstalledPlugin? plugin)
    {
        _monitorDetailsReady = true;
        UpdateDiskSummary();
        if (_monitorTab == "reports")
        {
            var reports = DiagnosticsHost(plugin?.Manifest.Id, detailed: true);
            reports.VerticalContentAlignment = VerticalAlignment.Stretch;
            var reportCard = Card(reports, padding: new(8));
            host.Content = reportCard;
            UpdateMonitoringData(); return;
        }
        var details = new Grid();
        var list = new ListView
        {
            ItemsSource = _monitorRows, SelectionMode = ListViewSelectionMode.None,
            ItemTemplate = (DataTemplate)XamlReader.Load("""
                <DataTemplate xmlns="http://schemas.microsoft.com/winfx/2006/xaml/presentation">
                  <Grid Padding="12,10" ColumnSpacing="20" MinHeight="44">
                    <Grid.ColumnDefinitions><ColumnDefinition Width="*"/><ColumnDefinition Width="*"/><ColumnDefinition Width="230"/></Grid.ColumnDefinitions>
                    <TextBlock Text="{Binding Name}" FontSize="14" TextTrimming="CharacterEllipsis" VerticalAlignment="Center"/>
                    <TextBlock Grid.Column="1" Text="{Binding Source}" FontSize="14" TextTrimming="CharacterEllipsis" VerticalAlignment="Center"/>
                    <TextBlock Grid.Column="2" Text="{Binding Value}" FontSize="14" TextTrimming="CharacterEllipsis" HorizontalAlignment="Right" VerticalAlignment="Center"/>
                  </Grid>
                </DataTemplate>
                """),
            ItemContainerStyle = new Style(typeof(ListViewItem))
            { Setters = { new Setter(Control.HorizontalContentAlignmentProperty, HorizontalAlignment.Stretch) } }
        };
        ScrollViewer.SetHorizontalScrollBarVisibility(list, ScrollBarVisibility.Disabled);
        details.Children.Add(list);
        _monitorEmpty = Text(plugin is null ? "启用监测后查看数据" : "等待监测数据", BodyTextSize);
        _monitorEmpty.HorizontalAlignment = HorizontalAlignment.Center;
        details.Children.Add(_monitorEmpty);
        var detailCard = Card(details, padding: new(8));
        host.Content = detailCard;
        UpdateMonitoringData();
    }

    private sealed record NetworkChoice(string Id, string Name);

    private void UpdateMonitoringData()
    {
        if (_page == "system" && _openPlugin is null && !_monitorDetailsReady) return;
        UpdatePluginDiagnostics();
        var plugin = MonitorPlugin;
        if (plugin is null) return;
        string id = plugin.Manifest.Id;
        var frame = _runtime.Latest(id);
        bool paused = Paused(id);
        if (_monitorPause is not null && !Equals(_monitorPause.Tag, paused))
        {
            _monitorPause.Tag = paused;
            _monitorPause.Content = Icon(paused ? "\uE768" : "\uE769", 18);
            ToolTipService.SetToolTip(_monitorPause, paused ? "继续" : "暂停");
        }
        if (frame is null || !UsableData(id))
        {
            _monitorRows.Clear();
            if (_monitorEmpty is not null) _monitorEmpty.Visibility = Visibility.Visible;
            return;
        }
        var values = frame.Values;
        _updatingMonitorControls = true;
        try
        {
            if (_monitorNetwork is not null && values.TryGetValue("network.adapters", out var adapters))
            {
                var choices = new List<NetworkChoice> { new("", "自动选择网卡") };
                foreach (var adapter in adapters.EnumerateArray()) choices.Add(new(adapter.GetProperty("id").GetString()!, adapter.GetProperty("name").GetString()!));
                string signature = string.Join('|', choices.Select(choice => choice.Id + choice.Name));
                if (signature != _adapterSignature) { _adapterSignature = signature; _monitorNetwork.ItemsSource = choices; }
                string requested = values.TryGetValue("network.requestedId", out var request) ? request.GetString() ?? "" : "";
                _monitorNetwork.SelectedIndex = choices.FindIndex(choice => choice.Id == requested);
            }
        }
        finally { _updatingMonitorControls = false; }

        if (!_monitorDetailsReady || _monitorTab == "reports") return;
        var rows = new List<(string Id, string Name, string Source, string Value)>();
        if (_monitorTab == "storage")
        {
            foreach (var disk in MonitoringData.Disks(values).Where(disk => disk.IsPhysical))
            {
                string temperature = disk.Temperature is { } t ? $"{t:0.#}°C" : "温度 —";
                string capacity = disk.Total is { } size ? $"{size:0.##} GB" : "容量 —";
                rows.Add(("disk/" + disk.Id, disk.Name, "物理磁盘", temperature + "  ·  " + capacity));
            }
        }
        string key = _monitorTab switch { "network" => "network.adapters", "storage" => "storage.volumes", _ => "hardware.sensors" };
        if (values.TryGetValue(key, out var data))
        {
            foreach (var item in data.EnumerateArray())
            {
                string rowId = item.GetProperty("id").GetString()!;
                string name = item.GetProperty("name").GetString()!;
                if (_monitorTab == "network")
                    rows.Add((rowId, name, "网卡流量", $"↓ {MetricFormatter.Format(item.GetProperty("download"), " Mbps")}  ↑ {MetricFormatter.Format(item.GetProperty("upload"), " Mbps")}"));
                else if (_monitorTab == "storage")
                    rows.Add((rowId, name, "已用 / 总容量", $"{MetricFormatter.Format(item.GetProperty("used"))} / {MetricFormatter.Format(item.GetProperty("total"), " GB")}"));
                else rows.Add((rowId, name, item.GetProperty("hardware").GetString()!, MetricFormatter.Format(item.GetProperty("value"), item.GetProperty("unit").GetString() ?? "")));
            }
        }
        if (!_monitorRows.Select(row => row.Id).SequenceEqual(rows.Select(row => row.Id)))
        {
            _monitorRows.Clear();
            foreach (var row in rows) _monitorRows.Add(new() { Id = row.Id });
        }
        for (int index = 0; index < rows.Count; index++) _monitorRows[index].Update(rows[index].Name, rows[index].Source, rows[index].Value);
        if (_monitorEmpty is not null) _monitorEmpty.Visibility = rows.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
    }

}

using System.Text.Json;
using Hhub.Core;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Windows.UI;

namespace Hhub.App;

public sealed partial class MainWindow
{
    private ContentControl? _homeDeviceHost;
    private StackPanel? _diskListHost;
    private readonly Dictionary<string, (TextBlock Name, TextBlock Capacity, TextBlock Temperature, Border TemperatureFrame)> _diskRows = [];
    private string _diskSignature = "";
    private TextBlock? _cpuClockCaption;

    private void RenderHome()
    {
        double systemHeight = _summaryStacked ? 416 : 200;
        var body = new Grid { RowSpacing = 24, MinHeight = systemHeight + 372 };
        body.RowDefinitions.Add(new() { Height = GridLength.Auto });
        body.RowDefinitions.Add(new() { Height = new(1, GridUnitType.Star) });
        body.Height = Math.Max(_content.ActualHeight, body.MinHeight);
        var viewport = Scroll(body);
        viewport.SizeChanged += (_, args) => body.Height = Math.Max(args.NewSize.Height, body.MinHeight);
        _content.Children.Add(viewport);

        var systemSection = new Grid { RowSpacing = 6 };
        systemSection.RowDefinitions.Add(new() { Height = GridLength.Auto });
        systemSection.RowDefinitions.Add(new() { Height = new(systemHeight) });
        var systemHeading = SectionHeading("系统", "system");
        systemHeading.Margin = new(0); systemSection.Children.Add(systemHeading);
        var metrics = SystemSummary(); Grid.SetRow(metrics, 1); systemSection.Children.Add(metrics);
        body.Children.Add(systemSection);

        var deviceSection = new Grid { RowSpacing = 6 };
        deviceSection.RowDefinitions.Add(new() { Height = GridLength.Auto });
        deviceSection.RowDefinitions.Add(new() { Height = new(1, GridUnitType.Star) });
        var deviceCount = Text($"{_devices.Count} 个外设", 14); deviceCount.Foreground = Muted;
        var devicesHeading = SectionHeading("设备", "devices", deviceCount);
        devicesHeading.Margin = new(0); deviceSection.Children.Add(devicesHeading);
        Grid.SetRow(deviceSection, 1); body.Children.Add(deviceSection);
        if (_devices.Count == 0)
        {
            var empty = Empty("\uE772", "尚无设备信息", AsyncButton("刷新设备", RefreshDevicesAsync, true));
            Grid.SetRow((FrameworkElement)empty, 1); deviceSection.Children.Add(empty); return;
        }

        var deviceArea = new Grid { ColumnSpacing = 16, MinHeight = 264 };
        deviceArea.ColumnDefinitions.Add(new() { Width = new(2, GridUnitType.Star) });
        deviceArea.ColumnDefinitions.Add(new() { Width = new(1, GridUnitType.Star) });
        var selected = _devices.FirstOrDefault(device => device.ContainsInstance(_preferences.PrimaryDeviceId)) ?? _devices[0];
        var host = new ContentControl
        {
            HorizontalContentAlignment = HorizontalAlignment.Stretch, VerticalContentAlignment = VerticalAlignment.Stretch,
            VerticalAlignment = VerticalAlignment.Stretch
        };
        _homeDeviceHost = host; host.Content = PrimaryDevice(selected);
        var list = DeviceList(_devices, home: true, onSelected: device =>
        {
            _preferences = _preferences with { PrimaryDeviceId = device.InstanceId }; SavePreferences();
            host.Content = PrimaryDevice(device); QueueActivation();
        });
        list.VerticalAlignment = VerticalAlignment.Stretch;
        deviceArea.Children.Add(host); Grid.SetColumn(list, 1); deviceArea.Children.Add(list);
        Grid.SetRow(deviceArea, 1); deviceSection.Children.Add(deviceArea);
    }

    private Grid SystemSummary()
    {
        var grid = new Grid { ColumnSpacing = 16, RowSpacing = 16 };
        int columns = _summaryStacked ? 2 : 4;
        for (int i = 0; i < columns; i++) grid.ColumnDefinitions.Add(new() { Width = new(1, GridUnitType.Star) });
        for (int i = 0; i < (_summaryStacked ? 2 : 1); i++) grid.RowDefinitions.Add(new() { Height = new(1, GridUnitType.Star) });
        string? monitorId = MonitorPlugin?.Manifest.Id;
        UIElement[] cards =
        [
            SummaryCard("CPU", "\uE950", monitorId, "cpu.name", [("利用率", "cpu.load", "%"), ("核心 / 线程", "cpu.cores", ""), ("核心频率", "cpu.clock", " GHz"), ("功耗", "cpu.power", " W"), ("温度", "cpu.temperature", "°C")]),
            SummaryCard("GPU", "\uE7F4", monitorId, "gpu.name", [("利用率", "gpu.load", "%"), ("显存已用 / 总量", "gpu.memoryUsed", " GB"), ("核心频率", "gpu.clock", " MHz"), ("功耗", "gpu.power", " W"), ("核心 / 显存温度", "gpu.temperature", "°C")]),
            SummaryCard("内存", "\uE964", monitorId, null, [("利用率", "memory.load", "%"), ("已使用 / 总量", "memory.used", " GB"), ("频率", "memory.clock", " MHz"), ("温度", "memory.temperature", "°C")]),
            NetworkStorageCard(monitorId)
        ];
        for (int i = 0; i < cards.Length; i++)
        {
            Grid.SetRow((FrameworkElement)cards[i], i / columns);
            Grid.SetColumn((FrameworkElement)cards[i], i % columns);
            grid.Children.Add(cards[i]);
        }
        UpdateSummaryCardStates();
        return grid;
    }

    private Border SummaryCard(string name, string glyph, string? pluginId, string? modelKey,
        (string Name, string Key, string Unit)[] metrics)
    {
        var grid = new Grid { RowSpacing = 4 };
        grid.RowDefinitions.Add(new() { Height = new(24) });
        var header = new Grid { ColumnSpacing = 8 };
        header.ColumnDefinitions.Add(new() { Width = GridLength.Auto });
        header.ColumnDefinitions.Add(new() { Width = GridLength.Auto });
        header.ColumnDefinitions.Add(new() { Width = new(1, GridUnitType.Star) });
        header.Children.Add(Icon(glyph, 16));
        var label = Text(name, 14, true); Grid.SetColumn(label, 1); header.Children.Add(label);
        if (modelKey is not null)
        {
            var model = MetricText(pluginId, modelKey, "", 13);
            model.Foreground = Muted;
            model.TextTrimming = TextTrimming.CharacterEllipsis;
            model.HorizontalAlignment = HorizontalAlignment.Stretch;
            model.TextAlignment = TextAlignment.Right;
            Grid.SetColumn(model, 2); header.Children.Add(model);
        }
        grid.Children.Add(header);
        const int start = 1;
        for (int i = 0; i < metrics.Length; i++)
        {
            grid.RowDefinitions.Add(new() { Height = new(24) });
            var metric = metrics[i];
            var row = new Grid { ColumnSpacing = 8 };
            row.ColumnDefinitions.Add(new() { Width = new(1, GridUnitType.Star) });
            row.ColumnDefinitions.Add(new() { Width = GridLength.Auto });
            var metricLabel = Text(metric.Name, 13);
            if (metric.Key == "cpu.clock") _cpuClockCaption = metricLabel;
            metricLabel.Foreground = Muted;
            metricLabel.Opacity = .85;
            row.Children.Add(metricLabel);
            var value = MetricText(pluginId, metric.Key, metric.Unit, 14);
            value.HorizontalAlignment = HorizontalAlignment.Right;
            Grid.SetColumn(value, 1);
            if (metric.Key is "memory.used" or "cpu.cores" or "gpu.temperature" or "gpu.memoryUsed")
            {
                string secondKey = metric.Key switch { "cpu.cores" => "cpu.threads", "gpu.temperature" => "gpu.memoryTemperature", "gpu.memoryUsed" => "gpu.memoryTotal", _ => "memory.total" };
                string secondUnit = metric.Key switch { "cpu.cores" => "", "gpu.temperature" => "°C", _ => " GB" };
                var combined = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 4 };
                combined.Children.Add(value); combined.Children.Add(Text("/", BodyTextSize));
                combined.Children.Add(MetricText(pluginId, secondKey, secondUnit, 14));
                Grid.SetColumn(combined, 1); row.Children.Add(combined);
            }
            else row.Children.Add(value);
            Grid.SetRow(row, i + start); grid.Children.Add(row);
        }
        var card = Card(grid, padding: new(16));
        var required = metrics.Select(metric => (metric.Name, metric.Key)).ToList();
        if (metrics.Any(metric => metric.Key == "memory.used")) required.Add(("总量", "memory.total"));
        if (metrics.Any(metric => metric.Key == "cpu.cores")) required.Add(("线程", "cpu.threads"));
        if (metrics.Any(metric => metric.Key == "gpu.temperature")) required.Add(("显存温度", "gpu.memoryTemperature"));
        if (metrics.Any(metric => metric.Key == "gpu.memoryUsed")) required.Add(("显存总量", "gpu.memoryTotal"));
        _summaryCards.Add((card, pluginId, required.ToArray()));
        return card;
    }

    private Border NetworkStorageCard(string? pluginId)
    {
        var panel = new Grid { RowSpacing = 8 };
        panel.RowDefinitions.Add(new() { Height = new(24) });
        panel.RowDefinitions.Add(new() { Height = new(24) });
        panel.RowDefinitions.Add(new() { Height = new(1, GridUnitType.Star) });
        var network = new Grid { ColumnSpacing = 8 };
        network.ColumnDefinitions.Add(new() { Width = GridLength.Auto });
        network.ColumnDefinitions.Add(new() { Width = new(1, GridUnitType.Star) });
        network.Children.Add(Icon("\uE701", 16));
        var networkTitle = Text("网络", 14, true);
        Grid.SetColumn(networkTitle, 1); network.Children.Add(networkTitle);
        panel.Children.Add(network);
        var traffic = new Grid { ColumnSpacing = 8, HorizontalAlignment = HorizontalAlignment.Right };
        traffic.ColumnDefinitions.Add(new() { Width = new(1, GridUnitType.Star) });
        traffic.ColumnDefinitions.Add(new() { Width = GridLength.Auto });
        var download = MetricText(pluginId, "network.download", " Mbps", 13);
        var upload = MetricText(pluginId, "network.upload", " Mbps", 13);
        var down = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 6 };
        down.Children.Add(Text("↓", 14)); down.Children.Add(download);
        var up = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 6 };
        up.Children.Add(Text("↑", 14)); up.Children.Add(upload);
        traffic.Children.Add(down); Grid.SetColumn(up, 1); traffic.Children.Add(up);
        // Keep live traffic on the header row, without squeezing the section title.
        network.ColumnDefinitions.Add(new() { Width = GridLength.Auto });
        Grid.SetColumn(traffic, 2); network.Children.Add(traffic);
        var heading = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8 };
        heading.Children.Add(Icon("\uEDA2", 16)); heading.Children.Add(Text("磁盘", 14, true));
        Grid.SetRow(heading, 1); panel.Children.Add(heading);
        _diskListHost = new StackPanel { Spacing = 10 };
        var disks = Scroll(_diskListHost);
        Grid.SetRow(disks, 2); panel.Children.Add(disks);
        UpdateDiskSummary();
        return Card(panel, padding: new(16));
    }

    private void UpdateDiskSummary()
    {
        if (_diskListHost is null || _monitorSummaryStatus is not null && !_monitorDetailsReady) return;
        var plugin = MonitorPlugin;
        var disks = SummaryFrame(plugin?.Manifest.Id) is { } frame ? MonitoringData.Disks(frame.Values) : [];
        string signature = string.Join('|', disks.Select(disk => disk.Id));
        if (_diskSignature != signature || _diskListHost.Children.Count == 0)
        {
            _diskSignature = signature; _diskRows.Clear(); _diskListHost.Children.Clear();
            foreach (var disk in disks)
            {
                var row = new Grid { ColumnSpacing = 8, RowSpacing = 2 };
                row.RowDefinitions.Add(new() { Height = new(20) });
                row.RowDefinitions.Add(new() { Height = new(20) });
                row.ColumnDefinitions.Add(new() { Width = new(1, GridUnitType.Star) });
                row.ColumnDefinitions.Add(new() { Width = GridLength.Auto });
                var name = Text(disk.Name, 13); name.Foreground = Muted;
                var capacity = Text("—", 13);
                var temperature = Text("—", 13); temperature.HorizontalAlignment = HorizontalAlignment.Right;
                row.Children.Add(name);
                Grid.SetColumnSpan(name, 2);
                Grid.SetRow(capacity, 1); row.Children.Add(capacity);
                var temperatureFrame = new Border { Child = temperature };
                Grid.SetRow(temperatureFrame, 1); Grid.SetColumn(temperatureFrame, 1); row.Children.Add(temperatureFrame);
                _diskRows[disk.Id] = (name, capacity, temperature, temperatureFrame); _diskListHost.Children.Add(row);
            }
            if (disks.Count == 0)
            {
                var empty = Text(plugin is null ? "尚未启用监测" : "暂无磁盘数据", 15);
                empty.Foreground = Muted; _diskListHost.Children.Add(empty);
            }
        }
        foreach (var disk in disks)
        {
            var row = _diskRows[disk.Id]; row.Name.Text = disk.Name;
            string total = disk.Total is { } totalGb ? $"{totalGb:0.#}" : "—";
            row.Capacity.Text = disk.Used is { } usedGb ? $"{usedGb:0.#} / {total} GB"
                : disk.Total.HasValue ? $"{total} GB" : "—";
            ToolTipService.SetToolTip(row.Capacity, disk.Used.HasValue ? "已用 / 总容量" : "总容量");
            row.Temperature.Text = disk.Temperature is { } temperature ? $"{temperature:0.#}°C" : "—";
            ToolTipService.SetToolTip(row.TemperatureFrame, disk.Temperature is null ? "此磁盘没有提供可用温度" : null);
            ToolTipService.SetToolTip(row.Name, disk.Name);
        }
    }

    private TextBlock MetricText(string? pluginId, string key, string unit, double size = 16)
    {
        string reported = ReadMetric(pluginId, key, unit);
        var text = Text(HardwareDisplayName.Format(key, reported), size);
        if (key is "cpu.name" or "gpu.name") ToolTipService.SetToolTip(text, reported);
        if (pluginId is not null)
        {
            string binding = $"{pluginId}|{key}|{unit}";
            _metricViews[binding] = text;
        }
        return text;
    }

    private string ReadMetric(string? pluginId, string key, string unit)
    {
        return SummaryFrame(pluginId) is { } frame && frame.Values.TryGetValue(key, out var value)
            && value.ValueKind is not (JsonValueKind.Null or JsonValueKind.Undefined)
            ? MetricFormatter.Format(value, unit) : "—";
    }

    private void UpdateMetricViews()
    {
        UpdateMonitorSummaryStatus();
        foreach (var update in _pluginControlUpdates.ToArray()) update();
        foreach (var (binding, view) in _metricViews)
        {
            string[] parts = binding.Split('|');
            string reported = ReadMetric(parts[0], parts[1], parts[2]);
            if (parts[1] is "cpu.name" or "gpu.name") ToolTipService.SetToolTip(view, reported);
            view.Text = HardwareDisplayName.Format(parts[1], reported);
            if (parts[1] == "cpu.clock" && SummaryFrame(parts[0]) is { } frame
                && frame.Values.TryGetValue("cpu.clockSource", out var source))
            {
                ToolTipService.SetToolTip(view, source.GetString());
                if (_cpuClockCaption is not null) _cpuClockCaption.Text = source.GetString()?.StartsWith("Windows", StringComparison.Ordinal) == true
                    ? "系统报告频率" : "核心频率";
            }
        }
        UpdateDiskSummary();
        UpdateSummaryCardStates();
    }

    private FrameworkElement PrimaryDevice(DeviceRecord device)
    {
        var data = Describe(device);
        var grid = new Grid { ColumnSpacing = 18, RowSpacing = 14 };
        grid.ColumnDefinitions.Add(new() { Width = new(3, GridUnitType.Star) });
        grid.ColumnDefinitions.Add(new() { Width = new(2, GridUnitType.Star) });
        grid.RowDefinitions.Add(new() { Height = GridLength.Auto });
        grid.RowDefinitions.Add(new() { Height = new(1, GridUnitType.Star) });
        _primaryDevice = device;
        _primaryName = Text(data.Name, 20, true);
        _primaryName.TextWrapping = TextWrapping.Wrap;
        _primaryName.VerticalAlignment = VerticalAlignment.Center;
        grid.Children.Add(_primaryName);
        var picker = new ComboBox { MaxWidth = 280, MinWidth = 0, HorizontalAlignment = HorizontalAlignment.Stretch,
            PlaceholderText = "选择配置", VerticalAlignment = VerticalAlignment.Center };
        Microsoft.UI.Xaml.Automation.AutomationProperties.SetName(picker, "当前设备配置");
        _primaryConfiguration = picker;
        picker.SelectionChanged += async (_, _) =>
        {
            if (_updatingPrimaryConfiguration || _applyingPrimaryConfiguration || !ReferenceEquals(picker, _primaryConfiguration)
                || picker.SelectedItem is not string name || _primaryDevice?.InstanceId != device.InstanceId
                || !_devices.Any(current => current.ContainsInstance(device.InstanceId))
                || OverviewPlugin(device) is not { } plugin || OverviewCommand(plugin) is not { } command
                || !ReadOverview(plugin, device).Configurations.Contains(name, StringComparer.Ordinal)) return;
            _applyingPrimaryConfiguration = true;
            picker.IsEnabled = false;
            try
            {
                await RunUiActionAsync(() => SendPluginCommandAsync(plugin, command.Id, new Dictionary<string, JsonElement>
                {
                    ["name"] = JsonSerializer.SerializeToElement(name),
                    ["deviceInstanceId"] = JsonSerializer.SerializeToElement(device.InstanceId)
                }));
            }
            finally { _applyingPrimaryConfiguration = false; UpdatePrimaryOverview(); }
        };
        picker.DropDownClosed += (_, _) => UpdatePrimaryOverview();
        var controls = new Grid { ColumnSpacing = 10 };
        controls.ColumnDefinitions.Add(new() { Width = new(1, GridUnitType.Star) });
        controls.ColumnDefinitions.Add(new() { Width = GridLength.Auto });
        controls.Children.Add(picker);
        _primaryBattery = Text(data.Battery is { } battery ? $"{battery:0}%" : "", 14);
        _primaryBattery.VerticalAlignment = VerticalAlignment.Center;
        _primaryBattery.Visibility = data.Battery is null ? Visibility.Collapsed : Visibility.Visible;
        Grid.SetColumn(_primaryBattery, 1);
        controls.Children.Add(_primaryBattery);
        Grid.SetColumn(controls, 1);
        grid.Children.Add(controls);
        var details = new StackPanel { Spacing = 10, VerticalAlignment = VerticalAlignment.Top };
        _primaryNumber = Text("设备编号：" + data.Number, 12);
        _primaryNumber.Foreground = Muted;
        _primaryNumber.TextWrapping = TextWrapping.Wrap;
        ToolTipService.SetToolTip(_primaryNumber, device.InstanceId);
        _primaryConnection = Text(HomeDeviceConnection(device, data), 14);
        _primaryConnection.Foreground = Muted;
        _primaryConnection.TextWrapping = TextWrapping.Wrap;
        details.Children.Add(_primaryConnection);
        _primaryNumber.Margin = new(0, 8, 0, 0);
        details.Children.Add(_primaryNumber);
        _primaryOverview = new StackPanel { Spacing = 6, Margin = new(0, 4, 0, 0) };
        details.Children.Add(_primaryOverview);
        details.Children.Add(Button("打开设备设置", () => OpenDevice(device)));
        Grid.SetRow(details, 1);
        grid.Children.Add(details);
        var artwork = DeviceArtwork(device, 220);
        artwork.Width = double.NaN; artwork.Height = double.NaN;
        artwork.MaxWidth = 220; artwork.MaxHeight = 300;
        artwork.HorizontalAlignment = HorizontalAlignment.Stretch; artwork.VerticalAlignment = VerticalAlignment.Stretch;
        Grid.SetRow(artwork, 1); Grid.SetColumn(artwork, 1); grid.Children.Add(artwork);
        UpdatePrimaryOverview();
        QueueOverviewRefresh(device);
        return Card(grid, padding: new(24));
    }

}

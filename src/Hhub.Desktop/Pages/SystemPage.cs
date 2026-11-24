using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using System.Text.Json;
using Hhub.PluginContracts;
using HardwareDisplayName = Hhub.Core.HardwareDisplayName;

namespace Hhub.Desktop.Pages;

internal sealed partial class SystemPage : Grid
{
    private const double CompactDeviceCardHeight = 52;
    private const double DeviceCardHeight = 67;
    private const double FourColumnMonitorWidth = 760;
    private const double TwoColumnMonitorWidth = 340;
    private const double CardGap = 8;
    private const double HeadingHeight = 42;
    private const double MetricRowHeight = 20;
    private const double MonitorHeadingHeight = 46;
    private readonly Grid _monitorCards;

    private readonly PluginLab? _monitorLab;
    private readonly PeripheralInventory _inventory;
    private readonly Action<ConnectedPeripheral> _openDevice;
    private readonly Action<ConnectedPeripheral>? _deviceSelected;
    private readonly ListBox _devices = new() { Padding = new Thickness(0), BorderThickness = new Thickness(0) };
    private ConnectedPeripheral[] _displayedDevices = [];
    private IReadOnlyDictionary<string, Peripherals.BatteryReading> _deviceBattery = new Dictionary<string, Peripherals.BatteryReading>();
    private readonly TextBlock _monitorStatus = HeadingStatus();
    private readonly TextBlock _deviceStatus = HeadingStatus();
    private CancellationTokenSource? _monitorLoad;
    private int _monitorColumns;


    public SystemPage(PluginLab? monitorLab, PeripheralInventory inventory, Action openMonitor, Action<ConnectedPeripheral> openDevice,
        Action<ConnectedPeripheral>? deviceSelected = null, Action? openDevices = null)
    {
        _monitorLab = monitorLab;
        _inventory = inventory;
        _openDevice = openDevice;
        _deviceSelected = deviceSelected;
        Padding = PageLayout.Inset;
        HorizontalAlignment = HorizontalAlignment.Left;
        VerticalAlignment = VerticalAlignment.Top;
        RowSpacing = 6;
        RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });

        var heading = new Grid { Height = HeadingHeight };
        var system = new Button
        {
            Content = PageLayout.HeadingText("系统"), IsEnabled = monitorLab is not null,
            Style = (Style)Application.Current.Resources["SubtleButtonStyle"],
            HorizontalAlignment = HorizontalAlignment.Left, Padding = new Thickness(0), MinWidth = 0,
            VerticalAlignment = VerticalAlignment.Stretch,
            HorizontalContentAlignment = HorizontalAlignment.Left,
            VerticalContentAlignment = VerticalAlignment.Center
        };
        system.Click += (_, _) => openMonitor();
        AutomationProperties.SetName(system, "系统监测详情");
        heading.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        heading.ColumnDefinitions.Add(new ColumnDefinition());
        heading.Children.Add(system);
        _monitorStatus.Margin = new Thickness(8, 0, 0, 0);
        _monitorStatus.TextTrimming = TextTrimming.CharacterEllipsis;
        Grid.SetColumn(_monitorStatus, 1);
        AutomationProperties.SetLiveSetting(_monitorStatus, Microsoft.UI.Xaml.Automation.Peers.AutomationLiveSetting.Polite);
        SetMonitorStatus(monitorLab is null ? "未连接系统监测插件。" : "");
        heading.Children.Add(_monitorStatus);
        Children.Add(heading);
        _monitorCards = MonitorCards();
        ArrangeMonitorCards(4);
        SetRow(_monitorCards, 1);
        Children.Add(_monitorCards);
        var devicesHeading = new Grid { Height = HeadingHeight };
        var devices = new Button
        {
            Content = PageLayout.HeadingText("设备"),
            Style = (Style)Application.Current.Resources["SubtleButtonStyle"],
            HorizontalAlignment = HorizontalAlignment.Left, Padding = new Thickness(0), MinWidth = 0,
            VerticalAlignment = VerticalAlignment.Stretch,
            HorizontalContentAlignment = HorizontalAlignment.Left,
            VerticalContentAlignment = VerticalAlignment.Center,
            IsEnabled = openDevices is not null
        };
        devices.Click += (_, _) => openDevices?.Invoke();
        AutomationProperties.SetName(devices, "设备管理");
        devicesHeading.Children.Add(devices);
        if (monitorLab is not null) _deviceStatus.Text = "正在发现…";
        else { _deviceStatus.Text = "未连接"; _deviceStatus.Visibility = Visibility.Visible; }
        AutomationProperties.SetLiveSetting(_deviceStatus, Microsoft.UI.Xaml.Automation.Peers.AutomationLiveSetting.Polite);
        devicesHeading.Children.Add(_deviceStatus);
        SetRow(devicesHeading, 2);
        Children.Add(devicesHeading);
        Loaded += LoadDevices;
    }

    private void LoadDevices(object sender, RoutedEventArgs args)
    {
        Loaded -= LoadDevices;
        BuildDeviceList();
        _deviceArea.Children.Add(_devicePreview);
        _deviceArea.Children.Add(_devices);
        SetRow(_deviceArea, 3);
        Children.Add(_deviceArea);
        ArrangeDeviceArea(Width - Padding.Left - Padding.Right);
        _inventory.Subscribe(ShowInventory);
        _ = LoadServicesAsync();
    }

    public void ClosePage()
    {
        Loaded -= LoadDevices;
        _inventory.Unsubscribe(ShowInventory);
        _monitorLoad?.Cancel();
        _devicePreviewLoad?.Cancel();
    }

    private async Task LoadServicesAsync()
    {
        using var load = new CancellationTokenSource();
        _monitorLoad = load;
        try
        {
            await LoadMonitorAsync(load.Token);
        }
        finally { _monitorLoad = null; }
    }

    private void ShowInventory(PeripheralInventory.Snapshot snapshot)
    {
        _deviceBattery = snapshot.Battery;
        ShowDevices(snapshot.Devices, snapshot.Message, snapshot.Available);
    }

    private async Task LoadMonitorAsync(CancellationToken token)
    {
        if (_monitorLab is not { } lab) return;
        try
        {
            await lab.ReadServiceAsync(new(HomeMonitor.Service, HomeMonitor.MajorVersion), (values, _) =>
            {
                SetMonitorStatus(MonitorStatus.Error(MonitorStatus.Authorization(values)));
                ShowMonitorValues(values);
            }, token, PluginPreferences.MonitorInterval(), [HomeMonitor.AuthorizationStatus]);
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested) { }
        catch (Exception error)
        {
            System.Diagnostics.Trace.WriteLine(error);
            if (!token.IsCancellationRequested)
            {
                SetMonitorStatus(MonitorStatus.ReadError(error));
                ShowMonitorValues(new Dictionary<string, JsonElement>());
            }
        }
    }

    private void SetMonitorStatus(string text)
    {
        if (_monitorStatus.Text != text) _monitorStatus.Text = text;
        _monitorStatus.Visibility = text.Length == 0 ? Visibility.Collapsed : Visibility.Visible;
    }

    public void ResizeToViewport(double width, double height)
    {
        if (width <= 0) return;
        double inset = PageLayout.WideInset(width);
        Padding = new Thickness(inset, 0, inset, PageLayout.Inset.Bottom);
        Width = width;
        double contentWidth = Width - Padding.Left - Padding.Right;
        int columns = contentWidth switch
        {
            >= FourColumnMonitorWidth => 4,
            >= TwoColumnMonitorWidth => 2,
            _ => 1
        };
        ArrangeMonitorCards(columns);
        ArrangeDeviceArea(contentWidth);
        MinHeight = _showDevicePreview ? Math.Max(0, height) : 0;
    }

    private readonly Dictionary<string, TextBlock> _monitorValues = new(StringComparer.Ordinal);
    private readonly Dictionary<string, (TextBlock Brand, TextBlock Model)> _hardwareNames = new(StringComparer.Ordinal);
    private readonly (TextBlock Name, TextBlock Value)[] _volumes =
        [(new TextBlock(), new TextBlock()), (new TextBlock(), new TextBlock())];

    private Grid MonitorCards()
    {
        var grid = new Grid { ColumnSpacing = CardGap, RowSpacing = CardGap };
        grid.Children.Add(MonitorGroup("CPU", ("利用率", HomeMonitor.CpuLoad),
            ("核心 / 线程", HomeMonitor.CpuCores), ("核心频率", HomeMonitor.CpuClock),
            ("功耗", HomeMonitor.CpuPower), ("温度", HomeMonitor.CpuTemperature)));
        grid.Children.Add(MonitorGroup("GPU", ("利用率", HomeMonitor.GpuLoad), ("显存用量", HomeMonitor.GpuMemoryUsed),
            ("核心频率", HomeMonitor.GpuClock), ("功耗", HomeMonitor.GpuPower), ("温度", HomeMonitor.GpuTemperature)));
        grid.Children.Add(MonitorGroup("内存", ("利用率", HomeMonitor.MemoryLoad),
            ("用量", HomeMonitor.MemoryUsed), ("频率", HomeMonitor.MemoryClock), ("温度", HomeMonitor.MemoryTemperature)));
        var connectivity = new StackPanel { Spacing = 4 };
        var networkHeader = MonitorHeader("网络");
        var traffic = Title("—", "DashboardDetailStyle");
        traffic.TextAlignment = TextAlignment.Right;
        traffic.TextTrimming = TextTrimming.CharacterEllipsis;
        traffic.VerticalAlignment = VerticalAlignment.Center;
        SetColumn(traffic, 1);
        networkHeader.Children.Add(traffic);
        _monitorValues.Add(HomeMonitor.NetworkDownload, traffic);
        connectivity.Children.Add(networkHeader);
        var diskTitle = Title("磁盘", "BodyStrongTextBlockStyle");
        diskTitle.VerticalAlignment = VerticalAlignment.Center;
        var diskRow = new Grid { Height = MetricRowHeight };
        diskRow.Children.Add(diskTitle);
        connectivity.Children.Add(diskRow);
        foreach (var (name, value) in _volumes)
        {
            name.Text = "—";
            value.Text = "—";
            connectivity.Children.Add(MetricRow(name, value));
        }
        grid.Children.Add(Card(connectivity));
        return grid;
    }

    private Border MonitorGroup(string title, params (string Label, string Id)[] fields)
    {
        var content = new StackPanel { Spacing = 4 };
        content.Children.Add(MonitorHeader(title, title switch
        {
            "CPU" => HomeMonitor.CpuName,
            "GPU" => HomeMonitor.GpuName,
            "内存" => HomeMonitor.MemoryModel,
            _ => null
        }));
        foreach (var (label, id) in fields)
        {
            var value = new TextBlock { Text = "—" };
            _monitorValues.Add(id, value);
            content.Children.Add(MetricRow(label, value));
        }
        return Card(content);
    }

    private Grid MonitorHeader(string label, string? key = null)
    {
        var header = new Grid { Height = MonitorHeadingHeight, ColumnSpacing = 6 };
        header.ColumnDefinitions.Add(new() { Width = GridLength.Auto });
        header.ColumnDefinitions.Add(new());
        header.RowDefinitions.Add(new());
        header.RowDefinitions.Add(new());
        var category = Title(label, "BodyStrongTextBlockStyle");
        category.VerticalAlignment = VerticalAlignment.Center;
        header.Children.Add(category);
        if (key is null) return header;
        var brand = Title("", "DashboardDetailStyle");
        brand.TextTrimming = TextTrimming.CharacterEllipsis;
        brand.VerticalAlignment = VerticalAlignment.Center;
        brand.TextAlignment = TextAlignment.Right;
        SetColumn(brand, 1);
        header.Children.Add(brand);
        var model = Title(key == HomeMonitor.MemoryModel ? "" : "—", "DashboardDetailStyle");
        model.VerticalAlignment = VerticalAlignment.Center;
        model.TextTrimming = TextTrimming.CharacterEllipsis;
        model.TextAlignment = TextAlignment.Right;
        SetRow(model, 1);
        SetColumnSpan(model, 2);
        header.Children.Add(model);
        _hardwareNames.Add(key, (brand, model));
        return header;
    }

    private void ShowProcessorName(string key, string reported)
    {
        var (brand, model) = HardwareDisplayName.SplitReportedName(reported);
        var labels = _hardwareNames[key];
        if (labels.Brand.Text != brand) labels.Brand.Text = brand;
        if (labels.Model.Text != model)
        {
            labels.Model.Text = model;
            ToolTipService.SetToolTip(labels.Model, reported == "—" ? null : reported);
        }
    }

    private void ShowMonitorValues(IReadOnlyDictionary<string, JsonElement> values)
    {
        double? Sample(string id) => values.TryGetValue(id, out var value) && value.ValueKind == JsonValueKind.Array
            && value.GetArrayLength() == 1 && value[0].ValueKind == JsonValueKind.Number
            && value[0].TryGetDouble(out double number) && double.IsFinite(number) ? number : null;
        static string Format(double? value, string unit) => value is { } number ? $"{number:0.#}{unit}" : "—";
        static string FormatDecimal(double? value, string unit) => value is { } number ? $"{number:0.0}{unit}" : "—";
        string Text(string id) => values.TryGetValue(id, out var value) && value.ValueKind == JsonValueKind.String
            && value.GetString() is { Length: > 0 } label ? label : "—";
        void Set(string id, string text)
        {
            if (_monitorValues[id].Text != text) _monitorValues[id].Text = text;
        }
        ShowProcessorName(HomeMonitor.CpuName, Text(HomeMonitor.CpuName));
        Set(HomeMonitor.CpuLoad, FormatDecimal(Sample(HomeMonitor.CpuLoad), "%"));
        Set(HomeMonitor.CpuCores, $"{Format(Sample(HomeMonitor.CpuCores), "")} / {Format(Sample(HomeMonitor.CpuThreads), "")}");
        Set(HomeMonitor.CpuClock, Format(Sample(HomeMonitor.CpuClock) / 1000, " GHz"));
        Set(HomeMonitor.CpuPower, FormatDecimal(Sample(HomeMonitor.CpuPower), " W"));
        Set(HomeMonitor.CpuTemperature, Format(Sample(HomeMonitor.CpuTemperature), " °C"));
        ShowProcessorName(HomeMonitor.GpuName, Text(HomeMonitor.GpuName));
        Set(HomeMonitor.GpuLoad, Format(Sample(HomeMonitor.GpuLoad), "%"));
        Set(HomeMonitor.GpuMemoryUsed, $"{Format(Sample(HomeMonitor.GpuMemoryUsed), "")} / {Format(Sample(HomeMonitor.GpuMemoryTotal), " GiB")}");
        Set(HomeMonitor.GpuClock, Format(Sample(HomeMonitor.GpuClock), " MHz"));
        Set(HomeMonitor.GpuPower, FormatDecimal(Sample(HomeMonitor.GpuPower), " W"));
        Set(HomeMonitor.GpuTemperature, Format(Sample(HomeMonitor.GpuTemperature), " °C"));
        var memoryLabels = _hardwareNames[HomeMonitor.MemoryModel];
        string memoryBrand = Text(HomeMonitor.MemoryManufacturer);
        string memoryModel = Text(HomeMonitor.MemoryModel);
        if (memoryLabels.Brand.Text != memoryBrand) memoryLabels.Brand.Text = memoryBrand;
        if (memoryLabels.Model.Text != memoryModel)
        {
            memoryLabels.Model.Text = memoryModel;
            ToolTipService.SetToolTip(memoryLabels.Model, memoryModel == "—" ? null : memoryModel);
        }
        double? used = Sample(HomeMonitor.MemoryUsed), total = Sample(HomeMonitor.MemoryTotal);
        Set(HomeMonitor.MemoryUsed, used is not null && total is not null ? $"{used:0.0} / {total:0.0} GiB" : "—");
        Set(HomeMonitor.MemoryLoad, FormatDecimal(Sample(HomeMonitor.MemoryLoad) ?? (total > 0 ? used / total * 100 : null), "%"));
        Set(HomeMonitor.MemoryClock, Format(Sample(HomeMonitor.MemoryClock), " MT/s"));
        Set(HomeMonitor.MemoryTemperature, Format(Sample(HomeMonitor.MemoryTemperature), " °C"));
        string traffic = $"↓ {FormatDecimal(Sample(HomeMonitor.NetworkDownload), "")}  ↑ {FormatDecimal(Sample(HomeMonitor.NetworkUpload), "")} Mbps";
        Set(HomeMonitor.NetworkDownload, traffic);

        JsonElement volumes = values.GetValueOrDefault(HomeMonitor.Disks);
        if (volumes.ValueKind != JsonValueKind.Array || volumes.GetArrayLength() == 0)
            volumes = values.GetValueOrDefault(HomeMonitor.Volumes);
        for (int i = 0; i < _volumes.Length; i++)
        {
            var (name, value) = _volumes[i];
            if (volumes.ValueKind == JsonValueKind.Array && i < volumes.GetArrayLength()
                && volumes[i] is { ValueKind: JsonValueKind.Object } volume
                && volume.TryGetProperty("name", out var label) && label.ValueKind == JsonValueKind.String
                && volume.TryGetProperty("total", out var size) && size.ValueKind == JsonValueKind.Number
                && size.TryGetDouble(out double capacity))
            {
                string diskName = label.GetString() ?? "—";
                if (name.Text != diskName)
                {
                    name.Text = diskName;
                    ToolTipService.SetToolTip(name, diskName);
                }
                string temperature = "";
                if (volume.TryGetProperty("temperature", out var degrees))
                {
                    // Accept both the scalar in earlier providers and the contract's optional sample.
                    if (degrees.ValueKind == JsonValueKind.Array && degrees.GetArrayLength() == 1) degrees = degrees[0];
                    if (degrees.ValueKind == JsonValueKind.Number && degrees.TryGetDouble(out double reading) && double.IsFinite(reading))
                        temperature = $"  {Format(reading, " °C")}";
                }
                value.Text = Format(capacity, " GiB") + temperature;
            }
            else { name.Text = "—"; value.Text = "—"; ToolTipService.SetToolTip(name, null); }
        }
    }
    private void ArrangeMonitorCards(int columns)
    {
        if (_monitorColumns == columns) return;
        _monitorColumns = columns;
        _monitorCards.ColumnDefinitions.Clear();
        _monitorCards.RowDefinitions.Clear();
        for (int i = 0; i < columns; i++) _monitorCards.ColumnDefinitions.Add(new ColumnDefinition());
        for (int i = 0; i * columns < _monitorCards.Children.Count; i++)
            _monitorCards.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        for (int i = 0; i < _monitorCards.Children.Count; i++)
        {
            var card = (Border)_monitorCards.Children[i];
            SetColumn(card, i % columns);
            SetRow(card, i / columns);
        }
    }

    private static Grid MetricRow(string label, TextBlock value) => MetricRow(new TextBlock { Text = label }, value);

    private static Grid MetricRow(TextBlock label, TextBlock value)
    {
        var row = new Grid { MinHeight = MetricRowHeight, ColumnSpacing = 4 };
        var detailStyle = ResourceStyle("DashboardDetailStyle");
        label.Style = detailStyle;
        value.Style = detailStyle;
        label.TextTrimming = TextTrimming.CharacterEllipsis;
        label.VerticalAlignment = VerticalAlignment.Center;
        value.VerticalAlignment = VerticalAlignment.Center;
        value.TextAlignment = TextAlignment.Right;
        row.ColumnDefinitions.Add(new ColumnDefinition());
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        row.Children.Add(label);
        SetColumn(value, 1);
        row.Children.Add(value);
        return row;
    }

    private void BuildDeviceList()
    {
        _devices.Background = new SolidColorBrush(Microsoft.UI.Colors.Transparent);
        AutomationProperties.SetName(_devices, "已连接外设");
        ScrollViewer.SetVerticalScrollBarVisibility(_devices, ScrollBarVisibility.Disabled);
        ScrollViewer.SetVerticalScrollMode(_devices, ScrollMode.Disabled);
        _devices.SelectionChanged += (_, _) =>
        {
            ShowDevicePreview();
            if (_devices.SelectedItem is ListBoxItem { Content: Grid { Tag: ConnectedPeripheral selected } })
                _deviceSelected?.Invoke(selected);
        };
    }

    private void ShowDevices(ConnectedPeripheral[] devices, string? message, bool available)
    {
        string discoveryStatus = available ? $"{devices.Length} 台外设" : message ?? (_monitorLab is null ? "未连接" : "读取失败");
        if (_deviceStatus.Text != discoveryStatus) _deviceStatus.Text = discoveryStatus;
        foreach (var item in _devices.Items.Cast<ListBoxItem>())
            if (item.Content is Grid { Tag: ConnectedPeripheral known } row)
                RefreshBattery(item, row, known);
        if (_displayedDevices.SequenceEqual(devices))
        {
            ShowDevicePreview();
            return;
        }
        _displayedDevices = devices;
        string? selected = (_devices.SelectedItem as ListBoxItem)?.Tag as string;
        var cards = _devices.Items.Cast<ListBoxItem>().ToDictionary(item => (string)item.Tag);
        for (int i = _devices.Items.Count - 1; i >= 0; i--)
        {
            var item = (ListBoxItem)_devices.Items[i];
            if (!devices.Any(device => device.Id == (string)item.Tag))
                _devices.Items.RemoveAt(i);
        }
        for (int index = 0; index < devices.Length; index++)
        {
            var device = devices[index];
            if (!cards.TryGetValue(device.Id, out var item))
            {
                item = new ListBoxItem { Tag = device.Id, Style = ResourceStyle("DeviceChoiceStyle"),
                    Height = _showDevicePreview ? DeviceCardHeight : CompactDeviceCardHeight };
                item.DoubleTapped += (sender, args) =>
                {
                    if (sender is not ListBoxItem { Content: Grid { Tag: ConnectedPeripheral candidate } }
                        || !candidate.HasDevicePlugin) return;
                    args.Handled = true;
                    _openDevice(candidate);
                };
                _devices.Items.Insert(index, item);
            }
            else
            {
                int oldIndex = _devices.Items.IndexOf(item);
                if (oldIndex != index)
                {
                    _devices.Items.RemoveAt(oldIndex);
                    _devices.Items.Insert(index, item);
                }
            }
            if (item.Content is not Grid { Tag: ConnectedPeripheral previous } || previous != device)
            {
                var row = new Grid { ColumnSpacing = 8, Tag = device };
                row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
                row.ColumnDefinitions.Add(new ColumnDefinition());
                row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
                row.Children.Add(Peripherals.DeviceThumbnail(device, 22));
                var name = Title(device.Name, "BodyTextBlockStyle");
                name.TextWrapping = TextWrapping.NoWrap;
                name.TextTrimming = TextTrimming.CharacterEllipsis;
                SetColumn(name, 1);
                row.Children.Add(name);
                var status = Title("", "DashboardDetailStyle");
                var charge = DeviceIcon("\u26A1", 13);
                charge.FontFamily = new("Segoe UI Symbol");
                ToolTipService.SetToolTip(charge, "正在充电");
                var indicators = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 4, VerticalAlignment = VerticalAlignment.Center };
                indicators.Children.Add(Peripherals.ConnectionIndicator(device));
                indicators.Children.Add(charge);
                indicators.Children.Add(status);
                SetColumn(indicators, 2);
                row.Children.Add(indicators);
                item.Content = row;
                RefreshBattery(item, row, device);
            }
            item.Margin = new Thickness(0, 0, 0, index == devices.Length - 1 ? 0 : DeviceGap);
        }
        int selectedIndex = Array.FindIndex(devices, device => device.Id == selected);
        _devices.SelectedIndex = selectedIndex >= 0 ? selectedIndex : devices.Length > 0 ? 0 : -1;
        ShowDevicePreview();
    }

    private void RefreshBattery(ListBoxItem item, Grid row, ConnectedPeripheral device)
    {
        bool live = _deviceBattery.TryGetValue(device.Id, out var reading);
        bool charging = live && reading.Charging;
        string text = live ? $"{reading.Percent}%" : Peripherals.ConnectionLabel(device) == "USB"
            ? "" : TrayBattery.Read(device.Id).Text ?? "";
        if (row.Children[^1] is StackPanel indicators)
        {
            indicators.Children[1].Visibility = charging ? Visibility.Visible : Visibility.Collapsed;
            if (indicators.Children[2] is TextBlock status)
            {
                if (status.Text != text) status.Text = text;
                status.Visibility = text.Length > 0 ? Visibility.Visible : Visibility.Collapsed;
            }
        }
        string accessible = $"{device.Name}，{Peripherals.ConnectionLabel(device)}"
            + (text.Length > 0 ? $"，{text}" : "") + (charging ? "，正在充电" : "");
        if (AutomationProperties.GetName(item) != accessible) AutomationProperties.SetName(item, accessible);
    }

    private static TextBlock Title(string text, string style) => new() { Text = text, Style = ResourceStyle(style) };

    private static TextBlock HeadingStatus() => new()
    {
        Style = ResourceStyle("DashboardDetailStyle"),
        HorizontalAlignment = HorizontalAlignment.Right, VerticalAlignment = VerticalAlignment.Center,
        IsHitTestVisible = false
    };

    private static Border Card(UIElement content) => new() { Child = content, Style = ResourceStyle("DashboardCardStyle") };

    private static Style ResourceStyle(string key) => (Style)Application.Current.Resources[key];

    private static FontIcon DeviceIcon(string glyph, double size) => new()
    {
        Glyph = glyph,
        FontSize = size,
        FontFamily = new FontFamily("Segoe Fluent Icons")
    };

    private const double DeviceGap = 2;
}

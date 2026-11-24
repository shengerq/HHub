using Hhub.PluginContracts;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace Hhub.Desktop.Pages;

internal sealed partial class SystemPage
{
    private const double SideBySideDeviceWidth = 650;
    private readonly Grid _deviceArea = new() { ColumnSpacing = CardGap };
    private readonly Border _devicePreview = new() { Visibility = Visibility.Collapsed };
    private readonly Dictionary<string, DeviceDisplayOptions> _deviceDisplay = PluginPreferences.LoadDeviceDisplay();
    private ConnectedPeripheral? _previewedDevice;
    private CancellationTokenSource? _devicePreviewLoad;
    private TextBlock? _previewBattery;
    private StackPanel? _previewParameters;
    private DeviceOverviewRow[] _previewReadings = [];
    private bool _showDevicePreview;
    private bool _showAllParameters;

    private void ArrangeDeviceArea(double width)
    {
        bool showPreview = width >= SideBySideDeviceWidth;
        bool fullParameters = width >= 1150;
        if (_showAllParameters != fullParameters)
        {
            _showAllParameters = fullParameters;
            RenderDeviceParameters();
        }
        if (_showDevicePreview == showPreview) return;
        _showDevicePreview = showPreview;
        RowDefinitions[3].Height = showPreview ? new GridLength(1, GridUnitType.Star) : GridLength.Auto;
        _deviceArea.ColumnDefinitions.Clear();
        if (showPreview)
        {
            _deviceArea.ColumnDefinitions.Add(new() { Width = new GridLength(3, GridUnitType.Star) });
            _deviceArea.ColumnDefinitions.Add(new() { Width = new GridLength(2, GridUnitType.Star) });
        }
        SetColumn(_devices, showPreview ? 1 : 0);
        foreach (var item in _devices.Items.OfType<ListBoxItem>())
            item.Height = _showDevicePreview ? DeviceCardHeight : CompactDeviceCardHeight;
        _devicePreview.Visibility = _showDevicePreview ? Visibility.Visible : Visibility.Collapsed;
        _devicePreview.Style = ResourceStyle("DashboardCardStyle");
        ShowDevicePreview();
    }

    private void ShowDevicePreview()
    {
        var device = _showDevicePreview && _devices.SelectedItem is ListBoxItem { Content: Grid { Tag: ConnectedPeripheral selected } }
            ? selected : null;
        if (_previewedDevice == device && _devicePreview.Child is not null)
        {
            if (device is not null && _previewBattery is not null) UpdatePreviewBattery(device);
            else if (_devicePreview.Child is TextBlock placeholder) placeholder.Text = _deviceStatus.Text;
            return;
        }

        _devicePreviewLoad?.Cancel();
        _previewBattery = null;
        _previewParameters = null;
        _previewReadings = [];
        var previous = _previewedDevice;
        _previewedDevice = device;
        if (device is null)
        {
            _devicePreview.Child = _showDevicePreview ? Title(_deviceStatus.Text, "DashboardDetailStyle") : null;
            return;
        }

        var body = new Grid { ColumnSpacing = 12, RowSpacing = 5 };
        body.ColumnDefinitions.Add(new());
        body.ColumnDefinitions.Add(new() { Width = GridLength.Auto });
        body.RowDefinitions.Add(new() { Height = GridLength.Auto });
        body.RowDefinitions.Add(new());
        var title = PageLayout.HeadingText(device.Name);
        title.TextTrimming = TextTrimming.CharacterEllipsis;
        var header = new Grid { ColumnSpacing = 8 };
        header.ColumnDefinitions.Add(new());
        header.ColumnDefinitions.Add(new() { Width = GridLength.Auto });
        header.Children.Add(title);
        var details = Peripherals.DeviceConnectionDetails(device);
        details.VerticalAlignment = VerticalAlignment.Center;
        _previewBattery = Title("", "DashboardDetailStyle");
        _previewBattery.VerticalAlignment = VerticalAlignment.Center;
        details.Children.Add(_previewBattery);
        UpdatePreviewBattery(device);
        SetColumn(details, 1);
        header.Children.Add(details);
        SetColumnSpan(header, 2);
        body.Children.Add(header);
        _previewParameters = new StackPanel { Spacing = 1 };
        SetRow(_previewParameters, 1);
        body.Children.Add(_previewParameters);
        if (device.Artwork is not null || device.HasDevicePlugin)
        {
            var art = Peripherals.DeviceThumbnail(device, 162);
            if (device.HasDevicePlugin)
            {
                Microsoft.UI.Xaml.Automation.AutomationProperties.SetName(art, $"{device.Name} 概览图");
                ToolTipService.SetToolTip(art, "双击打开设备插件");
                art.DoubleTapped += (_, args) =>
                {
                    args.Handled = true;
                    _openDevice(device);
                };
            }
            SetRow(art, 1);
            SetColumn(art, 1);
            body.Children.Add(art);
        }
        _devicePreview.Child = body;
        if (previous is not null && previous.Id != device.Id) PageLayout.AnimateEntry(body);
        if (_monitorLab is { } lab && device.OverviewProvider is { } provider
            && _deviceDisplay.GetValueOrDefault(provider)?.HomeDetails != false)
            _ = LoadDeviceParametersAsync(lab, device);
    }

    private void UpdatePreviewBattery(ConnectedPeripheral device)
    {
        string text = _deviceBattery.TryGetValue(device.Id, out var battery)
            ? $"{(battery.Charging ? "⚡ " : "")}{battery.Percent}%"
            : Peripherals.ConnectionLabel(device) == "USB" ? "" : TrayBattery.Read(device.Id).Text ?? "";
        if (_previewBattery!.Text != text) _previewBattery.Text = text;
        _previewBattery.Visibility = text.Length == 0 ? Visibility.Collapsed : Visibility.Visible;
    }

    private async Task LoadDeviceParametersAsync(PluginLab lab, ConnectedPeripheral device)
    {
        using var load = new CancellationTokenSource();
        _devicePreviewLoad = load;
        try
        {
            var readings = await Peripherals.ReadOverviewAsync(lab, device, load.Token);
            if (load.IsCancellationRequested) return;
            _previewReadings = readings;
            RenderDeviceParameters();
        }
        catch (OperationCanceledException) when (load.IsCancellationRequested) { }
        catch (Exception error)
        {
            System.Diagnostics.Trace.WriteLine(error);
        }
        finally
        {
            if (ReferenceEquals(_devicePreviewLoad, load)) _devicePreviewLoad = null;
        }
    }

    private void RenderDeviceParameters()
    {
        if (_previewParameters is null) return;
        _previewParameters.Children.Clear();
        foreach (var reading in _previewReadings.Where(reading => _showAllParameters || reading.Compact))
            _previewParameters.Children.Add(MetricRow(reading.Label, new TextBlock { Text = reading.Value }));
    }
}

using System.Text.Json;
using Hhub.Core;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Markup;
using Microsoft.UI.Xaml.Media;
using System.ComponentModel;
using Microsoft.UI.Xaml.Media.Imaging;

namespace Hhub.App;

public sealed partial class MainWindow
{
    private sealed record DeviceDisplay(string Name, string Connection, double? Battery, string? ImagePath = null, string? Number = null, string? Category = null);
    private readonly List<Action> _deviceArtworkUpdates = [];
    private readonly List<DeviceRow> _deviceRows = [];
    private DeviceRecord? _primaryDevice;
    private TextBlock? _primaryName;
    private TextBlock? _primaryNumber;
    private TextBlock? _primaryBattery;
    private TextBlock? _primaryConnection;
    private StackPanel? _primaryOverview;
    private ComboBox? _primaryConfiguration;
    private bool _updatingPrimaryConfiguration;
    private bool _applyingPrimaryConfiguration;
    private (string Plugin, string Device)? _overviewRefreshTarget;
    private DeviceRecord? _pluginTargetDevice;

    private InstalledPlugin[] MatchingPlugins(DeviceRecord device) => _installed.Plugins.Where(plugin => PluginPolicies.MatchesDevice(plugin.Manifest, device)).ToArray();

    private DeviceDisplay Describe(DeviceRecord device)
    {
        foreach (var plugin in MatchingPlugins(device).OrderByDescending(plugin => plugin.Manifest.DeviceOverview is not null))
        {
            var data = DeviceMetadata.Resolve(plugin, device, _runtime.Snapshot(plugin.Manifest.Id), FreshDeviceData(plugin));
            if (data.Name != device.Name || data.ImagePath is not null || data.Battery is not null || data.Category is not null)
                return new(data.Name, DisplayConnection(device, data.Connection), data.Battery, data.ImagePath, data.Number, data.Category);
        }
        return new(device.Name, DisplayConnection(device, device.Connection), null, Number: DeviceMetadata.DeviceNumber(device));
    }

    private bool FreshDeviceData(InstalledPlugin plugin) => PluginMode(plugin.Manifest.Id) != "disabled"
        && _runtime.IsRunning(plugin.Manifest.Id) && _runtime.IsFresh(plugin.Manifest);

    private InstalledPlugin? OverviewPlugin(DeviceRecord device)
    {
        var matches = MatchingPlugins(device).Where(plugin => plugin.Manifest.DeviceOverview is not null
            && PluginMode(plugin.Manifest.Id) != "disabled").Take(2).ToArray();
        return matches.Length == 1 ? matches[0] : null;
    }

    private static PluginCommand? OverviewCommand(InstalledPlugin plugin) =>
        plugin.Manifest.Commands.FirstOrDefault(command => command.Id == plugin.Manifest.DeviceOverview?.ApplyCommand);

    private DeviceOverviewState ReadOverview(InstalledPlugin plugin, DeviceRecord device)
    {
        var overview = DeviceMetadata.Resolve(plugin, device, _runtime.Snapshot(plugin.Manifest.Id), FreshDeviceData(plugin)).Overview
            ?? new([], null, []);
        if (OverviewCommand(plugin)?.Relay is not { } relay) return overview;
        if (device.AudioRenderEndpointIds.Length == 0 || IntegrationTarget(plugin, relay.Integration) is not { } provider
            || !FreshDeviceData(provider)) return overview;
        var settings = plugin.Manifest.DeviceOverview!;
        var values = _runtime.Snapshot(provider.Manifest.Id);
        var options = DeviceOverviewState.Options(values.GetValueOrDefault(settings.IntegrationOptionsKey!));
        var endpoint = values.GetValueOrDefault(settings.IntegrationEndpointKey!);
        var current = values.GetValueOrDefault(settings.IntegrationCurrentKey!);
        return overview with { Configurations = options,
            Current = endpoint.ValueKind == JsonValueKind.String
                && device.AudioRenderEndpointIds.Contains(endpoint.GetString(), StringComparer.OrdinalIgnoreCase)
                && current.ValueKind == JsonValueKind.String ? current.GetString() : null };
    }

    private void QueueOverviewRefresh(DeviceRecord device)
    {
        if (OverviewPlugin(device) is not { } plugin || plugin.Manifest.DeviceOverview?.RefreshCommand is not { } command)
        { _overviewRefreshTarget = null; return; }
        var target = (plugin.Manifest.Id, device.InstanceId);
        if (_overviewRefreshTarget == target) return;
        _overviewRefreshTarget = target;
        DispatcherQueue.TryEnqueue(async () =>
        {
            if (_closing || _hiddenToTray || _overviewRefreshTarget != target || _primaryDevice?.InstanceId != device.InstanceId) return;
            await RunUiActionAsync(() => SendPluginCommandAsync(plugin, command,
                new Dictionary<string, JsonElement> { ["deviceInstanceId"] = JsonSerializer.SerializeToElement(device.InstanceId) }));
        });
    }

    private static string DisplayConnection(DeviceRecord device, string connection) => (device.Has35mmJack, connection) switch
    {
        (true, "USB") => "3.5 mm · USB 声卡",
        (true, "声卡接口" or "系统音频") => "3.5 mm",
        (false, "系统音频") => "接口未确认",
        _ => connection
    };

    private string DeviceConnection(DeviceRecord device, string connection) => (Describe(device).Category ?? device.Category) + " · " + connection
        + (string.IsNullOrWhiteSpace(device.Location) ? "" : " · " + device.Location);

    private static string HomeDeviceConnection(DeviceRecord device, DeviceDisplay display) =>
        (display.Category ?? device.Category) + " · " + display.Connection;

    public sealed class DeviceRow : INotifyPropertyChanged
    {
        public required DeviceRecord Device { get; init; }
        public required string Name { get; set; }
        public required string Connection { get; set; }
        public bool IsHome { get; init; }
        public Brush? SelectionBrush { get; init; }
        public Visibility SelectionVisibility { get; private set; } = Visibility.Collapsed;
        public required string BatteryText { get; set; }
        public Visibility BatteryVisibility => BatteryText.Length == 0 ? Visibility.Collapsed : Visibility.Visible;
        public required string Glyph { get; set; }
        public string? ImagePath { get; set; }
        public ImageSource? Picture => DevicePicture(ImagePath);
        public Visibility PictureVisibility => ImagePath is null ? Visibility.Collapsed : Visibility.Visible;
        public Visibility GlyphVisibility => ImagePath is null ? Visibility.Visible : Visibility.Collapsed;
        public required Brush Background { get; set; }
        public required Brush Border { get; init; }
        public required Brush IconBackground { get; init; }
        public double RowHeight { get; set; } = 72;
        public event PropertyChangedEventHandler? PropertyChanged;
        public void SetSelected(bool selected, Brush background)
        {
            Background = background;
            SelectionVisibility = selected ? Visibility.Visible : Visibility.Collapsed;
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(Background)));
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(SelectionVisibility)));
        }
        public void Update(string name, string connection, string battery, string? imagePath, string glyph)
        {
            if (Name == name && Connection == connection && BatteryText == battery && ImagePath == imagePath && Glyph == glyph) return;
            Name = name; Connection = connection; BatteryText = battery; ImagePath = imagePath; Glyph = glyph;
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(string.Empty));
        }
    }

    private static readonly string DeviceRowTemplate = """
        <DataTemplate xmlns="http://schemas.microsoft.com/winfx/2006/xaml/presentation">
          <Border Height="{Binding RowHeight}" Margin="0,0,0,8" CornerRadius="4"
                  Background="{Binding Background}"
                  BorderBrush="{Binding Border}" BorderThickness="1">
            <Grid>
              <Border Width="3" Height="24" CornerRadius="1.5" HorizontalAlignment="Left"
                      Background="{Binding SelectionBrush}" Visibility="{Binding SelectionVisibility}"/>
            <Grid ColumnSpacing="12" Margin="16,0">
              <Grid.ColumnDefinitions><ColumnDefinition Width="32"/><ColumnDefinition Width="*"/><ColumnDefinition Width="Auto"/></Grid.ColumnDefinitions>
              <Border Width="32" Height="32" CornerRadius="0">
                <Grid>
                  <Image Source="{Binding Picture}" Stretch="Uniform" Visibility="{Binding PictureVisibility}"/>
                  <FontIcon Glyph="{Binding Glyph}" FontFamily="Segoe Fluent Icons" FontSize="20" Visibility="{Binding GlyphVisibility}"/>
                </Grid>
              </Border>
              <StackPanel Grid.Column="1" Spacing="2" VerticalAlignment="Center">
                <TextBlock Text="{Binding Name}" ToolTipService.ToolTip="{Binding Name}" FontSize="14" LineHeight="20" TextTrimming="CharacterEllipsis"/>
                <TextBlock Text="{Binding Connection}" ToolTipService.ToolTip="{Binding Connection}" FontSize="13" LineHeight="18" Opacity="0.72" TextTrimming="CharacterEllipsis"/>
              </StackPanel>
              <TextBlock Grid.Column="2" Text="{Binding BatteryText}" Visibility="{Binding BatteryVisibility}" FontSize="14" VerticalAlignment="Center" HorizontalAlignment="Right"/>
            </Grid>
            </Grid>
          </Border>
        </DataTemplate>
        """;

    private ListView DeviceList(IReadOnlyList<DeviceRecord> devices, bool home = false, Action<DeviceRecord>? onSelected = null)
    {
        var rows = devices.Select(device =>
        {
            var data = Describe(device);
            return new DeviceRow { Device = device, Name = data.Name,
                Connection = home ? HomeDeviceConnection(device, data) : DeviceConnection(device, data.Connection),
                IsHome = home, SelectionBrush = Accent, RowHeight = home ? 80 : 72,
                BatteryText = data.Battery is { } battery ? $"{battery:0}%" : "", Glyph = DeviceGlyph(device), ImagePath = data.ImagePath,
                Background = PanelSurface, Border = Stroke, IconBackground = IconSurface };
        }).ToArray();
        string? selectedId = devices.FirstOrDefault(device => device.ContainsInstance(_preferences.PrimaryDeviceId))?.InstanceId
            ?? devices.FirstOrDefault()?.InstanceId;
        if (onSelected is not null)
            foreach (var row in rows) row.SetSelected(row.Device.ContainsInstance(selectedId), row.Device.ContainsInstance(selectedId) ? Selection : PanelSurface);
        _deviceRows.AddRange(rows);
        var list = new ListView
        {
            ItemsSource = rows, SelectionMode = ListViewSelectionMode.None, IsItemClickEnabled = true,
            ItemTemplate = (DataTemplate)XamlReader.Load(DeviceRowTemplate), Padding = new(0),
            HorizontalAlignment = HorizontalAlignment.Stretch,
            ItemContainerStyle = new Style(typeof(ListViewItem))
            {
                Setters =
                {
                    new Setter(Control.PaddingProperty, new Thickness(0)),
                    new Setter(FrameworkElement.MarginProperty, new Thickness(0)),
                    new Setter(Control.BorderThicknessProperty, new Thickness(0)),
                    new Setter(Control.HorizontalContentAlignmentProperty, HorizontalAlignment.Stretch),
                    new Setter(FrameworkElement.MinHeightProperty, 0d)
                }
            }
        };
        list.Resources["ListViewItemBackgroundPointerOver"] = new SolidColorBrush(Microsoft.UI.Colors.Transparent);
        list.Resources["ListViewItemBackgroundPressed"] = new SolidColorBrush(Microsoft.UI.Colors.Transparent);
        ScrollViewer.SetHorizontalScrollBarVisibility(list, ScrollBarVisibility.Disabled);
        ScrollViewer.SetHorizontalScrollMode(list, ScrollMode.Disabled);
        ScrollViewer.SetVerticalScrollBarVisibility(list, ScrollBarVisibility.Auto);
        string? previewedByClick = null;
        list.ItemClick += (_, args) =>
        {
            var device = ((DeviceRow)args.ClickedItem).Device;
            if (onSelected is null || device.ContainsInstance(previewedByClick)) OpenDevice(device);
            else
            {
                previewedByClick = device.InstanceId;
                foreach (var row in rows) row.SetSelected(row.Device.InstanceId == device.InstanceId,
                    row.Device.InstanceId == device.InstanceId ? Selection : PanelSurface);
                onSelected(device);
            }
        };
        return list;
    }

    private async Task RefreshDevicesAsync()
    {
        if (_deviceRefreshBusy) { _deviceRefreshAgain = true; return; }
        _deviceRefreshBusy = true;
        try
        {
            do
            {
                _deviceRefreshAgain = false;
                var identified = await PlatformServices.EnumerateDevicesAsync();
                _devices = identified.Where(device => GamingPeripheralPolicy.IsVisible(device, Describe(device).Category)).ToArray();
            } while (_deviceRefreshAgain && !_closing);
            _trayBatteries.Prune(_devices); UpdateTrayBatteries();
            foreach (string id in _requestedDeviceScenes.Keys.Where(id => !_devices.Any(device => device.ContainsInstance(id))).ToArray()) _requestedDeviceScenes.Remove(id);
            if (_openPlugin is not null)
            {
                UpdateDeviceViews();
                QueueActivation();
                return;
            }
            if (_page == "scenes" && DateTimeOffset.UtcNow < _sceneTapDeadline) _sceneViewDirty = true;
            else if (_page is "home" or "devices" or "scenes" or "sceneDetail") Render();
        }
        catch (Exception ex) when (ex is System.Runtime.InteropServices.COMException or UnauthorizedAccessException)
        { ShowError(ex.Message); }
        finally { _deviceRefreshBusy = false; }
    }

    private void RenderDevices()
    {
        _title.Text = "设备";
        _actions.Children.Add(Button("返回主页", () => Navigate("home")));
        var filterTypes = new[] { "全部", "鼠标", "键盘", "耳机", "手柄", "键鼠复合设备", "USB", "蓝牙" }
            .Concat(_devices.Select(device => Describe(device).Category ?? device.Category)).Distinct().ToArray();
        var filter = new ComboBox { Width = 112, ItemsSource = filterTypes, SelectedItem = _deviceFilter };
        filter.SelectionChanged += (_, _) => { _deviceFilter = (string)filter.SelectedItem; Render(); };
        _actions.Children.Add(filter);
        _actions.Children.Add(ActionMenu(("刷新设备", RefreshDevicesAsync), ("复制设备识别诊断", () =>
        {
            var content = new Windows.ApplicationModel.DataTransfer.DataPackage();
            content.SetText(JsonSerializer.Serialize(new { capturedAt = DateTimeOffset.UtcNow,
                nodes = PlatformServices.LastDeviceNodes, devices = _devices, issues = PlatformServices.DeviceEnumerationIssues }, JsonFormat.Options));
            Windows.ApplicationModel.DataTransfer.Clipboard.SetContent(content);
            return Task.CompletedTask;
        })));
        var devices = _devices.Where(device => _deviceFilter == "全部" || device.Category == _deviceFilter || Describe(device).Category == _deviceFilter || device.Categories.Contains(_deviceFilter) || device.Connection == _deviceFilter).ToArray();
        _content.Children.Add(devices.Length == 0 ? Empty("\uE772", "没有可显示的设备") : DeviceList(devices));
    }

    private void OpenDevice(DeviceRecord device)
    {
        _openDevice = device;
        OpenDevicePlugin(PluginPolicies.DevicePage(_installed.Plugins, device));
    }

    private void OpenDevicePlugin(InstalledPlugin? plugin)
    {
        _pluginSettingsOpen = false;
        _openPlugin = plugin;
        if (plugin is not null && StartPluginPageBackend(plugin) is { } start)
            _ = RunUiActionAsync(() => start);
        RefreshNavigation();
        Render();
    }

    private void RenderDeviceDetail(DeviceRecord device)
    {
        var display = Describe(device);
        bool connected = _devices.Any(item => item.ContainsInstance(device.InstanceId));
        _title.Text = display.Name;
        _actions.Children.Add(Button("返回", () => { _openDevice = null; Render(); }));
        _actions.Children.Add(Button("在主页选中", () =>
        {
            _preferences = _preferences with { PrimaryDeviceId = device.InstanceId };
            SavePreferences(); Navigate("home");
        }));
        var panel = new StackPanel { Spacing = 20 };
        panel.Children.Add(DeviceArtwork(device, 180));
        if (!connected) panel.Children.Add(Text("设备已断开", 20, true));
        panel.Children.Add(Text($"{display.Category ?? device.Category}  ·  {display.Connection}", BodyTextSize));
        if (device.ClassificationNote is { } reason)
        {
            var explanation = Text(reason, BodyTextSize); explanation.TextWrapping = TextWrapping.Wrap;
            panel.Children.Add(new Expander { Header = "Windows 识别依据", Content = explanation,
                HorizontalAlignment = HorizontalAlignment.Stretch, HorizontalContentAlignment = HorizontalAlignment.Stretch });
        }
        if (device.ReportedName is { } reported && reported != display.Name)
            panel.Children.Add(new TextBox { Header = "Windows 报告名称", Text = reported, IsReadOnly = true });
        if (display.Battery is { } battery) panel.Children.Add(Text($"电量：{battery:0}%", BodyTextSize));
        panel.Children.Add(Text(device.Vid is null ? "VID / PID  —" : $"VID  {device.Vid}     PID  {device.Pid}", 17));
        var identity = new TextBox { Header = "设备实例", Text = device.InstanceId, IsReadOnly = true };
        panel.Children.Add(identity);
        panel.Children.Add(new TextBox { Header = "接口", Text = device.InterfacePath, IsReadOnly = true });
        if (device.Location is not null) panel.Children.Add(Text("端口：" + device.Location, 17));
        if (device.LocationPath is not null) panel.Children.Add(new TextBox { Header = "连接位置", Text = device.LocationPath, IsReadOnly = true });
        if (device.InterfacePaths.Length > 1) panel.Children.Add(new TextBox { Header = "全部接口", Text = string.Join("\n", device.InterfacePaths), IsReadOnly = true, AcceptsReturn = true, TextWrapping = TextWrapping.Wrap });
        if (device.MemberInstanceIds.Length > 1) panel.Children.Add(new TextBox { Header = "关联设备节点", Text = string.Join("\n", device.MemberInstanceIds), IsReadOnly = true, AcceptsReturn = true, TextWrapping = TextWrapping.Wrap });
        var plugins = MatchingPlugins(device);
        foreach (var plugin in plugins)
        {
            var open = Button(plugin.Manifest.Name, () => OpenDevicePlugin(plugin), true);
            open.IsEnabled = connected; panel.Children.Add(open);
        }
        if (plugins.Length == 0) panel.Children.Add(Button("查找插件", () => FindPluginsForDevice(device), true));
        _content.Children.Add(Card(panel, !connected));
    }

    private void FindPluginsForDevice(DeviceRecord device)
    {
        _pluginTargetDevice = device; _pluginTab = "market"; _pluginSearch = "";
        Navigate("plugins");
    }

    private void ClearDeviceViews()
    {
        _deviceRows.Clear(); _deviceArtworkUpdates.Clear(); _primaryDevice = null;
        _primaryName = null; _primaryNumber = null; _primaryBattery = null; _primaryConnection = null;
        _primaryOverview = null; _primaryConfiguration = null;
    }

    private void UpdateDeviceViews()
    {
        foreach (var row in _deviceRows)
        {
            var data = Describe(row.Device);
            row.Update(data.Name, row.IsHome ? HomeDeviceConnection(row.Device, data) : DeviceConnection(row.Device, data.Connection),
                data.Battery is { } battery ? $"{battery:0}%" : "", data.ImagePath, DeviceGlyph(row.Device));
        }
        foreach (var update in _deviceArtworkUpdates) update();
        if (_primaryDevice is null) { _overviewRefreshTarget = null; return; }
        var display = Describe(_primaryDevice);
        if (_primaryName is not null) _primaryName.Text = display.Name;
        if (_primaryNumber is not null) _primaryNumber.Text = "设备编号：" + display.Number;
        if (_primaryBattery is not null)
        {
            _primaryBattery.Text = display.Battery is { } level ? $"{level:0}%" : "";
            _primaryBattery.Visibility = display.Battery is null ? Visibility.Collapsed : Visibility.Visible;
        }
        if (_primaryConnection is not null) _primaryConnection.Text = display.Connection;
        UpdatePrimaryOverview();
    }

    private void UpdatePrimaryOverview()
    {
        if (_primaryDevice is not { } device || _primaryOverview is not { } panel || _primaryConfiguration is not { } picker) return;
        var plugin = OverviewPlugin(device);
        var state = plugin is null ? new DeviceOverviewState([], null, []) : ReadOverview(plugin, device);
        var rows = state.Rows;
        string signature = string.Join("\n", rows.Select(row => $"{row.Label}\t{row.Value}"));
        if (!string.Equals(panel.Tag as string, signature, StringComparison.Ordinal))
        {
            panel.Tag = signature;
            panel.Children.Clear();
            if (rows.Length > 0) panel.Children.Add(Text("参数", 13, true));
            var columns = new Grid { ColumnSpacing = 10 };
            int columnCount = rows.Length > 8 ? 2 : 1;
            int rowsPerColumn = (rows.Length + columnCount - 1) / columnCount;
            for (int column = 0; column < columnCount; column++)
            {
                columns.ColumnDefinitions.Add(new() { Width = new(1, GridUnitType.Star) });
                var values = new StackPanel { Spacing = 4 };
                foreach (var row in rows.Skip(column * rowsPerColumn).Take(rowsPerColumn))
                {
                    var text = new TextBlock { FontSize = 12, TextWrapping = TextWrapping.Wrap };
                    text.Inlines.Add(new Microsoft.UI.Xaml.Documents.Run { Text = row.Label + "  ", Foreground = Muted });
                    text.Inlines.Add(new Microsoft.UI.Xaml.Documents.Run { Text = row.Value, Foreground = Accent });
                    values.Children.Add(text);
                }
                Grid.SetColumn(values, column);
                columns.Children.Add(values);
            }
            panel.Children.Add(columns);
        }
        panel.Visibility = rows.Length > 0 ? Visibility.Visible : Visibility.Collapsed;
        picker.Visibility = plugin is null ? Visibility.Collapsed : Visibility.Visible;
        picker.IsEnabled = !_applyingPrimaryConfiguration && state.Configurations.Length > 0;
        if (picker.IsDropDownOpen || _applyingPrimaryConfiguration) return;
        _updatingPrimaryConfiguration = true;
        try
        {
            if (picker.ItemsSource is not string[] previous || !previous.SequenceEqual(state.Configurations))
                picker.ItemsSource = state.Configurations;
            picker.SelectedItem = state.Configurations.Contains(state.Current, StringComparer.Ordinal) ? state.Current : null;
            picker.PlaceholderText = state.Current ?? (state.Configurations.Length > 0 ? "选择配置" : "配置暂不可用");
            ToolTipService.SetToolTip(picker, state.Current ?? picker.PlaceholderText);
        }
        finally { _updatingPrimaryConfiguration = false; }
    }
    private FrameworkElement DeviceArtwork(DeviceRecord device, double size = 48)
    {
        var grid = new Grid { Width = size, Height = size, HorizontalAlignment = HorizontalAlignment.Left };
        var photo = new Image { Stretch = Stretch.Uniform };
        var glyph = Icon(DeviceGlyph(device), Math.Min(32, size * .65));
        grid.Children.Add(glyph); grid.Children.Add(photo);
        string? shown = null;
        void Update()
        {
            glyph.Glyph = DeviceGlyph(device);
            string? path = Describe(device).ImagePath;
            if (shown != path) { shown = path; photo.Source = DevicePicture(path); }
            photo.Visibility = path is null ? Visibility.Collapsed : Visibility.Visible;
            glyph.Visibility = path is null ? Visibility.Visible : Visibility.Collapsed;
        }
        _deviceArtworkUpdates.Add(Update); Update(); return grid;
    }
    private static ImageSource? DevicePicture(string? path) => path is null ? null : Path.GetExtension(path).Equals(".svg", StringComparison.OrdinalIgnoreCase)
        ? new SvgImageSource(new Uri(path)) : new BitmapImage(new Uri(path));
}

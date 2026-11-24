using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Hhub.PluginContracts;
using Windows.Storage.Pickers;

namespace Hhub.Desktop.Pages;

internal sealed partial class ScenesPage : Grid
{
    private const double TileGap = 6;
    private const double SectionGap = 6;
    private const double MaximumTileWidth = 109;
    private const double MinimumTileWidth = 48;
    private const int MaximumTileColumns = 4;
    private readonly StackPanel _body = new() { Spacing = SectionGap };
    private readonly SceneCatalog _catalog;
    private readonly Action<IReadOnlyList<ScenePreview>, string?> _updateSceneRules;
    private readonly ForegroundScenes _foreground;
    private readonly SceneDeviceConfigurations _configurations;
    private readonly nint _windowHandle;
    private int _editorRevision;
    private readonly Action _resetScroll;
    private readonly PeripheralInventory _inventory;
    private readonly Action<ConnectedPeripheral> _openDevice;
    private IReadOnlyDictionary<string, Peripherals.BatteryReading> _battery = new Dictionary<string, Peripherals.BatteryReading>();
    private readonly Dictionary<string, TextBlock> _batteryLabels = new(StringComparer.Ordinal);
    private readonly Dictionary<string, Func<Task>> _readDeviceOptions = new(StringComparer.Ordinal);
    private ConnectedPeripheral[] _devices = [];
    private Action? _updateDeviceRows;
    private Action<string?>? _showForeground;
    private CancellationTokenSource? _editor;
    private string? _deviceMessage = "正在发现外设…";
    private readonly TextBlock _notice = new() { TextWrapping = TextWrapping.Wrap, Visibility = Visibility.Collapsed };
    private readonly TextBlock _activationNotice = new() { TextWrapping = TextWrapping.Wrap, Visibility = Visibility.Collapsed };
    private List<ScenePreview> _scenes = [];
    private Action? _arrangeTiles;
    private CancellationTokenSource? _load;
    private bool _closed;

    public ScenesPage(Action resetScroll, PeripheralInventory inventory, SceneCatalog catalog,
        Action<IReadOnlyList<ScenePreview>, string?> updateSceneRules, ForegroundScenes foreground,
        SceneDeviceConfigurations configurations, Action<ConnectedPeripheral> openDevice, nint windowHandle)
    {
        _resetScroll = resetScroll;
        _inventory = inventory;
        _catalog = catalog;
        _updateSceneRules = updateSceneRules;
        _foreground = foreground;
        _configurations = configurations;
        _openDevice = openDevice;
        _windowHandle = windowHandle;
        Padding = PageLayout.Inset;
        HorizontalAlignment = HorizontalAlignment.Center;
        VerticalAlignment = VerticalAlignment.Top;
        Children.Add(_body);
        AutomationProperties.SetLiveSetting(_notice, Microsoft.UI.Xaml.Automation.Peers.AutomationLiveSetting.Polite);
        AutomationProperties.SetLiveSetting(_activationNotice, Microsoft.UI.Xaml.Automation.Peers.AutomationLiveSetting.Polite);
        _body.Children.Add(PageLayout.Heading("场景"));
        _body.Children.Add(_notice);
        Loaded += LoadScenes;
    }

    private async void LoadScenes(object sender, RoutedEventArgs args)
    {
        Loaded -= LoadScenes;
        if (_closed) return;
        _inventory.Subscribe(ShowInventory);
        using var load = new CancellationTokenSource();
        _load = load;
        try
        {
            _scenes = await _catalog.LoadAsync(load.Token);
            load.Token.ThrowIfCancellationRequested();
            _updateSceneRules(_scenes, null);
            ShowOverview();
        }
        catch (OperationCanceledException) when (load.IsCancellationRequested) { }
        catch (Exception error) when (IsStorageError(error) || error is System.Text.Json.JsonException or InvalidDataException)
        {
            if (!load.IsCancellationRequested) ShowNotice("无法读取已保存的场景。");
        }
        finally { _load = null; }
    }

    public void ResizeToViewport(double width)
    {
        if (width > 0)
        {
            double inset = PageLayout.WideInset(width);
            Padding = new Thickness(inset, 0, inset, PageLayout.Inset.Bottom);
            Width = width;
        }
        _arrangeTiles?.Invoke();
    }

    public void ClosePage()
    {
        _closed = true;
        Loaded -= LoadScenes;
        _inventory.Unsubscribe(ShowInventory);
        CloseEditor();
        _load?.Cancel();
        _arrangeTiles = null;
    }

    private void ShowInventory(PeripheralInventory.Snapshot snapshot)
    {
        _battery = snapshot.Battery;
        ShowDevices(snapshot.Devices, snapshot.Message, snapshot.Available);
        foreach (var (id, label) in _batteryLabels) ShowBattery(id, label);
    }

    private void ShowBattery(string id, TextBlock label)
    {
        bool hasReading = _battery.TryGetValue(id, out var battery);
        string text = hasReading ? $"{(battery.Charging ? "⚡ " : "")}{battery.Percent}%" : TrayBattery.Read(id).Text ?? "";
        if (label.Text == text) return;
        label.Text = text;
        label.Visibility = text.Length > 0 ? Visibility.Visible : Visibility.Collapsed;
        AutomationProperties.SetName(label, text.Length == 0 ? "" : $"电量 {text.TrimStart('⚡', ' ')}"
            + (hasReading && battery.Charging ? "，正在充电" : ""));
    }

    private void ShowOverview()
    {
        _editorRevision++;
        CloseEditor();
        _editor = new CancellationTokenSource();
        var token = _editor.Token;
        _body.Children.Clear();
        _body.Children.Add(PageLayout.Heading("场景"));
        _body.Children.Add(_activationNotice);
        var tiles = new Grid { ColumnSpacing = TileGap, RowSpacing = TileGap,
            HorizontalAlignment = HorizontalAlignment.Left };
        for (int index = 0; index < _scenes.Count; index++)
        {
            int sceneIndex = index;
            var tile = Tile(_scenes[index].Name);
            tile.Click += (_, _) => ShowEditor(sceneIndex);
            tiles.Children.Add(tile);
        }

        var add = Tile("+");
        AutomationProperties.SetName(add, "新建场景");
        add.Click += (_, _) => ShowEditor(null);
        tiles.Children.Add(add);
        int lastColumns = 0;
        double lastTileSize = 0;
        void ArrangeTiles()
        {
            double width = Width - Padding.Left - Padding.Right;
            if (!double.IsFinite(width) || width <= 0)
                width = MaximumTileColumns * MinimumTileWidth + (MaximumTileColumns - 1) * TileGap;
            int columns = Math.Clamp((int)((width + TileGap) / (MinimumTileWidth + TileGap)), 1, MaximumTileColumns);
            double tileSize = Math.Min((width - (columns - 1) * TileGap) / columns, MaximumTileWidth);
            if (columns == lastColumns && tileSize == lastTileSize) return;
            lastColumns = columns;
            lastTileSize = tileSize;
            tiles.ColumnDefinitions.Clear();
            tiles.RowDefinitions.Clear();
            for (int index = 0; index < columns; index++)
                tiles.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(tileSize) });
            for (int index = 0; index < tiles.Children.Count; index++)
            {
                if (index % columns == 0) tiles.RowDefinitions.Add(new RowDefinition { Height = new GridLength(tileSize) });
                var tile = (Button)tiles.Children[index];
                Grid.SetColumn(tile, index % columns);
                Grid.SetRow(tile, index / columns);
            }
        }
        _arrangeTiles = ArrangeTiles;
        ArrangeTiles();
        _body.Children.Add(tiles);
        _body.Children.Add(PageLayout.Heading("设备"));

        AddDeviceRows(device => ConfigurationDeviceRow(device, null, null, token));
        _body.Children.Add(_notice);
        _notice.Visibility = Visibility.Collapsed;
        _resetScroll();
    }

    private void ShowEditor(int? index)
    {
        CloseEditor();
        int revision = ++_editorRevision;
        _editor = new CancellationTokenSource();
        CancellationToken token = _editor.Token;
        _arrangeTiles = null;
        var original = index is int sceneIndex ? _scenes[sceneIndex] : new ScenePreview("", [], new());
        _body.Children.Clear();
        _notice.Visibility = Visibility.Collapsed;
        var name = new TextBox { Text = original.Name, MaxLength = 40, MaxWidth = 280,
            HorizontalAlignment = HorizontalAlignment.Stretch };
        var save = PageLayout.AccentButton("保存");
        AutomationProperties.SetName(name, "场景名称");
        var breadcrumb = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 10,
            Height = PageLayout.HeadingRowHeight };
        var back = new Button { Content = PageLayout.BreadcrumbText("场景"),
            Style = (Style)Application.Current.Resources["SubtleButtonStyle"], Padding = new Thickness(0), MinWidth = 0 };
        back.Click += (_, _) => ShowOverview();
        var title = PageLayout.HeadingText(index.HasValue ? original.Name : "新建场景");
        title.TextTrimming = TextTrimming.CharacterEllipsis;
        breadcrumb.Children.Add(back);
        breadcrumb.Children.Add(new TextBlock { Text = "›", VerticalAlignment = VerticalAlignment.Center });
        breadcrumb.Children.Add(title);
        name.TextChanged += (_, _) => title.Text = string.IsNullOrWhiteSpace(name.Text) ? "新建场景" : name.Text.Trim();
        _body.Children.Add(breadcrumb);
        var rename = new Grid { ColumnSpacing = 12 };
        rename.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        rename.ColumnDefinitions.Add(new ColumnDefinition());
        rename.Children.Add(new TextBlock { Text = "改名", VerticalAlignment = VerticalAlignment.Center });
        Grid.SetColumn(name, 1);
        rename.Children.Add(name);
        _body.Children.Add(PageLayout.CardRow(rename, save));
        _body.Children.Add(_activationNotice);
        var bindings = new Dictionary<string, SceneDeviceBinding>(original.DeviceConfigurations ?? new(), StringComparer.Ordinal);
        var summary = new StackPanel { Spacing = 4 };
        var summaryRow = PageLayout.CardRow(summary, null);
        void ShowSceneConfigurations()
        {
            summaryRow.Visibility = bindings.Count == 0 ? Visibility.Collapsed : Visibility.Visible;
            summary.Children.Clear();
            if (bindings.Count == 0) return;
            summary.Children.Add(new TextBlock { Text = "已加入场景的设备配置" });
            foreach (var (target, binding) in bindings)
            {
                bool available = _devices.Any(device => device.Id == target && device.DeviceProvider == binding.PluginId);
                var label = new TextBlock
                {
                    Text = $"{binding.DeviceName} · {SceneDeviceConfigurations.MenuLabel(binding.Configuration)}"
                        + (available ? "" : "（当前不可用）"),
                    TextWrapping = TextWrapping.Wrap
                };
                if (available)
                {
                    summary.Children.Add(label);
                    continue;
                }
                var remove = new Button { Content = "移除配置" };
                remove.Click += (_, _) =>
                {
                    bindings.Remove(target);
                    ShowSceneConfigurations();
                };
                summary.Children.Add(ControlsRow(label, remove));
            }
        }
        _body.Children.Add(summaryRow);
        _body.Children.Add(_notice);

        _body.Children.Add(PageLayout.Heading("前台应用"));
        var apps = original.Applications.Where(application => !SceneCatalog.IsSampleApplication(application))
            .Distinct(StringComparer.OrdinalIgnoreCase).ToList();
        var applicationPanel = new StackPanel { Spacing = PageLayout.SettingsRowGap };
        void AddApplicationRow(string executable)
        {
            var row = new Grid { ColumnSpacing = 8, MinHeight = 36 };
            row.ColumnDefinitions.Add(new ColumnDefinition());
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            var label = new TextBlock { Text = Path.GetFileName(executable), VerticalAlignment = VerticalAlignment.Center,
                TextTrimming = TextTrimming.CharacterEllipsis };
            ToolTipService.SetToolTip(label, executable);
            var remove = new Button { Content = "移除" };
            AutomationProperties.SetName(remove, $"移除 {label.Text}");
            remove.Click += (_, _) =>
            {
                apps.Remove(executable);
                applicationPanel.Children.Remove(row);
            };
            Grid.SetColumn(remove, 1);
            row.Children.Add(label);
            row.Children.Add(remove);
            applicationPanel.Children.Add(row);
        }
        void AddApplication(string executable)
        {
            if (apps.Any(app => string.Equals(Path.GetFileName(app), Path.GetFileName(executable), StringComparison.OrdinalIgnoreCase))) return;
            apps.Add(executable);
            AddApplicationRow(executable);
            _notice.Visibility = Visibility.Collapsed;
        }
        foreach (string app in apps) AddApplicationRow(app);
        var browse = new Button { Content = "浏览…" };
        browse.Click += async (_, _) =>
        {
            browse.IsEnabled = false;
            try
            {
                var picker = new FileOpenPicker { SuggestedStartLocation = PickerLocationId.ComputerFolder };
                picker.FileTypeFilter.Add(".exe");
                WinRT.Interop.InitializeWithWindow.Initialize(picker, _windowHandle);
                var selected = await picker.PickSingleFileAsync();
                if (_closed || revision != _editorRevision || selected is null || string.IsNullOrWhiteSpace(selected.Path)) return;
                AddApplication(selected.Path);
            }
            catch (Exception error) when (error is IOException or UnauthorizedAccessException
                or System.Runtime.InteropServices.COMException)
            {
                if (!_closed && revision == _editorRevision) ShowNotice("无法打开应用选择器。");
            }
            finally
            {
                if (!_closed && revision == _editorRevision) browse.IsEnabled = true;
            }
        };
        AddRunningApplications(AddApplication, browse, token);
        _body.Children.Add(applicationPanel);

        _body.Children.Add(PageLayout.Heading("设备"));
        AddDeviceRows(device => ConfigurationDeviceRow(device, bindings, ShowSceneConfigurations, token), ShowSceneConfigurations);

        void Save()
        {
            string sceneName = name.Text.Trim();
            if (sceneName.Length == 0)
            {
                ShowNotice("请输入场景名称。");
                name.Focus(FocusState.Programmatic);
                return;
            }
            if (_scenes.Where((_, position) => position != index).Any(scene => scene.Name == sceneName))
            {
                ShowNotice("场景名称已存在。");
                name.Focus(FocusState.Programmatic);
                return;
            }

            bool renameCurrent = index.HasValue && sceneName != original.Name && _catalog.ReadCurrent() == original.Name;
            if (!SaveScene(index, new ScenePreview(sceneName, apps.ToArray(), original.Bindings, bindings))) return;
            if (renameCurrent && !SelectScene(sceneName))
            {
                _updateSceneRules(_scenes, null);
                ShowOverview();
                ShowNotice("场景已保存，无法更新当前场景。");
                return;
            }
            if (!renameCurrent) _updateSceneRules(_scenes, null);
            ShowOverview();
        }
        save.Click += (_, _) => Save();
        _resetScroll();
    }

    private bool SaveScene(int? index, ScenePreview scene)
    {
        var updated = new List<ScenePreview>(_scenes);
        if (index is int position) updated[position] = scene;
        else updated.Add(scene);

        try { _catalog.Save(updated); }
        catch (Exception error) when (IsStorageError(error))
        {
            ShowNotice("无法保存场景。");
            return false;
        }
        _scenes.Clear();
        _scenes.AddRange(updated);
        _notice.Visibility = Visibility.Collapsed;
        return true;
    }

    private bool SelectScene(string name)
    {
        try { _catalog.SetCurrent(name); }
        catch (Exception error) when (IsStorageError(error))
        {
            ShowNotice("无法保存当前场景。");
            return false;
        }
        _updateSceneRules(_scenes, name);
        _notice.Visibility = Visibility.Collapsed;
        return true;
    }

    private void ShowNotice(string message)
    {
        _notice.Text = message;
        _notice.Visibility = Visibility.Visible;
    }

    public void ShowActivationStatus(string? message)
    {
        _activationNotice.Text = message ?? "";
        _activationNotice.Visibility = message is null ? Visibility.Collapsed : Visibility.Visible;
    }

    private void CloseEditor()
    {
        _editor?.Cancel();
        _editor?.Dispose();
        _editor = null;
        if (_showForeground is not null) _foreground.ForegroundApplicationChanged -= _showForeground;
        _showForeground = null;
        _updateDeviceRows = null;
        _batteryLabels.Clear();
        _readDeviceOptions.Clear();
    }

    private static bool IsStorageError(Exception error) => error is IOException or UnauthorizedAccessException;

    private static Button Tile(string title)
    {
        var heading = PageLayout.SecondaryHeadingText(title);
        heading.TextTrimming = TextTrimming.CharacterEllipsis;
        heading.TextAlignment = TextAlignment.Center;
        return new Button
        {
            Style = (Style)Application.Current.Resources["AccentButtonStyle"],
            Content = heading,
            HorizontalAlignment = HorizontalAlignment.Stretch,
            VerticalAlignment = VerticalAlignment.Stretch,
            Padding = new Thickness(3, 6, 3, 6),
            HorizontalContentAlignment = HorizontalAlignment.Center,
            VerticalContentAlignment = VerticalAlignment.Center
        };
    }

    private void AddDeviceRows(Func<ConnectedPeripheral, Border> render, Action? onUpdate = null)
    {
        var rows = new StackPanel { Spacing = PageLayout.SettingsRowGap };
        var notice = new TextBlock { TextWrapping = TextWrapping.Wrap };
        rows.Children.Add(notice);
        _body.Children.Add(rows);
        var cards = new Dictionary<string, (ConnectedPeripheral Device, Border Card)>(StringComparer.Ordinal);
        void Update()
        {
            notice.Text = _deviceMessage ?? (_devices.Length == 0 ? "没有已连接设备" : "");
            notice.Visibility = notice.Text.Length > 0 ? Visibility.Visible : Visibility.Collapsed;
            var connected = _devices.Select(device => device.Id).ToHashSet(StringComparer.Ordinal);
            foreach (string id in cards.Keys.Where(id => !connected.Contains(id)).ToArray())
            {
                rows.Children.Remove(cards[id].Card);
                cards.Remove(id);
                _batteryLabels.Remove(id);
                _readDeviceOptions.Remove(id);
            }
            for (int index = 0; index < _devices.Length; index++)
            {
                var device = _devices[index];
                if (cards.TryGetValue(device.Id, out var existing)
                    && existing.Device.Name == device.Name && existing.Device.Category == device.Category
                    && Peripherals.ConnectionLabel(existing.Device) == Peripherals.ConnectionLabel(device)
                    && existing.Device.Artwork == device.Artwork
                    && existing.Device.DeviceProvider == device.DeviceProvider)
                {
                    int current = rows.Children.IndexOf(existing.Card);
                    if (current != index + 1)
                    {
                        rows.Children.RemoveAt(current);
                        rows.Children.Insert(index + 1, existing.Card);
                    }
                    continue;
                }
                if (existing.Card is not null)
                {
                    rows.Children.Remove(existing.Card);
                    _batteryLabels.Remove(device.Id);
                    _readDeviceOptions.Remove(device.Id);
                }
                var card = render(device);
                rows.Children.Insert(index + 1, card);
                cards[device.Id] = (device, card);
            }
            onUpdate?.Invoke();
        }
        _updateDeviceRows = Update;
        Update();
    }

    public void RefreshDeviceConfigurations(string id)
    {
        if (_readDeviceOptions.TryGetValue(id, out var read)) _ = read();
    }

    private void ShowDevices(ConnectedPeripheral[] devices, string? message, bool _)
    {
        if (_devices.SequenceEqual(devices) && _deviceMessage == message) return;
        _devices = devices;
        _deviceMessage = message;
        _updateDeviceRows?.Invoke();
    }

    private Border DeviceRow(ConnectedPeripheral device, FrameworkElement? action = null)
    {
        var row = new Grid { ColumnSpacing = 16 };
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(42) });
        row.ColumnDefinitions.Add(new ColumnDefinition());

        row.Children.Add(Peripherals.DeviceThumbnail(device, 42));
        var identity = new StackPanel { Spacing = 2, VerticalAlignment = VerticalAlignment.Center };
        identity.Children.Add(new TextBlock
        {
            Text = device.Name, Style = (Style)Application.Current.Resources["BodyTextBlockStyle"],
            TextWrapping = TextWrapping.NoWrap
        });
        var details = Peripherals.DeviceConnectionDetails(device);
        if (Peripherals.ConnectionLabel(device) == "2.4G")
        {
            var battery = new TextBlock { Style = (Style)Application.Current.Resources["DashboardDetailStyle"],
                Visibility = Visibility.Collapsed };
            _batteryLabels[device.Id] = battery;
            details.Children.Add(battery);
            ShowBattery(device.Id, battery);
        }
        identity.Children.Add(details);
        Grid.SetColumn(identity, 1);
        row.Children.Add(identity);
        if (action is not null)
        {
            row.ColumnDefinitions.Add(new ColumnDefinition());
            row.RowDefinitions.Add(new RowDefinition());
            row.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            row.Children.Add(action);
            void Arrange(double width)
            {
                bool stacked = width > 0 && width < 440;
                row.ColumnDefinitions[1].Width = stacked ? new GridLength(1, GridUnitType.Star) : new GridLength(206);
                row.ColumnDefinitions[2].Width = stacked ? new GridLength(0) : new GridLength(1, GridUnitType.Star);
                row.RowSpacing = stacked ? 5 : 0;
                Grid.SetColumn(action, stacked ? 0 : 2);
                Grid.SetColumnSpan(action, stacked ? 3 : 1);
                Grid.SetRow(action, stacked ? 1 : 0);
            }
            Arrange(0);
            row.SizeChanged += (_, args) => Arrange(args.NewSize.Width);
        }
        var card = new Border
        {
            Child = row,
            Style = (Style)Application.Current.Resources["DashboardCardStyle"],
            MinHeight = 67,
            Padding = new Thickness(16, 6, 16, 6)
        };
        if (device.HasDevicePlugin || device.DeviceProvider is not null)
        {
            card.DoubleTapped += (_, args) =>
            {
                for (var source = args.OriginalSource as DependencyObject; source is not null && source != card;
                     source = VisualTreeHelper.GetParent(source))
                    if (source is Microsoft.UI.Xaml.Controls.Primitives.ButtonBase) return;
                args.Handled = true;
                _openDevice(device);
            };
            ToolTipService.SetToolTip(identity, "双击打开设备插件");
        }
        return card;
    }
}

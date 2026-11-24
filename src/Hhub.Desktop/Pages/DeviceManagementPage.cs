using Hhub.PluginContracts;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;

namespace Hhub.Desktop.Pages;

internal sealed class DeviceManagementPage : StackPanel
{
    private readonly PeripheralInventory _inventory;
    private readonly Action _back;
    private readonly InfoBar _notice = new()
    {
        IsOpen = false, IsClosable = true, Severity = InfoBarSeverity.Success
    };
    private readonly StackPanel _rows = new() { Spacing = PageLayout.SettingsRowGap };
    private string _signature = "";

    public DeviceManagementPage(PeripheralInventory inventory, Action back)
    {
        _inventory = inventory;
        _back = back;
        Margin = PageLayout.Inset;
        Spacing = 12;
        HorizontalAlignment = HorizontalAlignment.Center;
        Children.Add(Header());
        Children.Add(_notice);
        Children.Add(_rows);
        Loaded += Start;
    }

    private FrameworkElement Header()
    {
        var header = new Grid();
        header.ColumnDefinitions.Add(new ColumnDefinition());
        header.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        var breadcrumb = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 10,
            VerticalAlignment = VerticalAlignment.Center };
        var back = new Button
        {
            Content = PageLayout.BreadcrumbText("首页"),
            Style = (Style)Application.Current.Resources["SubtleButtonStyle"],
            Padding = new Thickness(0), MinWidth = 0, VerticalAlignment = VerticalAlignment.Stretch
        };
        back.Click += (_, _) => _back();
        breadcrumb.Children.Add(back);
        var separator = new TextBlock { Text = "›", VerticalAlignment = VerticalAlignment.Center };
        breadcrumb.Children.Add(separator);
        breadcrumb.Children.Add(PageLayout.HeadingText("设备"));
        header.Children.Add(breadcrumb);
        var add = new Button
        {
            Content = "添加设备", VerticalAlignment = VerticalAlignment.Center,
            Style = (Style)Application.Current.Resources["AccentButtonStyle"]
        };
        AutomationProperties.SetName(add, "添加设备");
        add.Click += async (_, _) => await AddDeviceAsync();
        Grid.SetColumn(add, 1);
        header.Children.Add(add);
        return header;
    }

    public void ResizeToViewport(double width)
    {
        if (width <= 0) return;
        double inset = PageLayout.HorizontalInset(width);
        Margin = new Thickness(inset, 0, inset, PageLayout.Inset.Bottom);
        Width = PageLayout.ContentWidth(width);
    }

    public void ClosePage()
    {
        Loaded -= Start;
        _inventory.Unsubscribe(ShowInventory);
    }

    private void Start(object sender, RoutedEventArgs args)
    {
        Loaded -= Start;
        _inventory.Subscribe(ShowInventory);
    }

    private void ShowInventory(PeripheralInventory.Snapshot snapshot) =>
        ShowDevices(snapshot.Devices, snapshot.Message, snapshot.Available);

    private void ShowDevices(ConnectedPeripheral[] devices, string? message, bool available)
    {
        string signature = string.Join("\n", devices.Select(device =>
            $"{device.Id}|{device.Name}|{device.Connection}")) + "|" + available + "|" + message;
        if (signature == _signature) return;
        _signature = signature;
        ShowRows(devices, available ? null : message ?? "无法读取设备");
    }

    private void ShowRows(ConnectedPeripheral[] devices, string? error)
    {
        _rows.Children.Clear();
        if (error is { Length: > 0 })
            _rows.Children.Add(new TextBlock
            {
                Text = error, Style = (Style)Application.Current.Resources["DashboardDetailStyle"]
            });
        if (devices.Length == 0 && error is null)
            _rows.Children.Add(new TextBlock
            {
                Text = "未发现设备", Style = (Style)Application.Current.Resources["DashboardDetailStyle"]
            });
        foreach (var device in devices) _rows.Children.Add(DeviceRow(device));
    }

    private FrameworkElement DeviceRow(ConnectedPeripheral device)
    {
        var row = new Grid { ColumnSpacing = 16 };
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(24) });
        row.ColumnDefinitions.Add(new ColumnDefinition());
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        row.Children.Add(new FontIcon
        {
            Glyph = Peripherals.Glyph(device), FontSize = 20, FontFamily = new FontFamily("Segoe Fluent Icons"),
            VerticalAlignment = VerticalAlignment.Center
        });
        var identity = new StackPanel { Spacing = 2, VerticalAlignment = VerticalAlignment.Center };
        identity.Children.Add(new TextBlock
        {
            Text = device.Name, Style = (Style)Application.Current.Resources["BodyTextBlockStyle"],
            TextWrapping = TextWrapping.NoWrap, TextTrimming = TextTrimming.CharacterEllipsis
        });
        identity.Children.Add(Peripherals.DeviceConnectionDetails(device));
        Grid.SetColumn(identity, 1);
        row.Children.Add(identity);
        var remove = new Button { Content = "移除", VerticalAlignment = VerticalAlignment.Center };
        AutomationProperties.SetName(remove, $"移除 {device.Name}");
        remove.Click += async (_, _) => await RemoveDeviceAsync(device);
        Grid.SetColumn(remove, 2);
        row.Children.Add(remove);
        return new Border
        {
            Child = row,
            Style = (Style)Application.Current.Resources["DashboardCardStyle"],
            MinHeight = 67,
            Padding = new Thickness(16, 6, 16, 6)
        };
    }

    private async Task AddDeviceAsync()
    {
        var name = new TextBox { Header = "设备名称", MaxLength = 60, MinWidth = 320 };
        var error = new TextBlock
        {
            Text = "请输入设备名称", Visibility = Visibility.Collapsed, FontSize = 12,
            Foreground = (Brush)Application.Current.Resources["SystemFillColorCriticalBrush"]
        };
        var content = new StackPanel { Spacing = 8 };
        content.Children.Add(name);
        content.Children.Add(error);
        var dialog = new ContentDialog
        {
            XamlRoot = XamlRoot, Title = "添加设备", Content = content,
            PrimaryButtonText = "添加", CloseButtonText = "取消", DefaultButton = ContentDialogButton.Primary
        };
        dialog.PrimaryButtonClick += (_, args) =>
        {
            if (name.Text.Trim().Length != 0) return;
            args.Cancel = true;
            error.Visibility = Visibility.Visible;
            name.Focus(FocusState.Programmatic);
        };
        if (await dialog.ShowAsync() != ContentDialogResult.Primary) return;
        string title = name.Text.Trim();
        if (title.Length == 0) return;
        ManagedDevices.Add(title);
        ShowNotice($"已添加 {title}");
    }

    private async Task RemoveDeviceAsync(ConnectedPeripheral device)
    {
        var dialog = new ContentDialog
        {
            XamlRoot = XamlRoot, Title = "移除设备",
            Content = new TextBlock
            {
                Text = $"从 HHub 设备列表移除“{device.Name}”。不会卸载驱动或断开硬件。",
                TextWrapping = TextWrapping.Wrap
            },
            PrimaryButtonText = "移除", CloseButtonText = "取消", DefaultButton = ContentDialogButton.Close
        };
        if (await dialog.ShowAsync() != ContentDialogResult.Primary) return;
        var removed = ManagedDevices.Remove(device);
        ShowNotice($"已移除 {device.Name}", () =>
        {
            ManagedDevices.Undo(removed);
            ShowNotice($"已恢复 {device.Name}");
        });
    }

    private void ShowNotice(string message, Action? undo = null)
    {
        _notice.Message = message;
        _notice.Severity = InfoBarSeverity.Success;
        if (undo is null)
        {
            _notice.ActionButton = null;
        }
        else
        {
            var action = new Button { Content = "撤销" };
            AutomationProperties.SetName(action, "撤销移除");
            action.Click += (_, _) =>
            {
                undo();
                _notice.IsOpen = false;
            };
            _notice.ActionButton = action;
        }
        _notice.IsOpen = true;
    }
}

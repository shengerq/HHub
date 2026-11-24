using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Windows.System;

namespace Hhub.Desktop.Pages;

internal sealed class DeviceKeyTest : StackPanel
{
    internal sealed record Key(string Label, int? ScanCode = null, double Width = 1);

    private readonly ContentControl _surface;
    private readonly Button _start = new() { Content = "开始测试" };
    private readonly Button _clear = new() { Content = "清空", IsEnabled = false };
    private readonly TextBlock _status = new() { Text = "已测 0 键" };
    private readonly Dictionary<int, (Border Cell, TextBlock Label)> _keys = [];
    private readonly HashSet<int> _pressed = [];
    private readonly HashSet<int> _seen = [];
    private Window? _window;

    public static bool IsValid(Key[][] rows)
    {
        if (rows.Length is < 1 or > 8 || rows.Any(row => row is null || row.Length is < 1 or > 24))
            return false;
        var keys = rows.SelectMany(row => row).ToArray();
        if (keys.Any(key => key is null || string.IsNullOrWhiteSpace(key.Label) || key.Label.Length > 16
            || !double.IsFinite(key.Width) || key.Width is < 0.5 or > 12
            || key.ScanCode is < 1 or 256 or > 511))
            return false;
        var codes = keys.Select(key => key.ScanCode).OfType<int>().ToArray();
        return codes.Length > 0 && codes.Distinct().Count() == codes.Length;
    }

    public DeviceKeyTest(Key[][] rows)
    {
        Spacing = PageLayout.SettingsRowGap;
        var instructions = new TextBlock
        {
            Text = "按 Esc 结束测试",
            TextWrapping = TextWrapping.Wrap,
            Style = Resource("DashboardDetailStyle")
        };
        var keyboard = new StackPanel { Spacing = 4, Width = 660 };
        foreach (var row in rows)
        {
            var line = new Grid { ColumnSpacing = 4 };
            foreach (var key in row)
            {
                var label = new TextBlock
                {
                    Text = key.Label,
                    FontSize = 15,
                    TextTrimming = TextTrimming.CharacterEllipsis,
                    HorizontalAlignment = HorizontalAlignment.Center,
                    VerticalAlignment = VerticalAlignment.Center
                };
                var cell = new Border { Style = Resource("DeviceKeyCellStyle"), Height = 38, Child = label };
                if (key.ScanCode is int code) _keys.Add(code, (cell, label));
                else label.Opacity = 0.55;
                Grid.SetColumn(cell, line.ColumnDefinitions.Count);
                line.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(key.Width, GridUnitType.Star) });
                line.Children.Add(cell);
            }
            keyboard.Children.Add(line);
        }
        _surface = new ContentControl
        {
            IsTabStop = true,
            UseSystemFocusVisuals = true,
            HorizontalContentAlignment = HorizontalAlignment.Stretch,
            Content = new Viewbox { MaxWidth = 660, StretchDirection = StretchDirection.DownOnly, Child = keyboard }
        };
        AutomationProperties.SetName(_surface, "按键测试区域");
        AutomationProperties.SetHelpText(_surface, "按 Esc 退出");
        _surface.PointerPressed += (_, _) => _surface.Focus(FocusState.Pointer);
        _surface.LostFocus += (_, _) => ClearPressed();
        _surface.AddHandler(KeyDownEvent, new KeyEventHandler(OnKeyDown), handledEventsToo: true);
        _surface.AddHandler(KeyUpEvent, new KeyEventHandler(OnKeyUp), handledEventsToo: true);
        _start.Click += (_, _) => _surface.Focus(FocusState.Keyboard);
        _clear.Click += (_, _) =>
        {
            ClearPressed();
            var previous = _seen.ToArray();
            _seen.Clear();
            foreach (int code in previous) UpdateKey(code);
            _status.Text = "已测 0 键";
            _start.Focus(FocusState.Programmatic);
            _clear.IsEnabled = false;
        };
        var buttons = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8 };
        buttons.Children.Add(_start);
        buttons.Children.Add(_clear);
        Children.Add(instructions);
        Children.Add(_surface);
        Children.Add(buttons);
        Children.Add(_status);
        Loaded += (_, _) =>
        {
            if (_window is null && (Application.Current as App)?.MainWindow is { } window)
            {
                _window = window;
                window.Activated += OnWindowActivated;
            }
        };
        Unloaded += (_, _) =>
        {
            if (_window is not null) _window.Activated -= OnWindowActivated;
            _window = null;
            ClearPressed();
        };
    }

    private void OnWindowActivated(object sender, WindowActivatedEventArgs args)
    {
        if (args.WindowActivationState == WindowActivationState.Deactivated) ClearPressed();
    }

    private static int ScanCode(KeyRoutedEventArgs args) =>
        (int)(args.KeyStatus.ScanCode & 0xff) | (args.KeyStatus.IsExtendedKey ? 0x100 : 0);

    private void OnKeyDown(object sender, KeyRoutedEventArgs args)
    {
        args.Handled = true;
        int code = ScanCode(args);
        if (_keys.TryGetValue(code, out var key))
        {
            _seen.Add(code);
            _pressed.Add(code);
            UpdateKey(code);
            _clear.IsEnabled = true;
            _status.Text = $"已测 {_seen.Count} 键 · {key.Label.Text}";
        }
        if (args.Key == VirtualKey.Escape) _start.Focus(FocusState.Keyboard);
    }

    private void OnKeyUp(object sender, KeyRoutedEventArgs args)
    {
        args.Handled = true;
        int code = ScanCode(args);
        if (_pressed.Remove(code)) UpdateKey(code);
    }

    private void ClearPressed()
    {
        var previous = _pressed.ToArray();
        _pressed.Clear();
        foreach (int code in previous) UpdateKey(code);
    }

    private void UpdateKey(int code)
    {
        var (cell, label) = _keys[code];
        bool pressed = _pressed.Contains(code);
        cell.Style = Resource(pressed ? "DeviceKeyPressedStyle" : _seen.Contains(code) ? "DeviceKeySeenStyle" : "DeviceKeyCellStyle");
        label.Style = pressed ? Resource("DeviceKeyPressedTextStyle") : null;
    }

    private static Style Resource(string key) => (Style)Application.Current.Resources[key];
}

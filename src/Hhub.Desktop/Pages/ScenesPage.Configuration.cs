using Hhub.PluginContracts;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;

namespace Hhub.Desktop.Pages;

internal sealed partial class ScenesPage
{
    private void AddRunningApplications(Action<string> addApplication, Button browse, CancellationToken token)
    {
        var foreground = new TextBlock { TextTrimming = TextTrimming.CharacterEllipsis };
        void ShowForeground(string? application) =>
            foreground.Text = application is null ? "当前前台应用未识别" : $"当前前台应用　{application}.exe";
        _showForeground = ShowForeground;
        _foreground.ForegroundApplicationChanged += ShowForeground;
        ShowForeground(_foreground.ForegroundApplication);

        var choices = new ComboBox { PlaceholderText = "选择运行中的应用", HorizontalAlignment = HorizontalAlignment.Stretch,
            DisplayMemberPath = nameof(ForegroundScenes.RunningApplication.Label), MaxDropDownHeight = 260 };
        AutomationProperties.SetName(choices, "运行中的应用");
        var add = PageLayout.AccentButton("添加"); add.IsEnabled = false;
        var refresh = new Button { Content = "刷新" };
        var row = ControlsRow(choices, add, refresh, browse);
        var picker = new StackPanel { Spacing = 6 };
        add.Click += (_, _) =>
        {
            if (choices.SelectedItem is ForegroundScenes.RunningApplication application)
                addApplication(application.Executable);
        };
        choices.SelectionChanged += (_, _) => add.IsEnabled = choices.SelectedItem is not null;
        choices.KeyDown += (_, args) =>
        {
            if (args.Key != Windows.System.VirtualKey.Enter || choices.IsDropDownOpen
                || choices.SelectedItem is not ForegroundScenes.RunningApplication application) return;
            addApplication(application.Executable);
            args.Handled = true;
        };
        async Task RefreshAsync()
        {
            refresh.IsEnabled = false;
            try
            {
                var running = await Task.Run(ForegroundScenes.ReadRunningApplications, token);
                if (token.IsCancellationRequested) return;
                string? selected = (choices.SelectedItem as ForegroundScenes.RunningApplication)?.Executable;
                choices.ItemsSource = running;
                choices.SelectedItem = running.FirstOrDefault(application => application.Executable == selected)
                    ?? running.FirstOrDefault(application => string.Equals(application.Executable,
                        _foreground.ForegroundApplication + ".exe", StringComparison.OrdinalIgnoreCase));
                choices.PlaceholderText = running.Length == 0 ? "没有运行中的窗口应用" : "选择运行中的应用";
                choices.IsEnabled = running.Length > 0;
            }
            catch (OperationCanceledException) when (token.IsCancellationRequested) { }
            catch (Exception error) when (SceneDeviceConfigurations.IsDeviceError(error))
            {
                System.Diagnostics.Trace.WriteLine(error);
                if (!token.IsCancellationRequested) choices.PlaceholderText = "无法读取应用，请刷新";
            }
            finally { if (!token.IsCancellationRequested) refresh.IsEnabled = true; }
        }
        refresh.Click += async (_, _) => await RefreshAsync();
        picker.Children.Add(foreground);
        picker.Children.Add(row);
        _body.Children.Add(PageLayout.CardRow(picker, null));
        _ = RefreshAsync();
    }

    private Border ConfigurationDeviceRow(ConnectedPeripheral device,
        Dictionary<string, SceneDeviceBinding>? bindings, Action? bindingChanged, CancellationToken token)
    {
        if (device.DeviceProvider is not { } provider) return DeviceRow(device);
        string target = device.Id;
        var choices = new ComboBox { MinWidth = 128, MaxWidth = 224, MaxDropDownHeight = 320,
            HorizontalAlignment = HorizontalAlignment.Right, Visibility = Visibility.Collapsed };
        AutomationProperties.SetName(choices, $"{device.Name} 场景配置");
        var status = new TextBlock { TextWrapping = TextWrapping.Wrap, Visibility = Visibility.Collapsed };
        var actions = new StackPanel { Spacing = 4, Visibility = Visibility.Collapsed, Children = { choices, status } };
        var card = DeviceRow(device, actions);
        bool reading = false;
        bool refreshing = false;
        bool saving = false;
        bool canCapture = false;
        SceneConfiguration[] options = [];

        void ShowChoices()
        {
            refreshing = true;
            try
            {
                var selected = bindings?.GetValueOrDefault(target);
                var items = new List<ComboBoxItem>();
                if (bindings is not null && (options.Length != 0 || selected is not null))
                    items.Add(new ComboBoxItem { Content = "不加入场景" });
                foreach (var option in options)
                    items.Add(new ComboBoxItem { Content = SceneDeviceConfigurations.MenuLabel(option), Tag = option });
                if (canCapture)
                {
                    var add = DeviceConfigurationMenu.AddOption();
                    add.Tag = "+";
                    items.Add(add);
                }
                var active = selected?.PluginId == provider ? selected.Configuration : null;
                if (choices.Items.Count != items.Count || choices.Items.Cast<ComboBoxItem>().Zip(items)
                    .Any(pair => !Equals(pair.First.Tag, pair.Second.Tag) || !Equals(pair.First.Content, pair.Second.Content)))
                    choices.ItemsSource = items;
                int index = active is null ? -1 : items.FindIndex(item => Equals(item.Tag, active));
                if (choices.SelectedIndex != index) choices.SelectedIndex = index;
                choices.PlaceholderText = options.Length == 0 && canCapture ? "＋ 添加配置" : "选择配置";
                choices.Visibility = actions.Visibility = items.Count == 0 ? Visibility.Collapsed : Visibility.Visible;
            }
            finally { refreshing = false; }
        }

        choices.SelectionChanged += async (_, _) =>
        {
            if (refreshing || saving || token.IsCancellationRequested || choices.SelectedItem is not ComboBoxItem item) return;
            choices.IsDropDownOpen = false;
            if (bindings is null && item.Tag is SceneConfiguration)
            {
                _openDevice(device);
                return;
            }
            var configuration = item.Tag as SceneConfiguration;
            if (item.Tag is string && choices.XamlRoot is { } root)
            {
                saving = true;
                choices.IsEnabled = false;
                status.Visibility = Visibility.Collapsed;
                try
                {
                    string? name = await DeviceConfigurationMenu.AskNameAsync(root, options.Select(SceneDeviceConfigurations.MenuLabel));
                    if (name is null || token.IsCancellationRequested) return;
                    configuration = await _configurations.CaptureAsync(provider, target, name, token);
                    options = [.. options, configuration];
                }
                catch (OperationCanceledException) when (token.IsCancellationRequested) { return; }
                catch (Exception error) when (SceneDeviceConfigurations.IsDeviceError(error))
                {
                    System.Diagnostics.Trace.WriteLine(error);
                    status.Text = "无法保存配置，请确认设备已连接并唤醒。";
                    status.Visibility = Visibility.Visible;
                    return;
                }
                finally { saving = false; choices.IsEnabled = true; ShowChoices(); }
            }
            if (token.IsCancellationRequested || bindings is null) return;
            if (configuration is null) bindings.Remove(target);
            else bindings[target] = new(provider, device.Name, configuration);
            ShowChoices();
            bindingChanged?.Invoke();
        };

        async Task ReadOptionsAsync()
        {
            if (reading || saving || token.IsCancellationRequested) return;
            reading = true;
            try
            {
                var result = await _configurations.ListAsync(provider, target, token);
                if (token.IsCancellationRequested || saving) return;
                options = result.Configurations;
                canCapture = result.CanCapture;
                ShowChoices();
            }
            catch (OperationCanceledException) when (token.IsCancellationRequested) { }
            catch (Exception error) when (SceneDeviceConfigurations.IsDeviceError(error))
            { System.Diagnostics.Trace.WriteLine(error); }
            finally { reading = false; }
        }

        _readDeviceOptions[target] = ReadOptionsAsync;
        _ = ReadOptionsAsync();
        return card;
    }

    private static Grid ControlsRow(FrameworkElement first, params FrameworkElement[] actions)
    {
        var row = new Grid { ColumnSpacing = 6 };
        row.ColumnDefinitions.Add(new());
        first.VerticalAlignment = VerticalAlignment.Center;
        row.Children.Add(first);
        foreach (var action in actions)
        {
            Grid.SetColumn(action, row.ColumnDefinitions.Count);
            row.ColumnDefinitions.Add(new() { Width = GridLength.Auto });
            action.VerticalAlignment = VerticalAlignment.Center;
            row.Children.Add(action);
        }
        return row;
    }
}

using System.Text.Json;
using Hhub.UnifiedHost;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;

namespace Hhub.Desktop.Pages;

internal sealed class DeviceConfigurationMenu : StackPanel
{
    private readonly PluginStore _store;
    private readonly InstalledPackage _plugin;
    private readonly Func<SceneConfiguration, Task> _apply;
    private readonly Func<string, Task> _save;
    private readonly ComboBox _choices = new()
    {
        MinWidth = 128, MaxWidth = 224, MaxDropDownHeight = 320,
        HorizontalAlignment = HorizontalAlignment.Right, PlaceholderText = "配置"
    };
    private readonly TextBlock _status = new() { TextWrapping = TextWrapping.Wrap, MaxWidth = 224, Visibility = Visibility.Collapsed };
    private string? _target;
    private string? _selected;
    private IReadOnlyDictionary<string, JsonElement> _values = new Dictionary<string, JsonElement>();
    private SceneConfiguration[] _presets = [];
    private SceneConfiguration[] _displayedOptions = [];
    private bool _displayedAdd;
    private int _displayedSelection = -1;
    private bool _refreshing;
    private bool _busy;

    public DeviceConfigurationMenu(PluginStore store, InstalledPackage plugin, Func<SceneConfiguration, Task> apply, Func<string, Task> save)
    {
        (_store, _plugin, _apply, _save) = (store, plugin, apply, save);
        Spacing = 4;
        Visibility = Visibility.Collapsed;
        AutomationProperties.SetName(_choices, "设备配置");
        Children.Add(_choices);
        Children.Add(_status);

        _choices.SelectionChanged += async (_, _) =>
        {
            if (_refreshing || _busy || _target is not { } target || _choices.SelectedItem is not ComboBoxItem item) return;
            _choices.IsDropDownOpen = false;
            _busy = true;
            _choices.IsEnabled = false;
            SetError(null);
            try
            {
                if (item.Tag is SceneConfiguration configuration)
                {
                    await _apply(configuration);
                    if (_target == target) _selected = configuration.PresetId;
                }
                else if (XamlRoot is { } root)
                {
                    var name = await AskNameAsync(root, _choices.Items.OfType<ComboBoxItem>()
                        .Select(option => option.Tag).OfType<SceneConfiguration>().Select(SceneDeviceConfigurations.MenuLabel));
                    if (name is not null && _target == target)
                    {
                        await _save(name);
                        ReadPresets();
                        if (_target == target) _selected = _presets.FirstOrDefault(preset => preset.Name == name)?.PresetId;
                    }
                }
            }
            catch (OperationCanceledException) { }
            catch (Exception error) when (SceneDeviceConfigurations.IsDeviceError(error))
            {
                System.Diagnostics.Trace.WriteLine(error);
                SetError("无法保存或应用配置，请确认设备已连接并唤醒。");
            }
            finally { _busy = false; Refresh(); }
        };
    }

    public void Display(string? target, IReadOnlyDictionary<string, JsonElement> values)
    {
        bool changed = _target != target;
        if (changed)
        {
            _choices.IsDropDownOpen = false;
            _selected = null;
            SetError(null);
        }
        _target = target;
        _values = values;
        if (changed) ReadPresets();
        if (!_busy && !_choices.IsDropDownOpen) Refresh();
    }

    private void ReadPresets()
    {
        try
        {
            _presets = _target is null ? [] : _store.ListPresets(_plugin.Definition.Id, _target)
                .Select(preset => new SceneConfiguration(preset.Name, PresetId: preset.PresetId)).ToArray();
        }
        catch (Exception error) when (SceneDeviceConfigurations.IsDeviceError(error))
        {
            System.Diagnostics.Trace.WriteLine(error);
            SetError("无法读取设备配置。");
        }
    }

    private void Refresh()
    {
        _refreshing = true;
        try
        {
            var native = SceneDeviceConfigurations.ReadSelectors(_plugin.Definition, _values).ToArray();
            SceneConfiguration[] options = [.. native.Select(item => item.Configuration), .. _presets];
            bool canAdd = _target is not null && SceneDeviceConfigurations.CanSaveFromMenu(_plugin.Definition);
            bool changed = _displayedAdd != canAdd || !_displayedOptions.SequenceEqual(options);
            if (changed)
            {
                _displayedOptions = options;
                _displayedAdd = canAdd;
                var items = options.Select(configuration => new ComboBoxItem
                {
                    Content = SceneDeviceConfigurations.MenuLabel(configuration), Tag = configuration
                }).ToList();
                if (canAdd) items.Add(AddOption());
                _choices.ItemsSource = items;
            }
            int selected = _selected is null ? -1 : Array.FindIndex(options, option => option.PresetId == _selected);
            if (selected < 0)
                selected = Array.FindIndex(native, option => _values.TryGetValue(option.Configuration.Selector!, out var value)
                    && value.ValueKind == JsonValueKind.Array && value.GetArrayLength() == 1
                    && value[0].TryGetInt32(out int index) && index == option.Index);

            if (_displayedSelection != selected || _choices.SelectedIndex != selected)
            {
                _displayedSelection = selected;
                _choices.SelectedIndex = selected;
            }
            _choices.PlaceholderText = options.Length == 0 && canAdd ? "＋ 添加配置" : "选择配置";
            _choices.IsEnabled = !_busy && _choices.Items.Count > 0;
            Visibility = _target is not null && _choices.Items.Count > 0 ? Visibility.Visible : Visibility.Collapsed;
        }
        finally { _refreshing = false; }
    }

    internal static ComboBoxItem AddOption() => new() { Content = "＋ 添加配置" };

    internal static async Task<string?> AskNameAsync(XamlRoot root, IEnumerable<string> existing)
    {
        var names = existing.ToHashSet(StringComparer.OrdinalIgnoreCase);
        var input = new TextBox { Header = "配置名称", MaxLength = 64 };
        var error = new TextBlock { TextWrapping = TextWrapping.Wrap };
        var content = new StackPanel { Spacing = 4, Children = { input, error } };
        var dialog = new ContentDialog
        {
            XamlRoot = root, Title = "添加配置", Content = content,
            PrimaryButtonText = "保存当前设置", CloseButtonText = "取消", IsPrimaryButtonEnabled = false
        };
        input.TextChanged += (_, _) =>
        {
            string name = input.Text.Trim();
            bool duplicate = names.Contains(name);
            error.Text = duplicate ? "该配置名称已存在。" : "";
            dialog.IsPrimaryButtonEnabled = name.Length > 0 && !duplicate;
        };
        return await dialog.ShowAsync() == ContentDialogResult.Primary ? input.Text.Trim() : null;
    }

    private void SetError(string? message)
    {
        _status.Text = message ?? "";
        _status.Visibility = message is null ? Visibility.Collapsed : Visibility.Visible;
    }
}

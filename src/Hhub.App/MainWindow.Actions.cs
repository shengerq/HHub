using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;

namespace Hhub.App;

public sealed partial class MainWindow
{
    private const double BodyTextSize = ShellLayout.BodyFontSize;
    private const double SectionTextSize = ShellLayout.TitleFontSize;
    private const double FieldColumnWidth = 176;
    private bool _selectorOpen;
    private int _selectorEpoch;

    private ComboBox SettingsChoice(string selectedId, IEnumerable<(string Id, string Name)> choices, Func<string, Task> select)
    {
        var options = choices.ToArray();
        var control = new ComboBox
        {
            ItemsSource = options.Select(option => option.Name).ToArray(), Width = FieldColumnWidth,
            SelectedIndex = Array.FindIndex(options, option => option.Id == selectedId)
        };
        int revision = 0;
        string committed = selectedId;
        async Task CommitAfterAnimationAsync()
        {
            int current = ++revision;
            int epoch = _selectorEpoch;
            // Let the native popup finish its closing transition before disk/process work.
            await Task.Delay(180);
            if (current != revision || control.IsDropDownOpen || _closing) return;
            if (epoch == _selectorEpoch)
            {
                _selectorOpen = false;
                RuntimeChanged();
            }
            if (control.SelectedIndex >= 0 && options[control.SelectedIndex].Id != committed)
            {
                string chosen = options[control.SelectedIndex].Id;
                control.IsEnabled = false;
                try
                {
                    SetSettingNotice(control, null);
                    await select(chosen); committed = chosen;
                }
                catch (Exception error) when (error is IOException or InvalidOperationException or System.Text.Json.JsonException
                    or UnauthorizedAccessException or System.ComponentModel.Win32Exception or ArgumentException)
                {
                    if (_settingsRowNotices.ContainsKey(control)) SetSettingNotice(control, error.Message);
                    else ShowError(error.Message);
                    control.SelectedIndex = Array.FindIndex(options, option => option.Id == committed);
                }
                finally { control.IsEnabled = true; }
            }
            if (_visualRefreshPending && !_selectorOpen) RequestVisualRefresh();
        }
        control.DropDownOpened += (_, _) => { revision++; _selectorEpoch++; _selectorOpen = true; };
        control.DropDownClosed += async (_, _) => await CommitAfterAnimationAsync();
        control.SelectionChanged += async (_, _) =>
        {
            if (!control.IsDropDownOpen) await CommitAfterAnimationAsync();
        };
        return control;
    }

    private SelectorBar SectionTabs(IEnumerable<(string Id, string Name)> sections, string selected, Action<string> select)
    {
        var tabs = new SelectorBar { VerticalAlignment = VerticalAlignment.Center };
        foreach (var (id, name) in sections)
        {
            var item = new SelectorBarItem { Text = name, Tag = id };
            tabs.Items.Add(item); if (id == selected) tabs.SelectedItem = item;
        }
        tabs.SelectionChanged += (_, _) =>
        {
            string? id = tabs.SelectedItem?.Tag as string;
            if (id == selected) return;
            selected = id ?? "";
            if (id is not null) select(id);
        };
        return tabs;
    }

    private Button IconButton(string glyph, string name, Action action)
    {
        var button = Button("", action);
        button.Content = Icon(glyph, 18); button.MinWidth = 0; button.Width = 32; button.Padding = new(0);
        ToolTipService.SetToolTip(button, name); AutomationProperties.SetName(button, name);
        return button;
    }

    private Button ActionMenu(params (string Name, Func<Task> Action)[] actions)
    {
        var button = IconButton("\uE712", "更多操作", () => { });
        var menu = new MenuFlyout();
        foreach (var action in actions)
        {
            var item = new MenuFlyoutItem { Text = action.Name };
            item.Click += async (_, _) => await RunUiActionAsync(action.Action);
            menu.Items.Add(item);
        }
        button.Flyout = menu;
        return button;
    }
}

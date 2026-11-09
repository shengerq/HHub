using Hhub.Core;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Markup;
using Microsoft.UI.Xaml.Media;

namespace Hhub.App;

public sealed partial class MainWindow
{
    private readonly Dictionary<string, NavigationViewItem> _pluginNavigationItems = [];
    private readonly List<Action> _pluginNavigationSwitchUpdates = [];

    private IconElement PluginNavigationIcon(PluginNavigation? navigation)
    {
        if (navigation?.PathData is { } path)
        {
            try
            {
                return new PathIcon { Data = (Geometry)XamlBindingHelper.ConvertValue(typeof(Geometry), path),
                    Width = ShellLayout.NavigationIconSize, Height = ShellLayout.NavigationIconSize };
            }
            catch (Exception error) when (error is ArgumentException or System.Runtime.InteropServices.COMException)
            { /* Invalid optional artwork does not prevent access to the plugin. */ }
        }
        return Icon(string.IsNullOrWhiteSpace(navigation?.Glyph) ? "\uEA86" : navigation.Glyph, ShellLayout.NavigationIconSize);
    }

    private void RebuildPluginNavigation()
    {
        foreach (var item in _pluginNavigationItems.Values) _navigationView.MenuItems.Remove(item);
        _pluginNavigationItems.Clear();
        _pluginNavigationSwitchUpdates.Clear();
        int priorityIndex = _navigationView.MenuItems.IndexOf(_navigationItems["plugins"]) + 1;
        foreach (var plugin in _installed.Plugins.Where(plugin => PluginPolicies.HasNavigation(plugin.Manifest)))
        {
            var navigation = plugin.Manifest.Navigation;
            string name = string.IsNullOrWhiteSpace(navigation?.Title) ? plugin.Manifest.Name : navigation.Title;
            var item = new NavigationViewItem
            {
                Content = name, Tag = plugin, Icon = PluginNavigationIcon(navigation), FontSize = ShellLayout.NavigationFontSize, MinHeight = ShellLayout.NavigationRowHeight,
                IsEnabled = PluginMode(plugin.Manifest.Id) != "disabled"
            };
            ToolTipService.SetToolTip(item, item.IsEnabled ? name : name + "（已停用）");
            if (navigation?.Switch is { } toggle) item.Content = PluginNavigationWithSwitch(plugin, name, toggle);
            _pluginNavigationItems.Add(plugin.Manifest.Id, item);
            if (navigation?.Placement == "after-plugins") _navigationView.MenuItems.Insert(priorityIndex++, item);
            else _navigationView.MenuItems.Add(item);
        }
        RefreshNavigation();
    }

    private Grid PluginNavigationWithSwitch(InstalledPlugin plugin, string name, PluginNavigationSwitch definition)
    {
        var row = new Grid { ColumnSpacing = 6 };
        row.ColumnDefinitions.Add(new() { Width = new(1, GridUnitType.Star) });
        row.ColumnDefinitions.Add(new() { Width = GridLength.Auto });
        row.Children.Add(Text(name, ShellLayout.NavigationFontSize));
        var toggle = new ToggleSwitch { MinWidth = 0, OnContent = "", OffContent = "", VerticalAlignment = VerticalAlignment.Center };
        AutomationProperties.SetName(toggle, name + "开关");
        Grid.SetColumn(toggle, 1); row.Children.Add(toggle);
        bool updating = false, pending = false;
        void Refresh()
        {
            updating = true;
            toggle.Visibility = _preferences.SidebarCollapsed ? Visibility.Collapsed : Visibility.Visible;
            toggle.IsEnabled = !pending && PluginMode(plugin.Manifest.Id) != "disabled";
            toggle.IsOn = _runtime.IsFresh(plugin.Manifest) && PluginPageData.Boolean(_runtime.Snapshot(plugin.Manifest.Id), definition.StateKey);
            updating = false;
        }
        toggle.Toggled += async (_, _) =>
        {
            if (updating || pending) return;
            string command = toggle.IsOn ? definition.OnCommand : definition.OffCommand;
            pending = true; toggle.IsEnabled = false;
            try { await RunUiActionAsync(() => SendPluginCommandAsync(plugin, command)); }
            finally { pending = false; Refresh(); }
        };
        _pluginNavigationSwitchUpdates.Add(Refresh);
        Refresh();
        return row;
    }

    private async Task OpenPluginNavigationAsync(InstalledPlugin plugin)
    {
        bool samePage = _openPlugin?.Manifest.Id == plugin.Manifest.Id && !_pluginSettingsOpen && _openDevice is null;
        _pluginSettingsOpen = false; _openPlugin = plugin; _openDevice = null; _page = "plugins";
        Task? start = StartPluginPageBackend(plugin);
        RefreshNavigation();
        if (!samePage) Render();
        if (start is null) return;
        await start;
        if (!ReferenceEquals(_openPlugin, plugin)) return;
        if (plugin.Manifest.Navigation?.Command is { } command)
            await SendPluginCommandAsync(plugin, command);
    }

    private Task? StartPluginPageBackend(InstalledPlugin plugin)
    {
        string id = plugin.Manifest.Id;
        if (_closing || _localPackageInstallBusy || _installingPlugins.Contains(id) || PluginMode(id) == "disabled") return null;
        _pageStartedPlugins.Add(id);
        return _runtime.EnsureStartedAsync(plugin, retry: true);
    }
}

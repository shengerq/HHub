using Microsoft.UI.Xaml.Media.Animation;

namespace Hhub.App;

public sealed partial class MainWindow
{
    private string? _renderedRoute;

    private static TransitionCollection PageEntrance() => new Windows.UI.ViewManagement.UISettings().AnimationsEnabled
        ? new() { new EntranceThemeTransition { FromHorizontalOffset = 0, FromVerticalOffset = 6, IsStaggeringEnabled = false } }
        : new();

    private void PreparePageEntrance()
    {
        string section = _openPlugin is null && _page == "plugins" ? _pluginTab : _page == "system" ? _monitorTab : "";
        string route = $"{_page}|{section}|{_openPlugin?.Manifest.Id}|{_pluginSettingsOpen}|{_openDevice?.InstanceId}|{_sceneDetailId}";
        var pluginPage = _pluginSettingsOpen ? _openPlugin?.Manifest.SettingsPage : _openPlugin?.Manifest.Page;
        bool immediate = _openPlugin is null && _openDevice is null && _page == "system" || pluginPage?.Layout == "declarative";
        _content.ChildrenTransitions = !immediate && route != _renderedRoute ? PageEntrance() : new();
        _renderedRoute = route;
    }
}

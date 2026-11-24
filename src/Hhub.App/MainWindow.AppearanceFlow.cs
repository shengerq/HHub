using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Media;

namespace Hhub.App;

public sealed partial class MainWindow
{
    private bool _uiLoaded;
    private bool _visualRefreshQueued;
    private bool _visualRefreshPending;
    private bool _themePreferencePending;
    private bool? _appliedBackdrop;

    private ElementTheme RequestedElementTheme => _preferences.Theme switch
    { "light" => ElementTheme.Light, "dark" => ElementTheme.Dark, _ => ElementTheme.Default };

    // Window.SystemBackdrop follows Window.Content, which includes the outer
    // plugin-drop surface. Theme the actual window root, not only its child grid.
    private FrameworkElement ThemeRoot => Content as FrameworkElement ?? _root;

    private void ApplyTheme()
    {
        if (_uiLoaded) { RequestVisualRefresh(applyThemePreference: true); return; }
        ThemeRoot.RequestedTheme = RequestedElementTheme;
        _root.ClearValue(FrameworkElement.RequestedThemeProperty);
        ApplyBackdropPreference();
        _dark = _root.ActualTheme == ElementTheme.Dark;
        RefreshShellColors();
    }

    private void ApplyBackdropPreference()
    {
        if (_appliedBackdrop == _preferences.TranslucentBackground) return;
        SystemBackdrop = _preferences.TranslucentBackground ? new MicaBackdrop() : null;
        _appliedBackdrop = _preferences.TranslucentBackground;
    }

    private void RequestVisualRefresh(bool applyThemePreference = false)
    {
        _visualRefreshPending = true;
        _themePreferencePending |= applyThemePreference;
        if (!_uiLoaded || _hiddenToTray || _selectorOpen || _visualRefreshQueued || _closing) return;
        _visualRefreshQueued = true;
        DispatcherQueue.TryEnqueue(DispatcherQueuePriority.Low, FlushVisualRefresh);
    }

    private void FlushVisualRefresh()
    {
        _visualRefreshQueued = false;
        if (_hiddenToTray || _selectorOpen || _closing) return;
        if (_themePreferencePending)
        {
            _themePreferencePending = false;
            if (ThemeRoot.RequestedTheme != RequestedElementTheme || _root.RequestedTheme != ElementTheme.Default)
            {
                ThemeRoot.RequestedTheme = RequestedElementTheme;
                _root.ClearValue(FrameworkElement.RequestedThemeProperty);
                // Finish WinUI's theme-resource traversal before touching the page tree.
                RequestVisualRefresh();
                return;
            }
        }
        ApplyBackdropPreference();
        _dark = _root.ActualTheme == ElementTheme.Dark;
        _visualRefreshPending = false;
        RefreshShellColors();
        // Theme resources update existing settings controls without replacing them.
        if (_page == "settings" && _openPlugin is null && _openDevice is null && _settingsBody is not null)
        {
            foreach (var text in _settingsMutedViews) text.Foreground = Muted;
            return;
        }
        Render();
    }
}

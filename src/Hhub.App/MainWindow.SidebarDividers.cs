using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;

namespace Hhub.App;

public sealed partial class MainWindow
{
    private readonly SolidColorBrush _sidebarDividerBrush = new(Microsoft.UI.Colors.Transparent);
    private readonly Border _pluginRegionStartRule = SidebarRule(VerticalAlignment.Bottom);
    private readonly Border _sceneStatusBottomRule = SidebarRule(VerticalAlignment.Bottom);
    private readonly Grid _sceneStatusFrame = new() { HorizontalAlignment = HorizontalAlignment.Left };

    private static Border SidebarRule(VerticalAlignment alignment) => new()
    {
        Height = 1, Margin = new(8, 0, 8, 0), VerticalAlignment = alignment, IsHitTestVisible = false
    };

    private void AttachMainMenuDivider(NavigationViewItem item)
    {
        // Draw over the existing template's bottom edge; do not insert a menu
        // item, separator row, padding or margin that could move the plugin list.
        if (VisualTreeHelper.GetChildrenCount(item) == 0 || VisualTreeHelper.GetChild(item, 0) is not Grid root) return;
        if (root.Children.Contains(_pluginRegionStartRule)) return;
        if (_pluginRegionStartRule.Parent is Panel previous) previous.Children.Remove(_pluginRegionStartRule);
        _pluginRegionStartRule.Background = _sidebarDividerBrush;
        root.Children.Add(_pluginRegionStartRule);
    }

    private void BuildSceneStatusFrame()
    {
        _sceneStatusFrame.Children.Add(_currentSceneStatusHost);
        _sceneStatusBottomRule.Background = _sidebarDividerBrush;
        _sceneStatusFrame.Children.Add(_sceneStatusBottomRule);
        _navigationView.PaneFooter = _sceneStatusFrame;
    }

    private void UpdateSidebarDividers(double width)
    {
        _sceneStatusFrame.Width = width;
        _sidebarDividerBrush.Color = _dark ? Windows.UI.Color.FromArgb(12, 255, 255, 255)
            : Windows.UI.Color.FromArgb(10, 0, 0, 0);
    }
}

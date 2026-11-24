using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Controls.Primitives;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Media;
using System.Numerics;

namespace Hhub.Desktop.Pages;

internal static class PageLayout
{
    public const double HeadingRowHeight = 48;
    public const double NavigationRowHeight = 40;
    public const double ContentMaximumWidth = 1440;
    public const double SettingsRowHeight = 52;
    public const double SettingsRowGap = 3;
    public static Thickness Inset => new(24, 0, 24, 16);
    public static Thickness SettingsRowPadding => new(16, 8, 16, 8);

    public static double ContentWidth(double viewportWidth)
    {
        double inset = HorizontalInset(viewportWidth);
        return Math.Max(0, Math.Min(viewportWidth, ContentMaximumWidth) - 2 * inset);
    }

    public static double HorizontalInset(double viewportWidth) => Math.Min(Inset.Left, viewportWidth * 0.07);

    public static double WideInset(double viewportWidth) => viewportWidth < 900 ? 8 : viewportWidth < 1100 ? 12 : 16;

    public static void AnimateEntry(UIElement element, int direction = 0)
    {
        if (!new Windows.UI.ViewManagement.UISettings().AnimationsEnabled) return;
        var compositor = CompositionTarget.GetCompositorForCurrentThread();
        var easing = compositor.CreateCubicBezierEasingFunction(new Vector2(0.16f, 1), new Vector2(0.3f, 1));
        var fade = compositor.CreateScalarKeyFrameAnimation();
        fade.Target = "Opacity";
        fade.Duration = TimeSpan.FromMilliseconds(direction == 0 ? 150 : 200);
        fade.InsertKeyFrame(0, direction == 0 ? 0.72f : 0.5f);
        fade.InsertKeyFrame(1, 1, easing);
        var slide = compositor.CreateVector3KeyFrameAnimation();
        slide.Target = "Translation";
        slide.Duration = fade.Duration;
        slide.InsertKeyFrame(0, direction == 0 ? new Vector3(0, 7, 0) : new Vector3(direction * 20, 0, 0));
        slide.InsertKeyFrame(1, Vector3.Zero, easing);
        element.StartAnimation(fade);
        element.StartAnimation(slide);
    }

    public static Button AccentButton(string label) => new()
    {
        Content = label, Style = (Style)Application.Current.Resources["AccentButtonStyle"],
        VerticalAlignment = VerticalAlignment.Center
    };

    public static Grid Heading(string title)
    {
        var row = new Grid { Height = HeadingRowHeight };
        row.Children.Add(HeadingText(title));
        return row;
    }

    public static TextBlock HeadingText(string title) => new()
    {
        Text = title,
        VerticalAlignment = VerticalAlignment.Center,
        Style = (Style)Application.Current.Resources["PageHeadingStyle"]
    };

    public static TextBlock SecondaryHeadingText(string title) => new()
    {
        Text = title,
        Style = (Style)Application.Current.Resources["SubtitleTextBlockStyle"]
    };

    public static TextBlock SectionHeading(string title)
    {
        var heading = SecondaryHeadingText(title);
        heading.Margin = new Thickness(0, 8, 0, 0);
        return heading;
    }

    public static Border CardRow(FrameworkElement content, FrameworkElement? action)
    {
        var row = new Grid { ColumnSpacing = 12 };
        row.ColumnDefinitions.Add(new ColumnDefinition());
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        content.VerticalAlignment = VerticalAlignment.Center;
        row.Children.Add(content);
        if (action is not null)
        {
            action.VerticalAlignment = VerticalAlignment.Center;
            Grid.SetColumn(action, 1);
            row.Children.Add(action);
        }
        return new Border
        {
            Child = row,
            Style = (Style)Application.Current.Resources["DashboardCardStyle"],
            MinHeight = SettingsRowHeight,
            Padding = SettingsRowPadding
        };
    }

    public static TextBlock BreadcrumbText(string title)
    {
        var text = HeadingText(title);
        text.Style = (Style)Application.Current.Resources["PageBreadcrumbTitleStyle"];
        return text;
    }

    public static ToggleButton Tab(string title, bool compact = false)
    {
        var label = compact ? new TextBlock
        {
            Text = title,
            VerticalAlignment = VerticalAlignment.Center,
            Style = (Style)Application.Current.Resources["BodyStrongTextBlockStyle"]
        } : HeadingText(title);
        var tab = new ToggleButton
        {
            Content = label,
            Width = compact ? double.NaN : 96,
            Height = compact ? NavigationRowHeight : HeadingRowHeight,
            HorizontalContentAlignment = compact ? HorizontalAlignment.Center : HorizontalAlignment.Left,
            Style = (Style)Application.Current.Resources["PluginTabStyle"]
        };
        AutomationProperties.SetName(tab, title);
        return tab;
    }

    public static ToggleButton SecondaryTab(string title)
    {
        var tab = Tab(title);
        var heading = SecondaryHeadingText(title);
        heading.VerticalAlignment = VerticalAlignment.Center;
        tab.Content = heading;
        tab.Width = double.NaN;
        tab.MinWidth = 60;
        tab.Height = 36;
        return tab;
    }
}

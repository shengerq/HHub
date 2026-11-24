using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using Windows.Foundation;

namespace Hhub.Desktop.Pages;

internal sealed class DeviceConfigurationButtons : Panel
{
    private const int MaximumPerRow = 6;
    private const double Gap = 6;
    public bool AlignRight { get; init; }

    public Button Add(string label, Action<Button> activate)
    {
        var button = new Button
        {
            Style = (Style)Application.Current.Resources["AccentButtonStyle"],
            Content = new TextBlock { Text = label, TextTrimming = TextTrimming.CharacterEllipsis },
            MaxWidth = 180
        };
        ToolTipService.SetToolTip(button, label);
        AutomationProperties.SetName(button, label);
        button.Click += (_, _) => activate(button);
        Children.Add(button);
        return button;
    }

    protected override Size MeasureOverride(Size availableSize)
    {
        foreach (var child in Children) child.Measure(new(availableSize.Width, double.PositiveInfinity));
        return Place(availableSize.Width, arrange: false);
    }

    protected override Size ArrangeOverride(Size finalSize)
    {
        Place(finalSize.Width, arrange: true);
        return finalSize;
    }

    private Size Place(double availableWidth, bool arrange)
    {
        double x = 0, y = 0, rowHeight = 0, width = 0;
        int count = 0;
        var row = new List<(UIElement Element, double Width)>();
        foreach (var child in Children)
        {
            double childWidth = Math.Min(availableWidth, child.DesiredSize.Width);
            double childHeight = child.DesiredSize.Height;
            if (count == MaximumPerRow || count > 0 && x + Gap + childWidth > availableWidth)
            {
                PlaceRow();
                x = 0;
                y += rowHeight + Gap;
                rowHeight = 0;
                count = 0;
            }
            if (count > 0) x += Gap;
            if (arrange) row.Add((child, childWidth));
            x += childWidth;
            width = Math.Max(width, x);
            rowHeight = Math.Max(rowHeight, childHeight);
            count++;
        }
        PlaceRow();
        return new(width, y + rowHeight);

        void PlaceRow()
        {
            if (!arrange) return;
            double left = AlignRight ? Math.Max(0, availableWidth - x) : 0;
            foreach (var item in row)
            {
                item.Element.Arrange(new(left, y, item.Width, item.Element.DesiredSize.Height));
                left += item.Width + Gap;
            }
            row.Clear();
        }
    }
}

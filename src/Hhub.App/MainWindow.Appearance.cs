using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Hhub.Core;
using System.Text.Json;

namespace Hhub.App;

public sealed partial class MainWindow
{
    private readonly List<(Border Card, string? PluginId, (string Name, string Key)[] Metrics)> _summaryCards = [];

    private void UpdateSummaryCardStates()
    {
        foreach (var (card, id, metrics) in _summaryCards)
        {
            var frame = SummaryFrame(id);
            string[] missing = frame is null ? [] : metrics.Where(metric => !frame.Values.TryGetValue(metric.Key, out var value)
                || value.ValueKind is JsonValueKind.Null or JsonValueKind.Undefined).Select(metric => metric.Name).ToArray();
            string? error = id is null ? null : _runtime.Error(id);
            if (error is not null) card.BorderBrush = ErrorBrush; else card.ClearValue(Border.BorderBrushProperty);
            ToolTipService.SetToolTip(card, error ?? (missing.Length > 0 ? string.Join("、", missing) + "暂无数据" : null));
        }
    }

    private Brush PanelSurface => _dark ? ColorBrush(255, 255, 255, 15) : ColorBrush(255, 255, 255, 179);
    private Brush IconSurface => new SolidColorBrush(Microsoft.UI.Colors.Transparent);

    private void HighlightOnHover(Border card)
    {
        var normal = card.BorderBrush;
        card.PointerEntered += (_, _) => card.BorderBrush = _dark ? ColorBrush(255, 255, 255, 64) : ColorBrush(0, 0, 0, 44);
        card.PointerExited += (_, _) => card.BorderBrush = normal;
    }
}

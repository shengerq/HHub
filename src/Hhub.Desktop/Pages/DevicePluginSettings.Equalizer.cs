using System.Text.Json;
using Hhub.PluginContracts;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Controls.Primitives;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Shapes;
using Windows.Foundation;
using Windows.System;
using Windows.UI.ViewManagement;

namespace Hhub.Desktop.Pages;

internal sealed partial class DevicePluginSettings
{
    private static Border PanelCard(FrameworkElement content, Thickness padding) => new()
    {
        Child = content, Padding = padding, Style = (Style)Application.Current.Resources["DashboardCardStyle"]
    };

    private FrameworkElement InlineToolbar(Row[] rows, int[] groups)
    {
        var toolbar = new Grid { ColumnSpacing = 14, RowSpacing = 6 };
        var panels = new List<Grid>(groups.Length);
        int index = 0;
        foreach (int count in groups)
        {
            var panel = new Grid { ColumnSpacing = 8, VerticalAlignment = VerticalAlignment.Top };
            for (int column = 0; column < count; column++)
            {
                var row = rows[index++];
                var item = new StackPanel();
                var width = row.Control == "choice" && row.ControlWidth is null ? new GridLength(1, GridUnitType.Star) : GridLength.Auto;
                panel.ColumnDefinitions.Add(new ColumnDefinition { Width = width });
                AddRow(row, item, _updates, dense: true, toolbar: true);
                Grid.SetColumn(item, column);
                panel.Children.Add(item);
            }
            panels.Add(panel);
            toolbar.ColumnDefinitions.Add(new());
            toolbar.RowDefinitions.Add(new() { Height = GridLength.Auto });
            Grid.SetColumn(panel, panels.Count - 1);
            toolbar.Children.Add(panel);
        }
        if (panels.Count == 2)
        {
            var right = panels[1];
            bool flexible = right.ColumnDefinitions.Any(column => column.Width.IsStar);
            right.HorizontalAlignment = flexible ? HorizontalAlignment.Stretch : HorizontalAlignment.Right;
            toolbar.SizeChanged += (_, args) =>
            {
                bool narrow = args.NewSize.Width < 690;
                toolbar.ColumnDefinitions[1].Width = narrow || flexible ? new(1, GridUnitType.Star) : GridLength.Auto;
                Grid.SetColumnSpan(panels[0], narrow ? 2 : 1);
                Grid.SetRow(right, narrow ? 1 : 0);
                Grid.SetColumn(right, narrow ? 0 : 1);
                Grid.SetColumnSpan(right, narrow ? 2 : 1);
            };
        }
        return toolbar;
    }

    private static Brush GraphAccent(FrameworkElement surface)
    {
        var colors = new UISettings();
        var brush = new SolidColorBrush();
        void Refresh() => brush.Color = colors.GetColorValue(surface.ActualTheme == ElementTheme.Dark
            ? UIColorType.AccentLight2 : UIColorType.AccentDark1);
        surface.Loaded += (_, _) => Refresh();
        surface.ActualThemeChanged += (_, _) => Refresh();
        Refresh();
        return brush;
    }

    private FrameworkElement ResponseGraph(string source)
    {
        var plot = new Canvas { Height = 108, IsHitTestVisible = false };
        AutomationProperties.SetName(plot, "均衡器频率响应预览");
        var accent = GraphAccent(plot);
        var trace = new Polyline { Stroke = accent, StrokeThickness = 1.5 };
        var guide = new Line { Stroke = new SolidColorBrush(Microsoft.UI.Colors.Gray), Opacity = 0.28 };
        plot.Children.Add(guide);
        plot.Children.Add(trace);
        var ticks = new[] { 100, 1000, 10000 }.Select(hz => (Hz: hz, Label: new TextBlock
        {
            Text = $"{hz} Hz", FontSize = 10, Style = (Style)Application.Current.Resources["DashboardDetailStyle"]
        })).ToArray();
        foreach (var (_, label) in ticks) plot.Children.Add(label);
        JsonElement previous = default;
        double[] samples = [];
        void Draw()
        {
            double width = plot.ActualWidth;
            const double bottom = 88;
            guide.X2 = width;
            const double baseline = 79;
            guide.Y1 = guide.Y2 = baseline;
            foreach (var (hz, label) in ticks)
            {
                Canvas.SetLeft(label, Math.Max(0, width * Math.Log(hz / 20.0) / Math.Log(1000) - 16));
                Canvas.SetTop(label, bottom + 2);
            }
            trace.Points.Clear();
            if (samples.Length < 2 || width <= 0) return;
            for (int index = 0; index < samples.Length; index++)
            {
                double x = width * index / (samples.Length - 1);
                double gain = Math.Clamp(samples[index], -24, 24);
                trace.Points.Add(new Point(x, baseline - gain * (gain < 0 ? 0.35 : 3)));
            }
        }
        plot.SizeChanged += (_, _) => Draw();
        _updateResponse = () =>
        {
            var value = _values.GetValueOrDefault(source);
            if (SettingsValueEquals(previous, value)) return;
            previous = value;
            samples = value.ValueKind == JsonValueKind.Array ? PluginApi.Read<double[]>(value.GetRawText()) : [];
            Draw();
        };
        return plot;
    }

    private static ScrollViewer ScrollBandBank(Grid bank, Func<int>? count = null)
    {
        var viewport = new ScrollViewer { Content = bank, HorizontalScrollMode = ScrollMode.Enabled,
            HorizontalScrollBarVisibility = ScrollBarVisibility.Auto, VerticalScrollMode = ScrollMode.Disabled,
            VerticalScrollBarVisibility = ScrollBarVisibility.Disabled };
        viewport.SizeChanged += (_, _) => SizeBandBank(viewport, bank, count?.Invoke() ?? bank.ColumnDefinitions.Count);
        return viewport;
    }

    private static void SizeBandBank(ScrollViewer viewport, Grid bank, int count)
    {
        double width = Math.Max(count * 38, viewport.ActualWidth);
        if (bank.Width != width) bank.Width = width;
    }

    private sealed record FrequencyLimit(double Minimum, double Maximum);
    private sealed record CurveBand(Line Guide, UserControl Handle, TextBlock Gain, Slider Frequency, TextBlock FrequencyLabel);

    private (Action<JsonElement> Display, Action<bool> SetEditable) CurveEqualizer(StackPanel controls, Row row, Func<JsonElement, Task> save)
    {
        var bank = new Grid { RowSpacing = 8 };
        bank.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        bank.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        var plot = new Canvas { Height = 244, Background = new SolidColorBrush(Microsoft.UI.Colors.Transparent) };
        var frequencies = new Grid();
        bank.Children.Add(plot);
        bank.Children.Add(frequencies);
        Grid.SetRow(frequencies, 1);
        var accent = GraphAccent(plot);
        var muted = new SolidColorBrush(Microsoft.UI.Colors.Gray);
        var trace = new Polyline { Stroke = accent, StrokeThickness = 2.5, IsHitTestVisible = false };
        var fill = new Polygon { Fill = accent, Opacity = 0.10, IsHitTestVisible = false };
        var editors = new List<CurveBand>();
        var viewport = ScrollBandBank(bank, () => editors.Count);
        controls.Children.Add(viewport);
        EqualizerBand[] bands = [];
        FrequencyLimit[] limits = [];
        bool displaying = false;
        bool enabled = false;
        int dragging = -1;
        double dragGain = 0;
        double X(int index) => plot.ActualWidth * (index + 0.5) / Math.Max(1, bands.Length);
        double Y(double gain) => 17 + (plot.Height - 34) * (12 - gain) / 24;
        double Gain(int index) => dragging == index ? dragGain : bands[index].Gain;

        void Draw()
        {
            trace.Points.Clear();
            fill.Points.Clear();
            if (editors.Count == 0) return;
            fill.Points.Add(new Point(X(0), plot.Height));
            for (int i = 0; i < editors.Count; i++)
            {
                double x = X(i), y = Y(Gain(i));
                var editor = editors[i];
                trace.Points.Add(new Point(x, y));
                fill.Points.Add(new Point(x, y));
                editor.Guide.X1 = editor.Guide.X2 = x;
                editor.Guide.Y2 = plot.Height;
                Canvas.SetLeft(editor.Handle, x - editor.Handle.Width / 2);
                Canvas.SetTop(editor.Handle, y - editor.Handle.Height / 2);
                Canvas.SetLeft(editor.Gain, x - 7);
                Canvas.SetTop(editor.Gain, y - 25);
                editor.Gain.Text = $"{Gain(i):+0.#;-0.#;0}";
            }
            fill.Points.Add(new Point(X(editors.Count - 1), plot.Height));
        }

        async Task Edit(int index, double? gain = null, double? frequency = null)
        {
            if (displaying || !enabled || (uint)index >= bands.Length) return;
            var next = (EqualizerBand[])bands.Clone();
            next[index] = next[index] with { Gain = gain ?? next[index].Gain, Frequency = frequency ?? next[index].Frequency };
            await save(Element(next));
        }

        FrequencyLimit Limit(int index) => index < limits.Length && limits[index] is { Minimum: > 0 } limit && limit.Maximum > limit.Minimum
            ? limit : new(20, 20000);
        void AddBand(int index)
        {
            var guide = new Line { Stroke = muted, Opacity = 0.40, StrokeDashArray = [4, 3], IsHitTestVisible = false };
            var dot = new Ellipse { Width = 15, Height = 15, Fill = new SolidColorBrush(Microsoft.UI.Colors.Transparent),
                Stroke = accent, StrokeThickness = 3.5 };
            var hit = new Grid { Background = new SolidColorBrush(Microsoft.UI.Colors.Transparent) };
            hit.Children.Add(dot);
            var handle = new UserControl { Width = 28, Height = 28, Content = hit, IsTabStop = true };
            var label = new TextBlock { FontSize = 10, IsHitTestVisible = false };
            var frequencyText = new TextBlock { HorizontalAlignment = HorizontalAlignment.Center, FontSize = 10 };
            var frequency = new Slider { Minimum = 0, Maximum = 1000, StepFrequency = 1, SmallChange = 10,
                Orientation = Orientation.Vertical, Height = 64, MinHeight = 0, MinWidth = 0,
                HorizontalAlignment = HorizontalAlignment.Center, IsThumbToolTipEnabled = false };
            var strip = new StackPanel { Spacing = 2 };
            strip.Children.Add(frequencyText);
            strip.Children.Add(frequency);
            frequencies.ColumnDefinitions.Add(new());
            Grid.SetColumn(strip, index);
            frequencies.Children.Add(strip);
            plot.Children.Insert(0, guide);
            plot.Children.Add(label);
            plot.Children.Add(handle);
            editors.Add(new(guide, handle, label, frequency, frequencyText));
            AutomationProperties.SetName(handle, $"频段 {index + 1} 增益");
            AutomationProperties.SetName(frequency, $"频段 {index + 1} 中心频率");

            handle.PointerPressed += (_, args) =>
            {
                if (!enabled || dragging != -1 || !handle.CapturePointer(args.Pointer)) return;
                handle.Focus(FocusState.Pointer);
                dragGain = bands[index].Gain;
                dragging = index;
                args.Handled = true;
            };
            handle.PointerMoved += (_, args) =>
            {
                if (dragging != index) return;
                double y = args.GetCurrentPoint(plot).Position.Y;
                dragGain = Math.Clamp(Math.Round((12 - (y - 17) * 24 / (plot.Height - 34)) * 2) / 2, -12, 12);
                Draw();
                args.Handled = true;
            };
            handle.PointerReleased += async (_, args) =>
            {
                if (dragging != index) return;
                double value = dragGain;
                dragging = -1;
                handle.ReleasePointerCapture(args.Pointer);
                args.Handled = true;
                await Edit(index, gain: value);
                Draw();
            };
            void CancelDrag()
            {
                if (dragging != index) return;
                dragging = -1;
                Draw();
            }
            handle.PointerCanceled += (_, _) => CancelDrag();
            handle.PointerCaptureLost += (_, _) => CancelDrag();
            handle.KeyDown += async (_, args) =>
            {
                if (args.Key is not (VirtualKey.Up or VirtualKey.Down)) return;
                args.Handled = true;
                await Edit(index, gain: Math.Clamp(bands[index].Gain + (args.Key == VirtualKey.Up ? 0.5 : -0.5), -12, 12));
            };

            double pendingFrequency = 0;
            var commit = Debounce(frequency, () => Edit(index, frequency: pendingFrequency));
            frequency.ValueChanged += (_, args) =>
            {
                if (displaying || !enabled) return;
                var limit = Limit(index);
                pendingFrequency = Math.Clamp(Math.Round(limit.Minimum * Math.Pow(limit.Maximum / limit.Minimum, args.NewValue / 1000)),
                    limit.Minimum, limit.Maximum);
                frequencyText.Text = $"{pendingFrequency:0} Hz";
                commit();
            };
        }

        plot.SizeChanged += (_, _) => Draw();
        return (value =>
        {
            displaying = true;
            try
            {
                var bounds = row.LimitsFrom is { } source ? _values.GetValueOrDefault(source) : default;
                limits = bounds.ValueKind == JsonValueKind.Array ? PluginApi.Read<FrequencyLimit[]>(bounds.GetRawText()) : [];
                bands = value.ValueKind == JsonValueKind.Array ? PluginApi.Read<EqualizerBand[]>(value.GetRawText()) : [];
                if (editors.Count != bands.Length)
                {
                    dragging = -1;
                    editors.Clear();
                    plot.Children.Clear();
                    plot.Children.Add(fill);
                    plot.Children.Add(trace);
                    frequencies.Children.Clear();
                    frequencies.ColumnDefinitions.Clear();
                    for (int i = 0; i < bands.Length; i++) AddBand(i);
                    SizeBandBank(viewport, bank, bands.Length);
                }
                for (int i = 0; i < bands.Length; i++)
                {
                    var band = bands[i];
                    var limit = Limit(i);
                    double fraction = Math.Log(Math.Clamp(band.Frequency, limit.Minimum, limit.Maximum) / limit.Minimum)
                        / Math.Log(limit.Maximum / limit.Minimum);
                    editors[i].Frequency.Value = fraction * 1000;
                    editors[i].FrequencyLabel.Text = $"{band.Frequency:0} Hz";
                    ToolTipService.SetToolTip(editors[i].Handle, $"{band.Frequency:0} Hz，{band.Gain:0.#} dB");
                }
                Draw();
            }
            finally { displaying = false; }
        }, ready =>
        {
            enabled = ready;
            foreach (var editor in editors)
            {
                editor.Handle.IsEnabled = ready;
                editor.Frequency.IsEnabled = ready;
            }
        });
    }
}

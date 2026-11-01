using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Input;
using System.Windows.Media;
using BF6Optimizer.Core.Audit;
using BF6Optimizer.Core.Network;

namespace BF6Optimizer.App.Controls;

public sealed class InverseBoolToVisibility : IValueConverter
{
    public object Convert(object value, Type t, object p, CultureInfo c) => value is true ? Visibility.Collapsed : Visibility.Visible;

    public object ConvertBack(object value, Type t, object p, CultureInfo c) => throw new NotSupportedException();
}

public sealed class NullToVisibility : IValueConverter
{
    public object Convert(object? value, Type t, object p, CultureInfo c) =>
        value is null or "" ? Visibility.Collapsed : Visibility.Visible;

    public object ConvertBack(object value, Type t, object p, CultureInfo c) => throw new NotSupportedException();
}

public sealed class StatusBrushConverter : IValueConverter
{
    public object Convert(object value, Type t, object p, CultureInfo c)
    {
        var key = value switch
        {
            AuditStatus.Optimal or AuditStatus.Acceptable or LatencyQuality.Excellent or LatencyQuality.Good => "StatusGood",
            AuditStatus.OptimizationAvailable or AuditStatus.ManualVerification or LatencyQuality.Fair => "StatusMaybe",
            AuditStatus.Warning => "StatusWarn",
            AuditStatus.Critical or LatencyQuality.HighLatency => "StatusBad",
            _ => "StatusNone",
        };
        return Application.Current.FindResource(key);
    }

    public object ConvertBack(object value, Type t, object p, CultureInfo c) => throw new NotSupportedException();
}

/// <summary>Reflowing card grid: columns follow the available width and rows take their tallest card.</summary>
public sealed class ResponsiveGrid : Panel
{
    public static readonly DependencyProperty MinItemWidthProperty = DependencyProperty.Register(
        nameof(MinItemWidth), typeof(double), typeof(ResponsiveGrid),
        new FrameworkPropertyMetadata(220.0, FrameworkPropertyMetadataOptions.AffectsMeasure));

    public static readonly DependencyProperty MaxColumnsProperty = DependencyProperty.Register(
        nameof(MaxColumns), typeof(int), typeof(ResponsiveGrid),
        new FrameworkPropertyMetadata(4, FrameworkPropertyMetadataOptions.AffectsMeasure));

    public double MinItemWidth
    {
        get => (double)GetValue(MinItemWidthProperty);
        set => SetValue(MinItemWidthProperty, value);
    }

    public int MaxColumns
    {
        get => (int)GetValue(MaxColumnsProperty);
        set => SetValue(MaxColumnsProperty, value);
    }

    public double Gap { get; set; } = 8;

    private double _itemWidth;
    private int _columns = 1;
    private double[] _rowHeights = [];

    private int ColumnsFor(double width) =>
        Math.Clamp((int)((width + Gap) / (MinItemWidth + Gap)), 1, Math.Max(1, MaxColumns));

    protected override Size MeasureOverride(Size available)
    {
        double width = double.IsInfinity(available.Width) ? MinItemWidth * MaxColumns : available.Width;
        _columns = ColumnsFor(width);
        _itemWidth = (width - Gap * (_columns - 1)) / _columns;

        int rows = (int)Math.Ceiling(InternalChildren.Count / (double)_columns);
        _rowHeights = new double[rows];
        for (int i = 0; i < InternalChildren.Count; i++)
        {
            var child = InternalChildren[i];
            child.Measure(new Size(_itemWidth, double.PositiveInfinity));
            _rowHeights[i / _columns] = Math.Max(_rowHeights[i / _columns], child.DesiredSize.Height);
        }
        return new Size(width, _rowHeights.Sum() + Gap * Math.Max(0, rows - 1));
    }

    protected override Size ArrangeOverride(Size final)
    {
        double y = 0;
        for (int i = 0; i < InternalChildren.Count; i++)
        {
            int row = i / _columns, col = i % _columns;
            if (i > 0 && col == 0) y += _rowHeights[row - 1] + Gap;
            InternalChildren[i].Arrange(new Rect(col * (_itemWidth + Gap), y, _itemWidth, _rowHeights[row]));
        }
        return final;
    }
}

/// <summary>Sends wheel input from a grid inside a page to the page scroller.</summary>
public static class WheelBubble
{
    public static readonly DependencyProperty EnabledProperty = DependencyProperty.RegisterAttached(
        "Enabled", typeof(bool), typeof(WheelBubble), new PropertyMetadata(false, OnChanged));

    public static bool GetEnabled(DependencyObject d) => (bool)d.GetValue(EnabledProperty);

    public static void SetEnabled(DependencyObject d, bool value) => d.SetValue(EnabledProperty, value);

    private static void OnChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        if (d is UIElement u)
        {
            u.PreviewMouseWheel -= Forward;
            if (e.NewValue is true) u.PreviewMouseWheel += Forward;
        }
    }

    private static void Forward(object sender, MouseWheelEventArgs e)
    {
        if (e.Handled || sender is not UIElement u) return;
        e.Handled = true;
        var parent = VisualTreeHelper.GetParent(u) as UIElement;
        parent?.RaiseEvent(new MouseWheelEventArgs(e.MouseDevice, e.Timestamp, e.Delta) { RoutedEvent = UIElement.MouseWheelEvent, Source = u });
    }
}

/// <summary>
/// Line chart of measured samples only. NaN marks a missing sample and draws a red tick,
/// so a timeout is shown as a gap, never as a value.
/// </summary>
public sealed class LineChart : FrameworkElement
{
    public static readonly DependencyProperty ValuesProperty = DependencyProperty.Register(
        nameof(Values), typeof(IReadOnlyList<double>), typeof(LineChart),
        new FrameworkPropertyMetadata(null, FrameworkPropertyMetadataOptions.AffectsRender));

    public static readonly DependencyProperty MaximumProperty = DependencyProperty.Register(
        nameof(Maximum), typeof(double), typeof(LineChart),
        new FrameworkPropertyMetadata(0.0, FrameworkPropertyMetadataOptions.AffectsRender));

    public static readonly DependencyProperty StrokeProperty = DependencyProperty.Register(
        nameof(Stroke), typeof(Brush), typeof(LineChart),
        new FrameworkPropertyMetadata(Brushes.Orange, FrameworkPropertyMetadataOptions.AffectsRender));

    public IReadOnlyList<double>? Values
    {
        get => (IReadOnlyList<double>?)GetValue(ValuesProperty);
        set => SetValue(ValuesProperty, value);
    }

    /// <summary>Fixed top of the scale; 0 scales to the largest value.</summary>
    public double Maximum
    {
        get => (double)GetValue(MaximumProperty);
        set => SetValue(MaximumProperty, value);
    }

    public Brush Stroke
    {
        get => (Brush)GetValue(StrokeProperty);
        set => SetValue(StrokeProperty, value);
    }

    protected override void OnRender(DrawingContext dc)
    {
        var values = Values;
        dc.DrawRectangle(Brushes.Transparent, null, new Rect(RenderSize));
        if (values is null || values.Count < 2 || ActualWidth <= 0 || ActualHeight <= 0) return;

        var valid = values.Where(v => !double.IsNaN(v)).ToList();
        double max = Maximum > 0 ? Maximum : Math.Max(valid.Count > 0 ? valid.Max() * 1.15 : 1, 1);
        double step = ActualWidth / (values.Count - 1);
        Point At(int i) => new(i * step, ActualHeight - Math.Min(values[i] / max, 1) * ActualHeight);

        var line = new StreamGeometry();
        using (var g = line.Open())
        {
            bool open = false;
            for (int i = 0; i < values.Count; i++)
            {
                if (double.IsNaN(values[i])) { open = false; continue; }
                if (!open) { g.BeginFigure(At(i), false, false); open = true; }
                else g.LineTo(At(i), true, false);
            }
        }
        line.Freeze();
        dc.DrawGeometry(null, new Pen(Stroke, 1.5), line);

        var bad = (Brush)Application.Current.FindResource("StatusBad");
        for (int i = 0; i < values.Count; i++)
            if (double.IsNaN(values[i]))
                dc.DrawRectangle(bad, null, new Rect(i * step - 1, 0, 2, ActualHeight));
    }
}

public sealed record BarItem(string Label, double Value, string Text);

/// <summary>Horizontal bars with labels; one row per measured item, so labels cannot overlap.</summary>
public sealed class BarChart : FrameworkElement
{
    private const double RowHeight = 20;
    private const double LabelWidth = 130;
    private const double ValueWidth = 62;

    public static readonly DependencyProperty ItemsProperty = DependencyProperty.Register(
        nameof(Items), typeof(IReadOnlyList<BarItem>), typeof(BarChart),
        new FrameworkPropertyMetadata(null, FrameworkPropertyMetadataOptions.AffectsMeasure | FrameworkPropertyMetadataOptions.AffectsRender));

    public static readonly DependencyProperty FillProperty = DependencyProperty.Register(
        nameof(Fill), typeof(Brush), typeof(BarChart),
        new FrameworkPropertyMetadata(Brushes.Orange, FrameworkPropertyMetadataOptions.AffectsRender));

    public IReadOnlyList<BarItem>? Items
    {
        get => (IReadOnlyList<BarItem>?)GetValue(ItemsProperty);
        set => SetValue(ItemsProperty, value);
    }

    public Brush Fill
    {
        get => (Brush)GetValue(FillProperty);
        set => SetValue(FillProperty, value);
    }

    protected override Size MeasureOverride(Size available) =>
        new(double.IsInfinity(available.Width) ? 300 : available.Width, (Items?.Count ?? 0) * RowHeight);

    protected override void OnRender(DrawingContext dc)
    {
        var items = Items;
        if (items is null || items.Count == 0) return;
        var text = (Brush)Application.Current.FindResource("TextPrimary");
        var muted = (Brush)Application.Current.FindResource("TextMuted");
        double dpi = VisualTreeHelper.GetDpi(this).PixelsPerDip;
        double max = Math.Max(items.Max(i => i.Value), 1);
        double barArea = Math.Max(20, ActualWidth - LabelWidth - ValueWidth);
        var face = new Typeface("Segoe UI");

        FormattedText T(string s, Brush b, double width)
        {
            var f = new FormattedText(s, CultureInfo.CurrentCulture, FlowDirection.LeftToRight, face, 11.5, b, dpi)
            {
                MaxTextWidth = Math.Max(1, width),
                MaxLineCount = 1,
                Trimming = TextTrimming.CharacterEllipsis,
            };
            return f;
        }

        for (int i = 0; i < items.Count; i++)
        {
            double y = i * RowHeight;
            dc.DrawText(T(items[i].Label, muted, LabelWidth - 6), new Point(0, y + 2));
            dc.DrawRoundedRectangle(Fill, null, new Rect(LabelWidth, y + 5, Math.Max(2, barArea * items[i].Value / max), RowHeight - 10), 2, 2);
            dc.DrawText(T(items[i].Text, text, ValueWidth), new Point(ActualWidth - ValueWidth + 6, y + 2));
        }
    }
}

/// <summary>Small bundled vector flags keyed by ISO 3166 code, simplified for 20-pixel display. Unknown codes draw a globe.</summary>
public sealed class FlagIcon : FrameworkElement
{
    public static readonly DependencyProperty CodeProperty = DependencyProperty.Register(
        nameof(Code), typeof(string), typeof(FlagIcon),
        new FrameworkPropertyMetadata("", FrameworkPropertyMetadataOptions.AffectsRender));

    public string Code
    {
        get => (string)GetValue(CodeProperty);
        set => SetValue(CodeProperty, value);
    }

    public FlagIcon()
    {
        Width = 22;
        Height = 15;
    }

    private static SolidColorBrush B(string hex) => new((Color)ColorConverter.ConvertFromString(hex));

    protected override void OnRender(DrawingContext dc)
    {
        double w = ActualWidth, h = ActualHeight;
        if (w <= 0) return;
        dc.PushClip(new RectangleGeometry(new Rect(0, 0, w, h), 2, 2));

        void Fill(string c) => dc.DrawRectangle(B(c), null, new Rect(0, 0, w, h));
        void HBands(params string[] c) { for (int i = 0; i < c.Length; i++) dc.DrawRectangle(B(c[i]), null, new Rect(0, h * i / c.Length, w, h / c.Length + .5)); }
        void VBands(params string[] c) { for (int i = 0; i < c.Length; i++) dc.DrawRectangle(B(c[i]), null, new Rect(w * i / c.Length, 0, w / c.Length + .5, h)); }
        void Cross(string c, double cx, double t) { dc.DrawRectangle(B(c), null, new Rect(cx - t / 2, 0, t, h)); dc.DrawRectangle(B(c), null, new Rect(0, h / 2 - t / 2, w, t)); }
        void Disc(string c, double x, double y, double r) => dc.DrawEllipse(B(c), null, new Point(x, y), r, r);
        void Line(string c, double t, double x1, double y1, double x2, double y2) => dc.DrawLine(new Pen(B(c), t), new Point(x1, y1), new Point(x2, y2));

        switch (Code)
        {
            case "DE": HBands("#000000", "#DD0000", "#FFCE00"); break;
            case "FR": VBands("#0055A4", "#FFFFFF", "#EF4135"); break;
            case "IE": VBands("#169B62", "#FFFFFF", "#FF883E"); break;
            case "SE": Fill("#006AA7"); Cross("#FECC00", w * .36, 3); break;
            case "GB":
                Fill("#012169");
                Line("#FFFFFF", 2.6, 0, 0, w, h); Line("#FFFFFF", 2.6, w, 0, 0, h);
                Line("#C8102E", 1, 0, 0, w, h); Line("#C8102E", 1, w, 0, 0, h);
                Cross("#FFFFFF", w / 2, 4.4); Cross("#C8102E", w / 2, 2.6);
                break;
            case "US":
                HBands("#B22234", "#FFFFFF", "#B22234", "#FFFFFF", "#B22234", "#FFFFFF", "#B22234");
                dc.DrawRectangle(B("#3C3B6E"), null, new Rect(0, 0, w * .44, h * .57));
                break;
            case "BH":
                Fill("#CE1126");
                dc.DrawRectangle(B("#FFFFFF"), null, new Rect(0, 0, w * .3, h));
                break;
            case "BR":
                Fill("#009B3A");
                var d = new StreamGeometry();
                using (var g = d.Open())
                {
                    g.BeginFigure(new Point(w / 2, 1.5), true, true);
                    g.LineTo(new Point(w - 2, h / 2), false, false);
                    g.LineTo(new Point(w / 2, h - 1.5), false, false);
                    g.LineTo(new Point(2, h / 2), false, false);
                }
                dc.DrawGeometry(B("#FEDF00"), null, d);
                Disc("#002776", w / 2, h / 2, 3.2);
                break;
            case "JP": Fill("#FFFFFF"); Disc("#BC002D", w / 2, h / 2, h * .3); break;
            case "KR": Fill("#FFFFFF"); Disc("#CD2E3A", w / 2, h / 2 - 1.1, 3.4); Disc("#0047A0", w / 2, h / 2 + 1.1, 2.6); break;
            case "SG": HBands("#EF3340", "#FFFFFF"); Disc("#FFFFFF", w * .2, h * .25, 2.2); Disc("#EF3340", w * .25, h * .25, 2); break;
            case "HK": Fill("#DE2910"); Disc("#FFFFFF", w / 2, h / 2, 3.6); Disc("#DE2910", w / 2, h / 2, 1.2); break;
            case "AU":
                Fill("#012169");
                dc.DrawRectangle(B("#FFFFFF"), null, new Rect(w * .22 - 1.1, 0, 2.2, h * .5));
                dc.DrawRectangle(B("#FFFFFF"), null, new Rect(0, h * .25 - 1.1, w * .44, 2.2));
                Disc("#FFFFFF", w * .72, h * .3, 1.1); Disc("#FFFFFF", w * .82, h * .6, 1.1); Disc("#FFFFFF", w * .25, h * .75, 1.5);
                break;
            case "ZA":
                HBands("#E03C31", "#007749", "#001489");
                var t = new StreamGeometry();
                using (var g = t.Open())
                {
                    g.BeginFigure(new Point(0, 0), true, true);
                    g.LineTo(new Point(w * .4, h / 2), false, false);
                    g.LineTo(new Point(0, h), false, false);
                }
                dc.DrawGeometry(B("#000000"), new Pen(B("#FFB81C"), 1.4), t);
                break;
            default:
                dc.Pop();
                var pen = new Pen((Brush)Application.Current.FindResource("TextMuted"), 1);
                dc.DrawEllipse(null, pen, new Point(w / 2, h / 2), h / 2 - 1, h / 2 - 1);
                dc.DrawEllipse(null, pen, new Point(w / 2, h / 2), h / 4, h / 2 - 1);
                dc.DrawLine(pen, new Point(w / 2 - h / 2 + 1, h / 2), new Point(w / 2 + h / 2 - 1, h / 2));
                return;
        }
        dc.Pop();
    }
}

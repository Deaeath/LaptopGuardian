using System.Globalization;
using System.Windows;
using System.Windows.Data;
using System.Windows.Media;

namespace LaptopGuardian;

/// <summary>A small line chart with a soft fill, on a fixed scale so a steady 85°C does not look like a spike.</summary>
public sealed class Sparkline : FrameworkElement
{
    public static readonly DependencyProperty ValuesProperty = DependencyProperty.Register(
        nameof(Values), typeof(IReadOnlyList<double>), typeof(Sparkline), new FrameworkPropertyMetadata(null, FrameworkPropertyMetadataOptions.AffectsRender));
    public static readonly DependencyProperty MinimumProperty = DependencyProperty.Register(
        nameof(Minimum), typeof(double), typeof(Sparkline), new FrameworkPropertyMetadata(0.0, FrameworkPropertyMetadataOptions.AffectsRender));
    public static readonly DependencyProperty MaximumProperty = DependencyProperty.Register(
        nameof(Maximum), typeof(double), typeof(Sparkline), new FrameworkPropertyMetadata(100.0, FrameworkPropertyMetadataOptions.AffectsRender));
    public static readonly DependencyProperty StrokeProperty = DependencyProperty.Register(
        nameof(Stroke), typeof(Brush), typeof(Sparkline), new FrameworkPropertyMetadata(Brushes.SteelBlue, FrameworkPropertyMetadataOptions.AffectsRender));
    public static readonly DependencyProperty SlotsProperty = DependencyProperty.Register(
        nameof(Slots), typeof(int), typeof(Sparkline), new FrameworkPropertyMetadata(0, FrameworkPropertyMetadataOptions.AffectsRender));

    public IReadOnlyList<double>? Values { get => (IReadOnlyList<double>?)GetValue(ValuesProperty); set => SetValue(ValuesProperty, value); }
    public double Minimum { get => (double)GetValue(MinimumProperty); set => SetValue(MinimumProperty, value); }
    public double Maximum { get => (double)GetValue(MaximumProperty); set => SetValue(MaximumProperty, value); }
    public Brush Stroke { get => (Brush)GetValue(StrokeProperty); set => SetValue(StrokeProperty, value); }
    /// <summary>How many points fill the width; 0 means "however many there are".</summary>
    public int Slots { get => (int)GetValue(SlotsProperty); set => SetValue(SlotsProperty, value); }

    protected override void OnRender(DrawingContext dc)
    {
        double w = ActualWidth, h = ActualHeight;
        var v = Values;
        // A faint baseline, so an empty chart still reads as a chart.
        var baseline = Stroke.Clone(); baseline.Opacity = 0.18; baseline.Freeze();
        dc.DrawLine(new Pen(baseline, 1), new Point(0, h - 0.5), new Point(w, h - 0.5));
        if (v == null || v.Count < 2 || w <= 0 || h <= 0) return;

        int slots = Math.Max(Slots, v.Count);
        double step = w / (slots - 1);
        double x0 = w - step * (v.Count - 1);
        double range = Math.Max(1e-6, Maximum - Minimum);
        double Y(double val) => h - 2 - Math.Clamp((val - Minimum) / range, 0, 1) * (h - 4);

        var line = new StreamGeometry();
        using (var g = line.Open())
        {
            g.BeginFigure(new Point(x0, Y(v[0])), false, false);
            for (int i = 1; i < v.Count; i++) g.LineTo(new Point(x0 + i * step, Y(v[i])), true, true);
        }
        line.Freeze();

        var area = new StreamGeometry();
        using (var g = area.Open())
        {
            g.BeginFigure(new Point(x0, h), true, true);
            for (int i = 0; i < v.Count; i++) g.LineTo(new Point(x0 + i * step, Y(v[i])), true, true);
            g.LineTo(new Point(x0 + (v.Count - 1) * step, h), true, true);
        }
        area.Freeze();

        var fill = Stroke.Clone(); fill.Opacity = 0.14; fill.Freeze();
        dc.DrawGeometry(fill, null, area);
        dc.DrawGeometry(null, new Pen(Stroke, 1.6) { LineJoin = PenLineJoin.Round }, line);
    }
}

/// <summary>Visible when the bound page key equals the parameter.</summary>
public sealed class PageVisibility : IValueConverter
{
    public object Convert(object value, Type t, object parameter, CultureInfo c) =>
        string.Equals(value as string, parameter as string, StringComparison.OrdinalIgnoreCase) ? Visibility.Visible : Visibility.Collapsed;
    public object ConvertBack(object value, Type t, object parameter, CultureInfo c) => Binding.DoNothing;
}

/// <summary>Collapsed for null, empty strings, empty lists and false. ConverterParameter "inverse" flips it (for empty-state text).</summary>
public sealed class HasValue : IValueConverter
{
    public object Convert(object value, Type t, object parameter, CultureInfo c)
    {
        bool has = value switch
        {
            null => false,
            string s => !string.IsNullOrWhiteSpace(s),
            System.Collections.ICollection col => col.Count > 0,
            bool b => b,
            _ => true,
        };
        if (parameter as string == "inverse") has = !has;
        return has ? Visibility.Visible : Visibility.Collapsed;
    }
    public object ConvertBack(object value, Type t, object parameter, CultureInfo c) => Binding.DoNothing;
}

public sealed class NotVisible : IValueConverter
{
    public object Convert(object value, Type t, object parameter, CultureInfo c) => value is true ? Visibility.Collapsed : Visibility.Visible;
    public object ConvertBack(object value, Type t, object parameter, CultureInfo c) => Binding.DoNothing;
}

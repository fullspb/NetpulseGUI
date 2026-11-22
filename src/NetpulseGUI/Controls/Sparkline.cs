using System.Windows;
using System.Windows.Media;

namespace NetpulseGUI.Controls;

/// <summary>
/// Лёгкий спарклайн для строк таблицы: рисуется напрямую в OnRender, без отдельного графического движка,
/// поэтому сотни строк не нагружают CPU.
/// </summary>
public sealed class Sparkline : FrameworkElement
{
    public static readonly DependencyProperty ValuesProperty = DependencyProperty.Register(
        nameof(Values), typeof(IReadOnlyList<double>), typeof(Sparkline),
        new FrameworkPropertyMetadata(null, FrameworkPropertyMetadataOptions.AffectsRender));

    public static readonly DependencyProperty StrokeProperty = DependencyProperty.Register(
        nameof(Stroke), typeof(Brush), typeof(Sparkline),
        new FrameworkPropertyMetadata(Brushes.DodgerBlue, FrameworkPropertyMetadataOptions.AffectsRender));

    public IReadOnlyList<double>? Values
    {
        get => (IReadOnlyList<double>?)GetValue(ValuesProperty);
        set => SetValue(ValuesProperty, value);
    }

    public Brush Stroke
    {
        get => (Brush)GetValue(StrokeProperty);
        set => SetValue(StrokeProperty, value);
    }

    protected override void OnRender(DrawingContext dc)
    {
        var values = Values;
        var w = ActualWidth;
        var h = ActualHeight;
        if (values == null || values.Count < 2 || w <= 0 || h <= 2) return;

        var max = 0.0;
        foreach (var v in values) if (v > max) max = v;
        if (max <= 0)
        {
            var pen0 = new Pen(Stroke, 1) { DashStyle = DashStyles.Dot };
            pen0.Freeze();
            dc.DrawLine(pen0, new Point(0, h - 1), new Point(w, h - 1));
            return;
        }

        var geometry = new StreamGeometry();
        var fill = new StreamGeometry();
        var step = w / (values.Count - 1);
        using (var ctx = geometry.Open())
        using (var fctx = fill.Open())
        {
            fctx.BeginFigure(new Point(0, h), true, true);
            for (var i = 0; i < values.Count; i++)
            {
                var p = new Point(i * step, h - 1 - values[i] / max * (h - 2));
                if (i == 0) ctx.BeginFigure(p, false, false);
                else ctx.LineTo(p, true, false);
                fctx.LineTo(p, false, false);
            }
            fctx.LineTo(new Point(w, h), false, false);
        }
        geometry.Freeze();
        fill.Freeze();

        var fillBrush = Stroke.CloneCurrentValue();
        fillBrush.Opacity = 0.18;
        fillBrush.Freeze();
        dc.DrawGeometry(fillBrush, null, fill);

        var pen = new Pen(Stroke, 1.4) { LineJoin = PenLineJoin.Round };
        pen.Freeze();
        dc.DrawGeometry(null, pen, geometry);
    }
}

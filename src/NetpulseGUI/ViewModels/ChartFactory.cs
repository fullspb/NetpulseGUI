using System.Collections.ObjectModel;
using LiveChartsCore;
using LiveChartsCore.SkiaSharpView;
using LiveChartsCore.SkiaSharpView.Painting;
using NetpulseGUI.Helpers;
using NetpulseGUI.Services;
using SkiaSharp;

namespace NetpulseGUI.ViewModels;

/// <summary>Единое оформление графиков в цветах темы.</summary>
public static class ChartFactory
{
    public static LineSeries<double> LiveLine(ThemeService theme, ObservableCollection<double> values, bool down, string name, bool smooth = true)
    {
        var color = down ? theme.DownColor : theme.UpColor;
        return new LineSeries<double>
        {
            Name = name,
            Values = values,
            GeometrySize = 0,
            GeometryFill = null,
            GeometryStroke = null,
            LineSmoothness = smooth ? 0.6 : 0,
            Stroke = new SolidColorPaint(color) { StrokeThickness = 2 },
            Fill = new LinearGradientPaint(
                new[] { color.WithAlpha(110), color.WithAlpha(0) },
                new SKPoint(0.5f, 0), new SKPoint(0.5f, 1)),
            YToolTipLabelFormatter = p => Fmt.Speed(p.Coordinate.PrimaryValue)
        };
    }

    public static ColumnSeries<double> Columns(ThemeService theme, IReadOnlyCollection<double> values, bool down, string name, Func<double, string> format)
    {
        var color = down ? theme.DownColor : theme.UpColor;
        return new ColumnSeries<double>
        {
            Name = name,
            Values = values,
            Fill = new SolidColorPaint(color),
            Stroke = null,
            MaxBarWidth = 22,
            Padding = 2,
            Rx = 3,
            Ry = 3,
            YToolTipLabelFormatter = p => format(p.Coordinate.PrimaryValue)
        };
    }

    public static Axis[] SpeedYAxis(ThemeService theme) => new[]
    {
        new Axis
        {
            MinLimit = 0,
            Labeler = v => Fmt.Speed(v),
            LabelsPaint = new SolidColorPaint(theme.LabelColor),
            SeparatorsPaint = new SolidColorPaint(theme.SeparatorColor) { StrokeThickness = 1 },
            TextSize = 11,
            MinStep = 1
        }
    };

    public static Axis[] BytesYAxis(ThemeService theme) => new[]
    {
        new Axis
        {
            MinLimit = 0,
            Labeler = v => Fmt.Bytes(v),
            LabelsPaint = new SolidColorPaint(theme.LabelColor),
            SeparatorsPaint = new SolidColorPaint(theme.SeparatorColor) { StrokeThickness = 1 },
            TextSize = 11,
            MinStep = 1
        }
    };

    public static Axis[] HiddenXAxis() => new[]
    {
        new Axis { IsVisible = false, ShowSeparatorLines = false }
    };

    public static Axis[] LabelsXAxis(ThemeService theme, IList<string> labels) => new[]
    {
        new Axis
        {
            Labels = labels,
            LabelsPaint = new SolidColorPaint(theme.LabelColor),
            SeparatorsPaint = null,
            TextSize = 11,
            LabelsRotation = labels.Count > 16 ? -45 : 0
        }
    };
}

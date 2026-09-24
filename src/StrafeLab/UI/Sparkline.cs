using System.Windows;
using System.Windows.Media;

namespace StrafeLab.UI;

public sealed class Sparkline : FrameworkElement
{
    private IReadOnlyList<double> _values = Array.Empty<double>();

    public void SetValues(IReadOnlyList<double> values)
    {
        _values = values;
        InvalidateVisual();
    }

    protected override void OnRender(DrawingContext drawingContext)
    {
        base.OnRender(drawingContext);
        var width = ActualWidth;
        var height = ActualHeight;
        if (width <= 2 || height <= 2) return;

        drawingContext.DrawRoundedRectangle(
            new SolidColorBrush(Color.FromRgb(15, 25, 42)),
            null,
            new Rect(0, 0, width, height),
            10,
            10);

        if (_values.Count < 2) return;
        var max = Math.Max(1, _values.Max());
        var min = Math.Min(0, _values.Min());
        var range = Math.Max(1, max - min);
        var points = new PointCollection();
        for (var i = 0; i < _values.Count; i++)
        {
            var x = i * (width - 16) / Math.Max(1, _values.Count - 1) + 8;
            var y = height - 8 - ((_values[i] - min) / range * (height - 16));
            points.Add(new Point(x, y));
        }

        drawingContext.DrawGeometry(
            new SolidColorBrush(Color.FromArgb(34, 91, 214, 255)),
            null,
            BuildArea(points, height));
        drawingContext.DrawGeometry(null, new Pen(new SolidColorBrush(Color.FromRgb(71, 199, 255)), 2), BuildLine(points));
    }

    private static StreamGeometry BuildArea(PointCollection points, double height)
    {
        var geometry = new StreamGeometry();
        using var context = geometry.Open();
        context.BeginFigure(new Point(points[0].X, height), true, true);
        for (var i = 0; i < points.Count; i++) context.LineTo(points[i], true, false);
        context.LineTo(new Point(points[^1].X, height), true, false);
        return geometry;
    }

    private static StreamGeometry BuildLine(PointCollection points)
    {
        var geometry = new StreamGeometry();
        using var context = geometry.Open();
        context.BeginFigure(points[0], false, false);
        for (var i = 1; i < points.Count; i++) context.LineTo(points[i], true, false);
        return geometry;
    }
}

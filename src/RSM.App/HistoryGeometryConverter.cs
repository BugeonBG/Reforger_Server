using System.Globalization;
using System.Windows;
using System.Windows.Data;
using System.Windows.Media;
using RSM.Core;

namespace RSM.App;

/// <summary>Turns a list of 0..1 values (oldest first) into a small graph for a Path with
/// Stretch="Fill". ConverterParameter "line" draws the line, "area" the filled shape under it.
/// Newest value sits at the right edge, so a fresh graph grows in from the right.</summary>
public sealed class HistoryGeometryConverter : IValueConverter
{
    private const double W = 100, H = 40;

    public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
    {
        var values = value as IReadOnlyList<double> ?? Array.Empty<double>();
        var area = string.Equals(parameter as string, "area", StringComparison.OrdinalIgnoreCase);

        var geo = new PathGeometry();

        // An invisible diagonal pins the bounds to the full 100×40 box, so Stretch="Fill"
        // keeps 0 at the bottom and 1 at the top even when every value is the same.
        var frame = new PathFigure { StartPoint = new Point(0, 0), IsFilled = false };
        frame.Segments.Add(new LineSegment(new Point(W, H), isStroked: false));
        geo.Figures.Add(frame);

        if (values.Count >= 2)
        {
            var slots = ServerMonitorViewModel.HistoryLength - 1;
            var first = ServerMonitorViewModel.HistoryLength - values.Count;
            Point At(int i) => new(W * (first + i) / slots, H - H * Math.Clamp(values[i], 0, 1));

            var fig = new PathFigure { StartPoint = area ? new Point(At(0).X, H) : At(0), IsFilled = area, IsClosed = area };
            if (area)
                fig.Segments.Add(new LineSegment(At(0), isStroked: false));
            for (var i = 1; i < values.Count; i++)
                fig.Segments.Add(new LineSegment(At(i), isStroked: !area));
            if (area)
                fig.Segments.Add(new LineSegment(new Point(At(values.Count - 1).X, H), isStroked: false));
            geo.Figures.Add(fig);
        }

        geo.Freeze();
        return geo;
    }

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture) =>
        throw new NotSupportedException();
}

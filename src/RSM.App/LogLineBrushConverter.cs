using System.Globalization;
using System.Windows;
using System.Windows.Data;
using System.Windows.Media;
using RSM.Core;

namespace RSM.App;

/// <summary>Console line → text colour: errors red, warnings amber, milestones (server online,
/// join code, mod downloads, players, saves) green, the stock game's harmless chatter dimmed,
/// everything else the normal console colour.</summary>
public sealed class LogLineBrushConverter : IValueConverter
{
    private static readonly Brush Error = Frozen("#EE7B63");
    private static readonly Brush Warning = Frozen("#E3B04F");
    private static readonly Brush Highlight = Frozen("#9DD06F");
    private static readonly Brush Noise = Frozen("#6C7064");

    public object Convert(object value, Type targetType, object parameter, CultureInfo culture) =>
        LogLine.Classify(value as string ?? "") switch
        {
            LogKind.Error => Error,
            LogKind.Warning => Warning,
            LogKind.Highlight => Highlight,
            LogKind.Noise => Noise,
            _ => DependencyProperty.UnsetValue, // inherit the console colour
        };

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture) =>
        throw new NotSupportedException();

    private static Brush Frozen(string hex)
    {
        var b = new SolidColorBrush((Color)ColorConverter.ConvertFromString(hex));
        b.Freeze();
        return b;
    }
}

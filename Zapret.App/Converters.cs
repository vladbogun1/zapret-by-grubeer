using System.Globalization;
using System.Windows.Data;
using System.Windows.Media;

namespace Zapret.App;

/// <summary>
/// Turns a 0..1 ratio into the dash pattern that draws that fraction of a ring. Doing it with a dash array
/// keeps the progress ring a single Ellipse — no arc geometry, no custom control, no per-frame redraw.
/// Values are in units of stroke thickness, which is how WPF interprets StrokeDashArray.
/// </summary>
public sealed class RingDashConverter : IValueConverter
{
    /// <summary>Circumference divided by stroke thickness for the dashboard's 118 px ring at 9 px stroke.</summary>
    private const double Circumference = 38.0;

    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        var ratio = value is double d ? Math.Clamp(d, 0, 1) : 0;

        // A zero-length dash still paints a round cap, so an untested strategy gets an empty ring.
        return new DoubleCollection([ratio <= 0 ? 0 : ratio * Circumference, Circumference]);
    }

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        throw new NotSupportedException();
}

/// <summary>Collapses an element when the bound value is false.</summary>
public sealed class BoolToVisibilityConverter : IValueConverter
{
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        var flag = value is true;
        if (parameter is string s && s.Equals("invert", StringComparison.OrdinalIgnoreCase)) flag = !flag;

        return flag ? System.Windows.Visibility.Visible : System.Windows.Visibility.Collapsed;
    }

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        throw new NotSupportedException();
}

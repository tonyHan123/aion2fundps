using System.Globalization;
using System.Windows.Data;
using System.Windows.Media;

namespace Aion2FunDps.UI;

/// <summary>WPF value converter: "#RRGGBB" string → SolidColorBrush. Frozen for thread safety.</summary>
public sealed class HexColorToBrushConverter : IValueConverter
{
    private static readonly Dictionary<string, Brush> _cache = new();

    public object? Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        if (value is not string hex || string.IsNullOrEmpty(hex)) return Brushes.Gray;
        if (_cache.TryGetValue(hex, out var cached)) return cached;

        try
        {
            var color = (Color)ColorConverter.ConvertFromString(hex)!;
            var brush = new SolidColorBrush(color);
            brush.Freeze();
            _cache[hex] = brush;
            return brush;
        }
        catch
        {
            return Brushes.Gray;
        }
    }

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        throw new NotImplementedException();
}

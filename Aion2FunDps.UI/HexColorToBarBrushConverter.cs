// 랭크별 시각 식별용 막대 색 + 클래스 색 hex 변환기들.
using System.Globalization;
using System.Windows.Data;
using System.Windows.Media;

namespace Aion2FunDps.UI;

/// <summary>
/// Maps a 1-based rank into a distinct visually-separated color for the
/// leaderboard's per-row damage bar. We deliberately avoid class colors
/// here — many users don't memorize the class palette, and a flat eight-
/// color rank palette (designer set, max perceptual distance) gives an
/// instant "who is who" read at a glance even mid-combat.
///
/// Palette is curated for dark backgrounds; alpha 0x55 keeps the text on
/// top legible. Falls back to a neutral gray for unknown / out-of-range
/// ranks (e.g., placeholder rows).
/// </summary>
public sealed class RankToBarBrushConverter : IValueConverter
{
    private const byte BarAlpha = 0x55;
    // Designer palette — 8 hues chosen for max pairwise perceptual distance
    // on dark theme. Indexed by rank (1..8).
    private static readonly string[] Palette =
    {
        "#FFD66B", // 1  gold
        "#5BC0DE", // 2  cyan
        "#F06292", // 3  pink
        "#9CCC65", // 4  lime
        "#FF8A65", // 5  coral
        "#BA68C8", // 6  purple
        "#FFEE58", // 7  yellow
        "#90A4AE", // 8  blue-gray
    };
    private const string FallbackHex = "#90A4AE";
    private static readonly Brush[] _cache;

    static RankToBarBrushConverter()
    {
        _cache = new Brush[Palette.Length];
        for (int i = 0; i < Palette.Length; i++)
            _cache[i] = MakeBrush(Palette[i], BarAlpha);
    }

    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        int rank = value is int r ? r : 0;
        int idx = rank - 1;
        if (idx >= 0 && idx < _cache.Length) return _cache[idx];
        return MakeBrush(FallbackHex, BarAlpha);
    }

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        throw new NotImplementedException();

    internal static Brush MakeBrush(string hex, byte alpha)
    {
        try
        {
            var rgb = (Color)ColorConverter.ConvertFromString(hex)!;
            var color = Color.FromArgb(alpha, rgb.R, rgb.G, rgb.B);
            var brush = new SolidColorBrush(color);
            brush.Freeze();
            return brush;
        }
        catch
        {
            return Brushes.Transparent;
        }
    }
}

/// <summary>
/// Edge highlight (2px stripe at bar's leading edge) — same rank palette
/// at higher alpha (0xCC) so it pops against the softer bar fill.
/// </summary>
public sealed class RankToEdgeBrushConverter : IValueConverter
{
    private const byte EdgeAlpha = 0xCC;
    private static readonly string[] Palette =
    {
        "#FFD66B", "#5BC0DE", "#F06292", "#9CCC65",
        "#FF8A65", "#BA68C8", "#FFEE58", "#90A4AE",
    };
    private const string FallbackHex = "#90A4AE";
    private static readonly Brush[] _cache;

    static RankToEdgeBrushConverter()
    {
        _cache = new Brush[Palette.Length];
        for (int i = 0; i < Palette.Length; i++)
            _cache[i] = RankToBarBrushConverter.MakeBrush(Palette[i], EdgeAlpha);
    }

    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        int rank = value is int r ? r : 0;
        int idx = rank - 1;
        if (idx >= 0 && idx < _cache.Length) return _cache[idx];
        return RankToBarBrushConverter.MakeBrush(FallbackHex, EdgeAlpha);
    }

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        throw new NotImplementedException();
}

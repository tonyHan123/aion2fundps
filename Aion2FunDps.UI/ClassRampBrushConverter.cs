// 클래스색 hex → 앰비언트 글래스 행 막대용 입체(수직 명암 램프) 브러시 컨버터
using System.Globalization;
using System.Windows;
using System.Windows.Data;
using System.Windows.Media;

namespace Aion2FunDps.UI;

/// <summary>
/// "#RRGGBB" → 수직 3-stop 그라데이션 (위 +White 20% / 중앙 원색 / 아래 +Black 38%).
/// 앰비언트 글래스 행의 "유리관" 입체 막대. 결과는 hex 별로 캐시 + Freeze —
/// 500ms 마다 행이 갱신되어도 브러시 재생성 없음 (capture hot path 와 무관하지만
/// UI 틱당 allocation 은 0 이 원칙).
/// </summary>
public sealed class ClassRampBrushConverter : IValueConverter
{
    private static readonly Dictionary<string, LinearGradientBrush> Cache = new();

    public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
    {
        var hex = value as string ?? "#9AA5B2";
        if (Cache.TryGetValue(hex, out var cached)) return cached;

        var c = (Color)ColorConverter.ConvertFromString(hex);
        var brush = new LinearGradientBrush { StartPoint = new Point(0, 0), EndPoint = new Point(0, 1) };
        brush.GradientStops.Add(new GradientStop(Mix(c, Colors.White, 0.20), 0.0));
        brush.GradientStops.Add(new GradientStop(c, 0.50));
        brush.GradientStops.Add(new GradientStop(Mix(c, Colors.Black, 0.38), 1.0));
        brush.Freeze();
        Cache[hex] = brush;
        return brush;
    }

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
        => throw new NotSupportedException();

    internal static Color Mix(Color a, Color b, double t) => Color.FromRgb(
        (byte)(a.R + (b.R - a.R) * t),
        (byte)(a.G + (b.G - a.G) * t),
        (byte)(a.B + (b.B - a.B) * t));
}

/// <summary>"#RRGGBB" → 막대 끝단 브라이트 팁 (+White 45%) SolidColorBrush. hex 별 캐시.</summary>
public sealed class ClassTipBrushConverter : IValueConverter
{
    private static readonly Dictionary<string, SolidColorBrush> Cache = new();

    public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
    {
        var hex = value as string ?? "#9AA5B2";
        if (Cache.TryGetValue(hex, out var cached)) return cached;

        var c = (Color)ColorConverter.ConvertFromString(hex);
        var brush = new SolidColorBrush(ClassRampBrushConverter.Mix(c, Colors.White, 0.45));
        brush.Freeze();
        Cache[hex] = brush;
        return brush;
    }

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
        => throw new NotSupportedException();
}

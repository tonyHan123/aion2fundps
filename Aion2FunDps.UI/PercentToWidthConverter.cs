// 막대 너비 계산: percent (0-100) × 부모 ActualWidth ÷ 100 = pixel width.
using System.Globalization;
using System.Windows.Data;

namespace Aion2FunDps.UI;

/// <summary>
/// MultiBinding converter that turns a (percent, parent width) pair into a
/// pixel width for the Glass Track bars. Used by Bar A (누적 데미지 비율) and
/// Bar B (DPS 강도) so a single bar Border can fill `percent%` of its track
/// without juggling Grid star sizing.
///
/// Parameters (in order):
///   values[0] : double — percent (0..100)
///   values[1] : double — parent's ActualWidth (px)
///
/// Returns: double — clamped percent / 100 × parentWidth.
/// </summary>
public sealed class PercentToWidthConverter : IMultiValueConverter
{
    public object Convert(object[] values, Type targetType, object? parameter, CultureInfo culture)
    {
        if (values is null || values.Length < 2) return 0.0;
        double percent = values[0] is double p ? p : 0;
        double parentWidth = values[1] is double w ? w : 0;
        if (parentWidth <= 0) return 0.0;
        if (percent < 0) percent = 0;
        if (percent > 100) percent = 100;
        return percent / 100.0 * parentWidth;
    }

    public object[] ConvertBack(object value, Type[] targetTypes, object? parameter, CultureInfo culture) =>
        throw new NotImplementedException();
}

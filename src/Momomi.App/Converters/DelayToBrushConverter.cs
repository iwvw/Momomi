using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Data;
using Microsoft.UI.Xaml.Media;
using Windows.UI;

namespace Momomi.App.Converters;

/// <summary>
/// 将延迟分档（good/medium/bad/none/timeout/testing）转换为延迟文本颜色。
/// </summary>
public sealed class DelayToBrushConverter : IValueConverter
{
    private static readonly SolidColorBrush Good = new(Color.FromArgb(255, 22, 163, 74));
    private static readonly SolidColorBrush Medium = new(Color.FromArgb(255, 217, 119, 6));
    private static readonly SolidColorBrush Bad = new(Color.FromArgb(255, 220, 38, 38));
    private static readonly SolidColorBrush Idle = new(Color.FromArgb(255, 128, 128, 128));

    public object Convert(object value, Type targetType, object parameter, string language)
    {
        return (value as string) switch
        {
            "good" => Good,
            "medium" => Medium,
            "bad" => Bad,
            // 超时与连接失败都视为异常，用红色高亮。
            "timeout" => Bad,
            "none" => Bad,
            _ => Idle,
        };
    }

    public object ConvertBack(object value, Type targetType, object parameter, string language)
        => throw new NotSupportedException();
}

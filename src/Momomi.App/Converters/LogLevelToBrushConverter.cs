using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Data;
using Microsoft.UI.Xaml.Media;
using Windows.UI;

namespace Momomi.App.Converters;

/// <summary>
/// 将日志等级（debug/info/warning/error）转换为等级标签色。
/// </summary>
public sealed class LogLevelToBrushConverter : IValueConverter
{
    private static readonly SolidColorBrush Debug = new(Color.FromArgb(255, 128, 128, 128));
    private static readonly SolidColorBrush Info = new(Color.FromArgb(255, 96, 165, 250));
    private static readonly SolidColorBrush Warning = new(Color.FromArgb(255, 245, 158, 11));
    private static readonly SolidColorBrush Error = new(Color.FromArgb(255, 239, 68, 68));

    public object Convert(object value, Type targetType, object parameter, string language)
    {
        return (value as string)?.ToLowerInvariant() switch
        {
            "warning" or "warn" => Warning,
            "error" => Error,
            "info" => Info,
            _ => Debug,
        };
    }

    public object ConvertBack(object value, Type targetType, object parameter, string language)
        => throw new NotSupportedException();
}

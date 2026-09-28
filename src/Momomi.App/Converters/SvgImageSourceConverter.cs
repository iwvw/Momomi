using System;
using Microsoft.UI.Xaml.Data;
using Microsoft.UI.Xaml.Media.Imaging;

namespace Momomi.App.Converters;

/// <summary>
/// 把 ms-appx SVG 资源路径转成 SvgImageSource，供 Image 显示国旗。
/// Image.Source 直接绑 SVG 会用 BitmapImage 解码导致崩溃，必须走本转换器。
/// </summary>
public sealed class SvgImageSourceConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, string language)
    {
        if (value is not string uri || string.IsNullOrWhiteSpace(uri)) return null!;
        try
        {
            if (uri.EndsWith(".svg", StringComparison.OrdinalIgnoreCase))
                return new SvgImageSource(new Uri(uri));
            return new BitmapImage(new Uri(uri));
        }
        catch
        {
            return null!;
        }
    }

    public object ConvertBack(object value, Type targetType, object parameter, string language)
        => throw new NotSupportedException();
}

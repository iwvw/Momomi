using System;
using System.Collections.Generic;
using Microsoft.UI.Xaml.Data;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Imaging;

namespace Momomi.App.Converters;

/// <summary>
/// 把 ms-appx SVG 资源路径转成 SvgImageSource，供 Image 显示国旗。
/// Image.Source 直接绑 SVG 会用 BitmapImage 解码导致崩溃，必须走本转换器。
/// 按 URI 缓存：国旗种类有限（天然有界），避免每次刷新都新建原生 SVG 资源造成私有内存增长。
/// </summary>
public sealed class SvgImageSourceConverter : IValueConverter
{
    private static readonly Dictionary<string, ImageSource> Cache = new(StringComparer.OrdinalIgnoreCase);
    private static readonly object Gate = new();

    /// <summary>
    /// 清空缓存。主窗口收进托盘时调用：国旗图片是原生 SVG 资源，
    /// 隐藏后长期不用，继续持有只会让私有内存基准抬高；
    /// 再次显示时重新解码即可（数量有限，开销很小）。
    /// </summary>
    public static void ClearCache()
    {
        lock (Gate) Cache.Clear();
    }
    public object Convert(object value, Type targetType, object parameter, string language)
    {
        if (value is not string uri || string.IsNullOrWhiteSpace(uri)) return null!;

        lock (Gate)
        {
            if (Cache.TryGetValue(uri, out var cached)) return cached;
        }

        ImageSource source;
        try
        {
            source = uri.EndsWith(".svg", StringComparison.OrdinalIgnoreCase)
                ? new SvgImageSource(new Uri(uri))
                : new BitmapImage(new Uri(uri));
        }
        catch
        {
            return null!;
        }

        lock (Gate)
        {
            Cache[uri] = source;
        }
        return source;
    }

    public object ConvertBack(object value, Type targetType, object parameter, string language)
        => throw new NotSupportedException();
}

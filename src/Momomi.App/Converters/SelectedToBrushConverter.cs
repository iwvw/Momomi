using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Data;
using Microsoft.UI.Xaml.Media;
using Windows.UI;

namespace Momomi.App.Converters;

/// <summary>
/// 节点选中态背景：选中时用半透明强调色，未选中透明。
/// </summary>
public sealed class SelectedToBrushConverter : IValueConverter
{
    private static SolidColorBrush? _selected;

    public object Convert(object value, Type targetType, object parameter, string language)
    {
        if (value is not bool selected || !selected) return Transparent();

        if (_selected is null)
        {
            _selected = new SolidColorBrush(Color.FromArgb(48, 0, 120, 212));
            try
            {
                if (Application.Current?.Resources is { } res
                    && res.TryGetValue("SystemAccentColor", out var accent)
                    && accent is Color color)
                {
                    _selected = new SolidColorBrush(Color.FromArgb(48, color.R, color.G, color.B));
                }
            }
            catch
            {
            }
        }

        return _selected;
    }

    public object ConvertBack(object value, Type targetType, object parameter, string language)
        => throw new NotSupportedException();

    internal static SolidColorBrush Transparent() => new(Color.FromArgb(0, 0, 0, 0));
}

/// <summary>
/// 节点选中态描边：选中时用强调色，未选中透明（由外层 ThemeResource 提供默认描边）。
/// </summary>
public sealed class SelectedBorderBrushConverter : IValueConverter
{
    private static SolidColorBrush? _selected;

    public object Convert(object value, Type targetType, object parameter, string language)
    {
        if (value is not bool selected || !selected)
            return SelectedToBrushConverter.Transparent();

        if (_selected is null)
        {
            _selected = new SolidColorBrush(Color.FromArgb(255, 0, 120, 212));
            try
            {
                if (Application.Current?.Resources is { } res
                    && res.TryGetValue("SystemAccentColor", out var accent)
                    && accent is Color color)
                {
                    _selected = new SolidColorBrush(color);
                }
            }
            catch
            {
            }
        }

        return _selected;
    }

    public object ConvertBack(object value, Type targetType, object parameter, string language)
        => throw new NotSupportedException();
}

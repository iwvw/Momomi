using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Data;

namespace Momomi.App.Converters;

public sealed class EmptyToVisibilityConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, string language)
    {
        var count = value switch
        {
            int i => i,
            long l => (int)l,
            System.Collections.ICollection collection => collection.Count,
            _ => 0,
        };
        return count <= 0 ? Visibility.Visible : Visibility.Collapsed;
    }

    public object ConvertBack(object value, Type targetType, object parameter, string language)
        => throw new NotSupportedException();
}

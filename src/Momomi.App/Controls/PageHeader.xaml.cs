using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace Momomi.App.Controls;

public sealed partial class PageHeader : UserControl
{
    public static readonly DependencyProperty TitleProperty = DependencyProperty.Register(
        nameof(Title), typeof(string), typeof(PageHeader),
        new PropertyMetadata("", OnTitleChanged));

    public static readonly DependencyProperty SubtitleProperty = DependencyProperty.Register(
        nameof(Subtitle), typeof(string), typeof(PageHeader),
        new PropertyMetadata(null, OnSubtitleChanged));

    public static readonly DependencyProperty SubtitleContentProperty = DependencyProperty.Register(
        nameof(SubtitleContent), typeof(object), typeof(PageHeader),
        new PropertyMetadata(null, OnSubtitleContentChanged));

    public static readonly DependencyProperty ActionsProperty = DependencyProperty.Register(
        nameof(Actions), typeof(object), typeof(PageHeader),
        new PropertyMetadata(null, OnActionsChanged));

    public string Title
    {
        get => (string)GetValue(TitleProperty);
        set => SetValue(TitleProperty, value);
    }

    public string? Subtitle
    {
        get => (string?)GetValue(SubtitleProperty);
        set => SetValue(SubtitleProperty, value);
    }

    public object? SubtitleContent
    {
        get => GetValue(SubtitleContentProperty);
        set => SetValue(SubtitleContentProperty, value);
    }

    public object? Actions
    {
        get => GetValue(ActionsProperty);
        set => SetValue(ActionsProperty, value);
    }

    public PageHeader()
    {
        InitializeComponent();
    }

    private static void OnTitleChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
        => ((PageHeader)d).TitleText.Text = e.NewValue as string ?? "";

    private static void OnSubtitleChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        var header = (PageHeader)d;
        if (header.SubtitleContent is not null) return;

        var text = e.NewValue as string;
        header.SubtitleText.Text = text ?? "";
        header.SubtitleText.Visibility = string.IsNullOrEmpty(text)
            ? Visibility.Collapsed
            : Visibility.Visible;
    }

    private static void OnSubtitleContentChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        var header = (PageHeader)d;
        if (e.NewValue is null)
        {
            header.SubtitleHost.Content = null;
            header.SubtitleHost.Visibility = Visibility.Collapsed;

            var text = header.Subtitle ?? "";
            header.SubtitleText.Text = text;
            header.SubtitleText.Visibility = string.IsNullOrEmpty(text)
                ? Visibility.Collapsed
                : Visibility.Visible;
            return;
        }

        header.SubtitleText.Text = "";
        header.SubtitleText.Visibility = Visibility.Collapsed;
        header.SubtitleHost.Content = e.NewValue;
        header.SubtitleHost.Visibility = Visibility.Visible;
    }

    private static void OnActionsChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
        => ((PageHeader)d).ActionsHost.Content = e.NewValue;
}

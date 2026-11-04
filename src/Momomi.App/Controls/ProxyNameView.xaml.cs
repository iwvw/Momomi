using Microsoft.UI.Text;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Momomi.App.Converters;
using Momomi.App.ViewModels;
using Windows.Foundation;
using Windows.UI.Text;

namespace Momomi.App.Controls;

/// <summary>
/// 统一的代理组/节点名称显示：前导旗帜用内置 SVG，其他 emoji 用专用 emoji 字体，
/// 名称文本单独排布，避免字体回退链把三种元素混在一条文本里。
/// </summary>
public sealed partial class ProxyNameView : UserControl
{
    private static readonly SvgImageSourceConverter SvgConverter = new();
    private static readonly FontFamily SegoeEmoji = new("Segoe UI Emoji");
    private static readonly FontFamily FlagEmoji =
        new("ms-appx:///Assets/FlagsEmoji.ttf#Twemoji Mozilla, Segoe UI Emoji");

    public static readonly DependencyProperty NameTextProperty = DependencyProperty.Register(
        nameof(NameText), typeof(string), typeof(ProxyNameView),
        new PropertyMetadata("", OnVisualChanged));

    public static readonly DependencyProperty TextSizeProperty = DependencyProperty.Register(
        nameof(TextSize), typeof(double), typeof(ProxyNameView),
        new PropertyMetadata(14d, OnVisualChanged));

    public static readonly DependencyProperty IsSemiBoldProperty = DependencyProperty.Register(
        nameof(IsSemiBold), typeof(bool), typeof(ProxyNameView),
        new PropertyMetadata(false, OnVisualChanged));

    public static readonly DependencyProperty TextForegroundProperty = DependencyProperty.Register(
        nameof(TextForeground), typeof(Brush), typeof(ProxyNameView),
        new PropertyMetadata(null, OnVisualChanged));

    public static readonly DependencyProperty ChainModeProperty = DependencyProperty.Register(
        nameof(ChainMode), typeof(bool), typeof(ProxyNameView),
        new PropertyMetadata(false, OnVisualChanged));

    public string NameText
    {
        get => (string)GetValue(NameTextProperty);
        set => SetValue(NameTextProperty, value);
    }

    public double TextSize
    {
        get => (double)GetValue(TextSizeProperty);
        set => SetValue(TextSizeProperty, value);
    }

    public bool IsSemiBold
    {
        get => (bool)GetValue(IsSemiBoldProperty);
        set => SetValue(IsSemiBoldProperty, value);
    }

    public Brush? TextForeground
    {
        get => (Brush?)GetValue(TextForegroundProperty);
        set => SetValue(TextForegroundProperty, value);
    }

    public bool ChainMode
    {
        get => (bool)GetValue(ChainModeProperty);
        set => SetValue(ChainModeProperty, value);
    }

    public ProxyNameView()
    {
        InitializeComponent();
        Loaded += (_, _) => Rebuild();
        Root.SizeChanged += (_, e) =>
            Root.Clip = new RectangleGeometry { Rect = new Rect(0, 0, e.NewSize.Width, e.NewSize.Height) };
    }

    private static void OnVisualChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
        => ((ProxyNameView)d).Rebuild();

    private void Rebuild()
    {
        if (Root is null) return;

        Root.Children.Clear();
        Root.ColumnDefinitions.Clear();
        Root.RowDefinitions.Clear();
        Root.Clip = null;

        var name = NameText;
        if (string.IsNullOrEmpty(name)) return;

        if (ChainMode) BuildChain(name);
        else BuildSingle(name);
    }

    private void BuildSingle(string name)
    {
        var info = ProxyNameFormat.Parse(name);

        Root.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        Root.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });

        var icon = CreateIcon(info);
        var text = CreateText(info.Text, trim: true);

        if (icon is not null)
        {
            Grid.SetColumn(icon, 0);
            Root.Children.Add(icon);
            Grid.SetColumn(text, 1);
        }
        else
        {
            Grid.SetColumn(text, 0);
            Grid.SetColumnSpan(text, 2);
        }

        Root.Children.Add(text);
    }

    private void BuildChain(string chain)
    {
        var panel = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            VerticalAlignment = VerticalAlignment.Center,
        };

        var parts = ProxyNameFormat.ParseChain(chain);
        for (var i = 0; i < parts.Count; i++)
        {
            if (i > 0)
            {
                panel.Children.Add(new TextBlock
                {
                    Text = " → ",
                    FontSize = TextSize,
                    Foreground = SecondaryBrush(),
                    VerticalAlignment = VerticalAlignment.Center,
                });
            }

            var icon = CreateIcon(parts[i]);
            if (icon is not null) panel.Children.Add(icon);
            panel.Children.Add(CreateText(parts[i].Text, trim: false));
        }

        Root.Children.Add(panel);
    }

    private FrameworkElement? CreateIcon(ProxyNameIcon info)
    {
        var spacing = Math.Max(4, Math.Round(TextSize * 0.4));
        var height = Math.Round(TextSize + 1);

        if (info.IsFlag && ProxyNameFormat.HasFlagAsset(info.FlagCode))
        {
            var width = Math.Round(height * 4 / 3);
            var uri = CountryNames.GetFlag(info.FlagCode);
            if (SvgConverter.Convert(uri, typeof(ImageSource), null!, "") is ImageSource source)
            {
                return new Image
                {
                    Source = source,
                    Width = width,
                    Height = height,
                    Stretch = Stretch.Uniform,
                    VerticalAlignment = VerticalAlignment.Center,
                    Margin = new Thickness(0, 0, spacing, 0),
                };
            }
        }

        if (string.IsNullOrEmpty(info.Emoji)) return null;

        return new TextBlock
        {
            Text = info.Emoji,
            FontFamily = info.IsFlag ? FlagEmoji : SegoeEmoji,
            FontSize = Math.Round(TextSize + 1),
            VerticalAlignment = VerticalAlignment.Center,
            Margin = new Thickness(0, 0, spacing, 0),
        };
    }

    private TextBlock CreateText(string text, bool trim)
    {
        var block = new TextBlock
        {
            Text = text,
            FontSize = TextSize,
            FontWeight = IsSemiBold ? FontWeights.SemiBold : FontWeights.Normal,
            VerticalAlignment = VerticalAlignment.Center,
            TextWrapping = TextWrapping.NoWrap,
            TextTrimming = trim ? TextTrimming.CharacterEllipsis : TextTrimming.None,
        };

        if (TextForeground is not null) block.Foreground = TextForeground;
        return block;
    }

    private static Brush? SecondaryBrush()
    {
        try
        {
            return Application.Current.Resources["TextFillColorSecondaryBrush"] as Brush;
        }
        catch
        {
            return null;
        }
    }
}

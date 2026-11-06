using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Shapes;
using Momomi.App.ViewModels;
using Windows.Foundation;

namespace Momomi.App.Controls;

public sealed partial class SparklineChart : UserControl
{
    public static readonly DependencyProperty ValuesProperty = DependencyProperty.Register(
        nameof(Values),
        typeof(IReadOnlyList<double>),
        typeof(SparklineChart),
        new PropertyMetadata(null, OnValuesChanged));

    public static readonly DependencyProperty LabelsProperty = DependencyProperty.Register(
        nameof(Labels),
        typeof(IReadOnlyList<string>),
        typeof(SparklineChart),
        new PropertyMetadata(null, OnValuesChanged));

    public static readonly DependencyProperty StrokeProperty = DependencyProperty.Register(
        nameof(Stroke),
        typeof(Brush),
        typeof(SparklineChart),
        new PropertyMetadata(new SolidColorBrush(Microsoft.UI.Colors.DodgerBlue), OnValuesChanged));

    public static readonly DependencyProperty FillProperty = DependencyProperty.Register(
        nameof(Fill),
        typeof(Brush),
        typeof(SparklineChart),
        new PropertyMetadata(null, OnValuesChanged));

    public static readonly DependencyProperty MaximumProperty = DependencyProperty.Register(
        nameof(Maximum),
        typeof(double),
        typeof(SparklineChart),
        new PropertyMetadata(0d, OnValuesChanged));

    public static readonly DependencyProperty AxisFormatProperty = DependencyProperty.Register(
        nameof(AxisFormat),
        typeof(string),
        typeof(SparklineChart),
        new PropertyMetadata("", OnValuesChanged));

    public IReadOnlyList<double>? Values
    {
        get => (IReadOnlyList<double>?)GetValue(ValuesProperty);
        set => SetValue(ValuesProperty, value);
    }

    public IReadOnlyList<string>? Labels
    {
        get => (IReadOnlyList<string>?)GetValue(LabelsProperty);
        set => SetValue(LabelsProperty, value);
    }

    public Brush Stroke
    {
        get => (Brush)GetValue(StrokeProperty);
        set => SetValue(StrokeProperty, value);
    }

    public Brush? Fill
    {
        get => (Brush?)GetValue(FillProperty);
        set => SetValue(FillProperty, value);
    }

    public double Maximum
    {
        get => (double)GetValue(MaximumProperty);
        set => SetValue(MaximumProperty, value);
    }

    public string AxisFormat
    {
        get => (string)GetValue(AxisFormatProperty);
        set => SetValue(AxisFormatProperty, value);
    }

    private const double PadTop = 8;
    private const double PadBottom = 6;
    private const double PadRight = 8;
    private const double AxisGap = 8;
    private const int AxisSplits = 4;

    private readonly Canvas _plot;
    private readonly Grid _root;
    private readonly Grid _xAxis;
    private readonly TextBlock _xStart = new() { FontSize = 11 };
    private readonly TextBlock _xMid = new() { FontSize = 11, HorizontalAlignment = HorizontalAlignment.Center };
    private readonly TextBlock _xEnd = new() { FontSize = 11, HorizontalAlignment = HorizontalAlignment.Right };

    private readonly Polyline _line;
    private readonly Polygon _area;
    private readonly Line _cursorLine;
    private readonly Ellipse _cursorDot;
    private readonly Border _tooltip;
    private readonly TextBlock _tipLabel = new() { FontSize = 11 };
    private readonly TextBlock _tipValue = new() { FontSize = 12, FontWeight = Microsoft.UI.Text.FontWeights.SemiBold };

    private IReadOnlyList<double> _current = Array.Empty<double>();
    private double _niceMax = 1;
    private double _plotLeft;
    private double _plotRight;

    public SparklineChart()
    {
        _plot = new Canvas { Background = new SolidColorBrush(Microsoft.UI.Colors.Transparent) };
        _area = new Polygon { Visibility = Visibility.Collapsed };
        _line = new Polyline
        {
            Stroke = new SolidColorBrush(Microsoft.UI.Colors.DodgerBlue),
            StrokeThickness = 1.6,
            StrokeLineJoin = PenLineJoin.Round,
        };
        _cursorLine = new Line
        {
            StrokeThickness = 1,
            Visibility = Visibility.Collapsed,
            IsHitTestVisible = false,
        };
        _cursorDot = new Ellipse
        {
            Width = 9,
            Height = 9,
            StrokeThickness = 2,
            Visibility = Visibility.Collapsed,
            IsHitTestVisible = false,
        };
        _tooltip = new Border
        {
            CornerRadius = new CornerRadius(6),
            BorderThickness = new Thickness(1),
            Padding = new Thickness(8, 5, 8, 5),
            Visibility = Visibility.Collapsed,
            IsHitTestVisible = false,
            Child = new StackPanel { Spacing = 1, Children = { _tipLabel, _tipValue } },
        };

        _xAxis = new Grid { Margin = new Thickness(0, 4, 0, 0) };
        _xAxis.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        _xAxis.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        _xAxis.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        Grid.SetColumn(_xStart, 0);
        Grid.SetColumn(_xMid, 1);
        Grid.SetColumn(_xEnd, 2);
        _xAxis.Children.Add(_xStart);
        _xAxis.Children.Add(_xMid);
        _xAxis.Children.Add(_xEnd);

        _root = new Grid();
        _root.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
        _root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        Grid.SetRow(_plot, 0);
        Grid.SetRow(_xAxis, 1);
        _root.Children.Add(_plot);
        _root.Children.Add(_xAxis);

        Content = _root;
        SizeChanged += (_, _) => Redraw();
        Loaded += (_, _) => Redraw();
        ActualThemeChanged += (_, _) => Redraw();
        _plot.PointerMoved += Plot_PointerMoved;
        _plot.PointerExited += (_, _) => HideCursor();
        _plot.PointerCaptureLost += (_, _) => HideCursor();
    }

    private static void OnValuesChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
        => (d as SparklineChart)?.Redraw();

    private void Redraw()
    {
        var w = _plot.ActualWidth;
        var h = _plot.ActualHeight;
        if (w <= 1 || h <= 1) return;

        _current = Values ?? Array.Empty<double>();

        var axisBrush = ThemeBrush("TextFillColorTertiaryBrush", Microsoft.UI.Colors.Gray);
        var gridBrush = new SolidColorBrush(Microsoft.UI.ColorHelper.FromArgb(0x24, 0x80, 0x80, 0x80));

        _plot.Children.Clear();
        _plot.Children.Add(_area);
        _plot.Children.Add(_line);
        _plot.Children.Add(_cursorLine);
        _plot.Children.Add(_cursorDot);
        _plot.Children.Add(_tooltip);

        _line.Stroke = Stroke;
        if (Fill is not null) _area.Fill = Fill;
        _cursorLine.Stroke = axisBrush;
        _cursorDot.Stroke = Stroke;
        _cursorDot.Fill = ThemeBrush("SolidBackgroundFillColorBaseBrush", Microsoft.UI.Colors.White);
        _tooltip.Background = ThemeBrush("SolidBackgroundFillColorBaseBrush", Microsoft.UI.Colors.Black);
        _tooltip.BorderBrush = ThemeBrush("CardStrokeColorDefaultBrush", Microsoft.UI.Colors.Gray);
        _tipLabel.Foreground = ThemeBrush("TextFillColorSecondaryBrush", Microsoft.UI.Colors.Gray);
        _tipValue.Foreground = ThemeBrush("TextFillColorPrimaryBrush", Microsoft.UI.Colors.White);

        _xStart.Foreground = axisBrush;
        _xMid.Foreground = axisBrush;
        _xEnd.Foreground = axisBrush;

        if (_current.Count == 0)
        {
            _line.Points.Clear();
            _area.Points.Clear();
            _area.Visibility = Visibility.Collapsed;
            _xStart.Text = "";
            _xMid.Text = "";
            _xEnd.Text = "";
            HideCursor();
            return;
        }

        var rawMax = Maximum > 0 ? Maximum : _current.Max();
        _niceMax = NiceCeiling(rawMax);
        if (_niceMax <= 0) _niceMax = 1;

        var gutter = DrawAxis(w, h, axisBrush, gridBrush);
        _plotLeft = gutter;
        _plotRight = w - PadRight;
        DrawSeries(h);
        _xAxis.Margin = new Thickness(gutter, 4, PadRight, 0);

        var labels = Labels;
        if (labels is { Count: > 0 })
        {
            _xStart.Text = labels[0];
            _xMid.Text = labels[labels.Count / 2];
            _xEnd.Text = labels[^1];
        }
        else
        {
            _xStart.Text = "";
            _xMid.Text = "";
            _xEnd.Text = "";
        }
    }

    private double DrawAxis(double w, double h, Brush axisBrush, Brush gridBrush)
    {
        var plotHeight = h - PadTop - PadBottom;

        var texts = new List<TextBlock>(AxisSplits + 1);
        var gutter = 0d;
        for (var i = 0; i <= AxisSplits; i++)
        {
            var text = new TextBlock
            {
                Text = FormatValue(_niceMax * (1 - i / (double)AxisSplits)),
                FontSize = 11,
                Foreground = axisBrush,
                Opacity = 0.85,
            };
            text.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));
            gutter = Math.Max(gutter, text.DesiredSize.Width);
            texts.Add(text);
        }
        gutter = Math.Ceiling(gutter) + AxisGap;

        for (var i = 0; i <= AxisSplits; i++)
        {
            var frac = i / (double)AxisSplits;
            var y = PadTop + plotHeight * frac;

            var grid = new Line
            {
                X1 = gutter,
                X2 = w,
                Y1 = y,
                Y2 = y,
                Stroke = gridBrush,
                StrokeThickness = 1,
                StrokeDashArray = new DoubleCollection { 3, 3 },
                IsHitTestVisible = false,
            };
            _plot.Children.Insert(_plot.Children.Count - 5, grid);

            var text = texts[i];
            Canvas.SetLeft(text, 0);
            Canvas.SetTop(text, Math.Max(0, Math.Min(h - text.DesiredSize.Height, y - text.DesiredSize.Height / 2)));
            _plot.Children.Insert(_plot.Children.Count - 5, text);
        }

        return gutter;
    }

    private void DrawSeries(double h)
    {
        var count = _current.Count;
        var plotWidth = Math.Max(0, _plotRight - _plotLeft);
        var plotHeight = h - PadTop - PadBottom;
        var stepX = count > 1 ? plotWidth / (count - 1) : 0;

        _line.Points.Clear();
        _area.Points.Clear();

        for (var i = 0; i < count; i++)
        {
            var x = _plotLeft + i * stepX;
            var normalized = Math.Clamp(_current[i] / _niceMax, 0, 1);
            var y = h - PadBottom - normalized * plotHeight;
            _line.Points.Add(new Point(x, y));
            _area.Points.Add(new Point(x, y));
        }

        if (count > 1)
        {
            _area.Points.Add(new Point(_plotRight, h - PadBottom));
            _area.Points.Add(new Point(_plotLeft, h - PadBottom));
            _area.Visibility = Visibility.Visible;
        }
        else
        {
            _area.Visibility = Visibility.Collapsed;
        }
    }

    private void Plot_PointerMoved(object sender, Microsoft.UI.Xaml.Input.PointerRoutedEventArgs e)
    {
        var values = _current;
        var count = values.Count;
        if (count == 0) return;

        var w = _plot.ActualWidth;
        var h = _plot.ActualHeight;
        if (w <= 1 || h <= 1) return;

        var p = e.GetCurrentPoint(_plot).Position;
        if (p.X < _plotLeft) { HideCursor(); return; }

        var plotWidth = Math.Max(1, _plotRight - _plotLeft);
        var plotHeight = h - PadTop - PadBottom;
        var stepX = count > 1 ? plotWidth / (count - 1) : 0;

        var index = stepX > 0 ? (int)Math.Round((p.X - _plotLeft) / stepX) : 0;
        index = Math.Clamp(index, 0, count - 1);

        var x = _plotLeft + index * stepX;
        var normalized = Math.Clamp(values[index] / _niceMax, 0, 1);
        var y = h - PadBottom - normalized * plotHeight;

        _cursorLine.X1 = x;
        _cursorLine.X2 = x;
        _cursorLine.Y1 = PadTop;
        _cursorLine.Y2 = h - PadBottom;
        _cursorLine.Visibility = Visibility.Visible;

        Canvas.SetLeft(_cursorDot, x - _cursorDot.Width / 2);
        Canvas.SetTop(_cursorDot, y - _cursorDot.Height / 2);
        _cursorDot.Visibility = Visibility.Visible;

        var labels = Labels;
        _tipLabel.Text = labels is not null && index < labels.Count ? labels[index] : $"{index + 1}";
        _tipValue.Text = FormatValue(values[index]);

        _tooltip.Visibility = Visibility.Visible;
        _tooltip.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));
        var tw = _tooltip.DesiredSize.Width;
        var th = _tooltip.DesiredSize.Height;

        var left = x + 12;
        if (left + tw > w - 2) left = x - 12 - tw;
        if (left < 2) left = 2;

        var top = y - th / 2;
        top = Math.Max(2, Math.Min(top, h - th - 2));

        Canvas.SetLeft(_tooltip, left);
        Canvas.SetTop(_tooltip, top);
    }

    private void HideCursor()
    {
        _cursorLine.Visibility = Visibility.Collapsed;
        _cursorDot.Visibility = Visibility.Collapsed;
        _tooltip.Visibility = Visibility.Collapsed;
    }

    private string FormatValue(double value) => AxisFormat switch
    {
        "rate" => Format.Rate((long)Math.Round(value)),
        "mb" => Format.RateMb(value),
        "bytes" => Format.Bytes((long)Math.Round(value)),
        _ => value >= 100 ? value.ToString("0") : value.ToString("0.##"),
    };

    private static double NiceCeiling(double value)
    {
        if (value <= 0) return 1;
        var exponent = Math.Pow(10, Math.Floor(Math.Log10(value)));
        var fraction = value / exponent;
        var nice = fraction switch
        {
            <= 1 => 1,
            <= 2 => 2,
            <= 2.5 => 2.5,
            <= 5 => 5,
            _ => 10,
        };
        return nice * exponent;
    }

    private static Brush ThemeBrush(string key, Windows.UI.Color fallback)
    {
        if (Application.Current.Resources.TryGetValue(key, out var value) && value is Brush brush)
            return brush;
        return new SolidColorBrush(fallback);
    }
}

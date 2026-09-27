using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Shapes;
using Windows.Foundation;

namespace Momomi.App.Controls;

public sealed partial class SparklineChart : UserControl
{
    public static readonly DependencyProperty ValuesProperty = DependencyProperty.Register(
        nameof(Values),
        typeof(IReadOnlyList<double>),
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

    public IReadOnlyList<double>? Values
    {
        get => (IReadOnlyList<double>?)GetValue(ValuesProperty);
        set => SetValue(ValuesProperty, value);
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

    private readonly Canvas _canvas;
    private readonly Polyline _line;
    private readonly Polygon _area;

    public SparklineChart()
    {
        _canvas = new Canvas { Background = null };
        _area = new Polygon
        {
            Fill = new SolidColorBrush(Microsoft.UI.ColorHelper.FromArgb(40, 59, 130, 246)),
            Visibility = Visibility.Collapsed,
        };
        _line = new Polyline
        {
            Stroke = new SolidColorBrush(Microsoft.UI.Colors.DodgerBlue),
            StrokeThickness = 1.6,
            StrokeLineJoin = PenLineJoin.Round,
        };
        _canvas.Children.Add(_area);
        _canvas.Children.Add(_line);

        Content = _canvas;
        SizeChanged += (_, _) => Redraw();
        Loaded += (_, _) => Redraw();
    }

    private static void OnValuesChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
        => (d as SparklineChart)?.Redraw();

    private void Redraw()
    {
        if (ActualWidth <= 1 || ActualHeight <= 1) return;

        var values = Values;
        if (values is null || values.Count == 0)
        {
            _line.Points.Clear();
            _area.Points.Clear();
            _area.Visibility = Visibility.Collapsed;
            return;
        }

        _line.Stroke = Stroke;
        if (Fill is not null) _area.Fill = Fill;

        var max = Maximum > 0 ? Maximum : values.Max();
        if (max <= 0) max = 1;

        double w = ActualWidth;
        double h = ActualHeight;
        double stepX = values.Count > 1 ? w / (values.Count - 1) : w;

        _line.Points.Clear();
        _area.Points.Clear();

        for (var i = 0; i < values.Count; i++)
        {
            var x = i * stepX;
            var normalized = Math.Clamp(values[i] / max, 0, 1);
            var y = h - normalized * (h - 4) - 2;
            _line.Points.Add(new Point(x, y));
            _area.Points.Add(new Point(x, y));
        }

        if (values.Count > 1)
        {
            _area.Points.Add(new Point(w, h));
            _area.Points.Add(new Point(0, h));
            _area.Visibility = Visibility.Visible;
        }
    }
}

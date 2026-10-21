using System.Globalization;
using System.Windows;
using System.Windows.Media;
using EmbeddedHelper.Core;

namespace EmbeddedHelper.App;

public sealed class ObservationPlot : FrameworkElement
{
    public static readonly DependencyProperty SeriesProperty = DependencyProperty.Register(nameof(Series), typeof(double[][]), typeof(ObservationPlot), new FrameworkPropertyMetadata(Array.Empty<double[]>(), FrameworkPropertyMetadataOptions.AffectsRender, LayoutChanged));
    public static readonly DependencyProperty TimesProperty = DependencyProperty.Register(nameof(Times), typeof(double[][]), typeof(ObservationPlot), new FrameworkPropertyMetadata(Array.Empty<double[]>(), FrameworkPropertyMetadataOptions.AffectsRender));
    public static readonly DependencyProperty LabelsProperty = DependencyProperty.Register(nameof(Labels), typeof(string[]), typeof(ObservationPlot), new FrameworkPropertyMetadata(Array.Empty<string>(), FrameworkPropertyMetadataOptions.AffectsRender));
    public static readonly DependencyProperty IndependentProperty = DependencyProperty.Register(nameof(Independent), typeof(bool), typeof(ObservationPlot), new FrameworkPropertyMetadata(true, FrameworkPropertyMetadataOptions.AffectsRender, LayoutChanged));
    public double[][] Series { get => (double[][])GetValue(SeriesProperty); set => SetValue(SeriesProperty, value); }
    public double[][] Times { get => (double[][])GetValue(TimesProperty); set => SetValue(TimesProperty, value); }
    public string[] Labels { get => (string[])GetValue(LabelsProperty); set => SetValue(LabelsProperty, value); }
    public bool Independent { get => (bool)GetValue(IndependentProperty); set => SetValue(IndependentProperty, value); }
    private static readonly Pen[] pens = new[] { Brushes.DeepSkyBlue, Brushes.MediumAquamarine, Brushes.Gold, Brushes.Orchid }.Select(brush => FrozenPen(brush, 1.5)).ToArray();
    private static readonly Brush axisBrush = new SolidColorBrush(Color.FromRgb(191, 207, 221));
    private static readonly Pen axisPen = FrozenPen(axisBrush, 1);
    private static readonly Pen gridPen = FrozenPen(new SolidColorBrush(Color.FromArgb(45, 191, 207, 221)), 1);
    private double[][]? renderedSeries, renderedTimes;
    private Size renderedSize;
    private bool renderedIndependent, hasSeconds;
    private StreamGeometry[] geometries = [];
    private PlotAxis[] verticalAxes = [];
    private Rect[] areas = [];
    private double firstTime, lastTime = 1;
    public ObservationPlot() => MinHeight = 220;
    private static Pen FrozenPen(Brush brush, double width) { var pen = new Pen(brush, width); pen.Freeze(); return pen; }
    private static void LayoutChanged(DependencyObject sender, DependencyPropertyChangedEventArgs e)
    {
        var plot = (ObservationPlot)sender;
        plot.MinHeight = plot.Independent ? Math.Max(220, Math.Min(16, plot.Series.Length) * 150) : Math.Max(220, Math.Min(16, plot.Series.Length) * 17 + 150);
    }
    protected override void OnRenderSizeChanged(SizeChangedInfo info) { base.OnRenderSizeChanged(info); InvalidateVisual(); }
    protected override void OnRender(DrawingContext context)
    {
        base.OnRender(context);
        if (ActualWidth < 120 || ActualHeight < 60) return;
        if (!ReferenceEquals(renderedSeries, Series) || !ReferenceEquals(renderedTimes, Times) || renderedSize != RenderSize || renderedIndependent != Independent)
        {
            RebuildGeometry(); renderedSeries = Series; renderedTimes = Times; renderedSize = RenderSize; renderedIndependent = Independent;
        }
        context.PushClip(new RectangleGeometry(new Rect(RenderSize)));
        for (var i = 0; i < areas.Length; i++)
            if (Independent || i == 0) DrawAxes(context, areas[i], verticalAxes[i]);
        for (var i = 0; i < geometries.Length; i++)
        {
            var pen = pens[i % pens.Length];
            context.PushClip(new RectangleGeometry(areas[i]));
            context.DrawGeometry(null, pen, geometries[i]);
            context.Pop();
            var label = i < Labels.Length ? Labels[i] : $"曲线 {i + 1}";
            DrawText(context, label, new Point(4, Independent ? i * ActualHeight / geometries.Length : i * 17), pen.Brush, ActualWidth - 8);
        }
        context.Pop();
    }
    private void DrawAxes(DrawingContext context, Rect area, PlotAxis vertical)
    {
        DrawText(context, "数值", new Point(4, area.Top - 18), axisBrush, 70);
        foreach (var value in vertical.Ticks)
        {
            var y = area.Bottom - vertical.Fraction(value) * area.Height;
            context.DrawLine(gridPen, new Point(area.Left, y), new Point(area.Right, y));
            context.DrawLine(axisPen, new Point(area.Left - 4, y), new Point(area.Left, y));
            DrawText(context, PlotAxis.Label(value), new Point(0, y - 7), axisBrush, area.Left - 8, TextAlignment.Right);
        }
        context.DrawLine(axisPen, area.TopLeft, area.BottomLeft);
        context.DrawLine(axisPen, area.BottomLeft, area.BottomRight);
        var intervals = Math.Clamp((int)(area.Width / 110), 2, 6);
        for (var tick = 0; tick <= intervals; tick++)
        {
            var fraction = tick / (double)intervals;
            var x = area.Left + fraction * area.Width;
            var value = firstTime + fraction * (lastTime - firstTime);
            context.DrawLine(gridPen, new Point(x, area.Top), new Point(x, area.Bottom));
            context.DrawLine(axisPen, new Point(x, area.Bottom), new Point(x, area.Bottom + 4));
            DrawText(context, PlotAxis.Label(value), new Point(x - 38, area.Bottom + 5), axisBrush, 76, TextAlignment.Center);
        }
        DrawText(context, hasSeconds ? "时间 (s)" : "样本序号", new Point(area.Left, area.Bottom + 23), axisBrush, area.Width, TextAlignment.Center);
    }
    private void DrawText(DrawingContext context, string value, Point position, Brush brush, double width, TextAlignment alignment = TextAlignment.Left)
    {
        var text = new FormattedText(value, CultureInfo.CurrentCulture, FlowDirection.LeftToRight, new Typeface("Segoe UI"), 11, brush, VisualTreeHelper.GetDpi(this).PixelsPerDip)
        { MaxTextWidth = Math.Max(1, width), MaxLineCount = 1, Trimming = TextTrimming.CharacterEllipsis, TextAlignment = alignment };
        context.DrawText(text, position);
    }
    private void RebuildGeometry()
    {
        var rows = Series.Take(16).ToArray();
        geometries = new StreamGeometry[rows.Length];
        verticalAxes = new PlotAxis[Math.Max(1, rows.Length)];
        areas = new Rect[Math.Max(1, rows.Length)];
        hasSeconds = rows.Length > 0 && Times.Length >= rows.Length && rows.Select((row, i) => Times[i].Length == row.Length && Times[i].All(double.IsFinite)).All(valid => valid);
        var timeValues = hasSeconds ? Times.Take(rows.Length).SelectMany(t => t).ToArray() : [];
        firstTime = timeValues.Length > 0 ? timeValues.Min() : 0;
        lastTime = timeValues.Length > 0 ? timeValues.Max() : Math.Max(1, rows.Select(row => row.Length - 1).DefaultIfEmpty(1).Max());
        if (lastTime <= firstTime) lastTime = firstTime + 1;
        var commonAxis = PlotAxis.Create(rows.SelectMany(row => row));
        for (var i = 0; i < areas.Length; i++)
        {
            var laneHeight = Independent ? ActualHeight / areas.Length : ActualHeight;
            var top = Independent ? i * laneHeight + 36 : Math.Min(rows.Length * 17 + 36, ActualHeight / 2);
            var bottom = Independent ? (i + 1) * laneHeight - 42 : ActualHeight - 42;
            areas[i] = new Rect(82, top, Math.Max(1, ActualWidth - 124), Math.Max(1, bottom - top));
            verticalAxes[i] = Independent && i < rows.Length ? PlotAxis.Create(rows[i], 2) : commonAxis;
            if (i >= rows.Length) continue;
            var values = rows[i];
            var area = areas[i];
            var geometry = new StreamGeometry();
            using (var drawing = geometry.Open())
            {
                var first = true;
                // At most 2000 retained samples: retain gaps and their true timestamps.
                for (var index = 0; index < values.Length; index++)
                {
                    if (!double.IsFinite(values[index])) { first = true; continue; }
                    var time = hasSeconds ? Times[i][index] : index;
                    var point = new Point(area.Left + (time - firstTime) / (lastTime - firstTime) * area.Width, area.Bottom - verticalAxes[i].Fraction(values[index]) * area.Height);
                    if (first) { drawing.BeginFigure(point, false, false); first = false; }
                    else drawing.LineTo(point, true, false);
                }
            }
            geometry.Freeze(); geometries[i] = geometry;
        }
    }
}

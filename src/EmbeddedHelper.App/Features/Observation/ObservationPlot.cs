using System.Globalization;
using System.Windows;
using System.Windows.Media;
using EmbeddedHelper.Core;

namespace EmbeddedHelper.App;

public sealed class ObservationPlot : FrameworkElement
{
    public static readonly DependencyProperty SeriesProperty = DependencyProperty.Register(nameof(Series), typeof(double[][]), typeof(ObservationPlot), new FrameworkPropertyMetadata(Array.Empty<double[]>(), FrameworkPropertyMetadataOptions.AffectsRender, LayoutChanged));
    public static readonly DependencyProperty LabelsProperty = DependencyProperty.Register(nameof(Labels), typeof(string[]), typeof(ObservationPlot), new FrameworkPropertyMetadata(Array.Empty<string>(), FrameworkPropertyMetadataOptions.AffectsRender));
    public static readonly DependencyProperty IndependentProperty = DependencyProperty.Register(nameof(Independent), typeof(bool), typeof(ObservationPlot), new FrameworkPropertyMetadata(true, FrameworkPropertyMetadataOptions.AffectsRender, LayoutChanged));
    public double[][] Series { get => (double[][])GetValue(SeriesProperty); set => SetValue(SeriesProperty, value); }
    public string[] Labels { get => (string[])GetValue(LabelsProperty); set => SetValue(LabelsProperty, value); }
    public bool Independent { get => (bool)GetValue(IndependentProperty); set => SetValue(IndependentProperty, value); }
    private static readonly Pen[] pens = new[] { Brushes.DeepSkyBlue, Brushes.MediumAquamarine, Brushes.Gold, Brushes.Orchid }.Select(brush => { var pen = new Pen(brush, 1.5); pen.Freeze(); return pen; }).ToArray();
    private double[][]? renderedSeries;
    private Size renderedSize;
    private bool renderedIndependent;
    private StreamGeometry[] geometries = [];
    private (double Min, double Max)[] bounds = [];
    public ObservationPlot() => MinHeight = 220;
    private static void LayoutChanged(DependencyObject sender, DependencyPropertyChangedEventArgs e)
    {
        var plot = (ObservationPlot)sender;
        plot.MinHeight = plot.Independent ? Math.Max(220, Math.Min(16, plot.Series.Length) * 110) : 220;
    }
    protected override void OnRenderSizeChanged(SizeChangedInfo info) { base.OnRenderSizeChanged(info); InvalidateVisual(); }
    protected override void OnRender(DrawingContext context)
    {
        base.OnRender(context);
        if (ActualWidth < 5 || ActualHeight < 5) return;
        if (!ReferenceEquals(renderedSeries, Series) || renderedSize != RenderSize || renderedIndependent != Independent)
        { RebuildGeometry(); renderedSeries = Series; renderedSize = RenderSize; renderedIndependent = Independent; }
        context.PushClip(new RectangleGeometry(new Rect(RenderSize)));
        for (var i = 0; i < geometries.Length; i++)
        {
            var pen = pens[i % pens.Length];
            context.DrawGeometry(null, pen, geometries[i]);
            var label = i < Labels.Length ? Labels[i] : $"曲线 {i + 1}";
            var text = Independent ? $"{label}   {bounds[i].Min:G6} ～ {bounds[i].Max:G6}" : label;
            var caption = new FormattedText(text, CultureInfo.CurrentCulture, FlowDirection.LeftToRight, new Typeface("Segoe UI"), 12, pen.Brush, VisualTreeHelper.GetDpi(this).PixelsPerDip) { MaxTextWidth = Math.Max(1, ActualWidth - 8), MaxLineCount = 1, Trimming = TextTrimming.CharacterEllipsis };
            context.DrawText(caption, new Point(4, Independent ? i * ActualHeight / geometries.Length : i * 17));
        }
        context.Pop();
    }
    private void RebuildGeometry()
    {
        var rows = Series.Take(16).ToArray();
        geometries = new StreamGeometry[rows.Length]; bounds = new (double, double)[rows.Length];
        if (rows.Length == 0) return;
        var valid = rows.SelectMany(s => s).Where(double.IsFinite).ToArray();
        var commonMin = valid.Length == 0 ? 0 : valid.Min();
        var commonMax = valid.Length == 0 ? 1 : valid.Max();
        for (var i = 0; i < rows.Length; i++)
        {
            var values = rows[i];
            var finite = values.Where(double.IsFinite).ToArray();
            var min = Independent && finite.Length > 0 ? finite.Min() : commonMin;
            var max = Independent && finite.Length > 0 ? finite.Max() : commonMax;
            bounds[i] = (min, max);
            var lane = Independent ? ActualHeight / rows.Length : ActualHeight;
            var top = Independent ? i * lane + 24 : Math.Min(rows.Length * 17 + 8, ActualHeight / 2);
            var bottom = Independent ? (i + 1) * lane - 12 : ActualHeight - 12;
            var range = max - min;
            var geometry = new StreamGeometry();
            using (var drawing = geometry.Open())
            {
                var first = true;
                foreach (var index in PlotSampling.Indices(values, (int)ActualWidth))
                {
                    if (!double.IsFinite(values[index])) { first = true; continue; }
                    var fraction = range > 0 ? (values[index] - min) / range : 0.5;
                    var point = new Point(index * ActualWidth / Math.Max(1, values.Length - 1), bottom - fraction * Math.Max(1, bottom - top));
                    if (first) { drawing.BeginFigure(point, false, false); first = false; }
                    else drawing.LineTo(point, true, false);
                }
            }
            geometry.Freeze(); geometries[i] = geometry;
        }
    }
}

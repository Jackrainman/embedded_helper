using System.Windows;
using System.Windows.Media;
using EmbeddedHelper.Core;

namespace EmbeddedHelper.App;

public sealed class ObservationPlot : FrameworkElement
{
    public static readonly DependencyProperty SeriesProperty = DependencyProperty.Register(nameof(Series), typeof(double[][]), typeof(ObservationPlot), new FrameworkPropertyMetadata(Array.Empty<double[]>(), FrameworkPropertyMetadataOptions.AffectsRender));
    public double[][] Series { get => (double[][])GetValue(SeriesProperty); set => SetValue(SeriesProperty, value); }
    private static readonly Pen[] pens = new[] { Brushes.DeepSkyBlue, Brushes.MediumAquamarine, Brushes.Gold, Brushes.Orchid }.Select(brush => { var pen = new Pen(brush, 1.5); pen.Freeze(); return pen; }).ToArray();
    private double[][]? renderedSeries;
    private Size renderedSize;
    private StreamGeometry[] geometries = [];

    protected override void OnRenderSizeChanged(SizeChangedInfo info)
    { base.OnRenderSizeChanged(info); InvalidateVisual(); }

    protected override void OnRender(DrawingContext context)
    {
        base.OnRender(context);
        if (ActualWidth < 5 || ActualHeight < 5) return;
        if (!ReferenceEquals(renderedSeries, Series) || renderedSize != RenderSize)
        {
            RebuildGeometry(); renderedSeries = Series; renderedSize = RenderSize;
        }
        context.PushClip(new RectangleGeometry(new Rect(RenderSize)));
        for (var i = 0; i < geometries.Length; i++) context.DrawGeometry(null, pens[i % pens.Length], geometries[i]);
        context.Pop();
    }
    private void RebuildGeometry()
    {
        var rows = Series.Where(s => s.Length > 1).Take(16).ToArray();
        geometries = new StreamGeometry[rows.Length];
        if (rows.Length == 0) return;
        var min = rows.Min(s => s.Min());
        var range = Math.Max(0.001, rows.Max(s => s.Max()) - min);
        for (var i = 0; i < rows.Length; i++)
        {
            var values = rows[i];
            var geometry = new StreamGeometry();
            using (var drawing = geometry.Open())
            {
                var first = true;
                foreach (var index in PlotSampling.Indices(values, (int)ActualWidth))
                {
                    var point = new Point(index * ActualWidth / (values.Length - 1), ActualHeight - 8 - (values[index] - min) / range * Math.Max(1, ActualHeight - 16));
                    if (first) { drawing.BeginFigure(point, false, false); first = false; }
                    else drawing.LineTo(point, true, false);
                }
            }
            geometry.Freeze();
            geometries[i] = geometry;
        }
    }
}

using System.Globalization;

namespace EmbeddedHelper.Core;

public sealed record PlotAxis(double Minimum, double Maximum, double[] Ticks)
{
    public double Fraction(double value) => (value - Minimum) / (Maximum - Minimum);
    public static string Label(double value) => value.ToString("G10", CultureInfo.CurrentCulture);

    public static PlotAxis Create(IEnumerable<double> values, int intervals = 4)
    {
        var finite = values.Where(double.IsFinite).ToArray();
        var min = finite.Length == 0 ? 0 : finite.Min();
        var max = finite.Length == 0 ? 1 : finite.Max();
        if (max == min)
        {
            var padding = min == 0 ? 1 : Math.Abs(min) * 0.05;
            min -= padding; max += padding;
        }
        var rawStep = (max - min) / Math.Clamp(intervals, 2, 8);
        var magnitude = Math.Pow(10, Math.Floor(Math.Log10(rawStep)));
        var normalized = rawStep / magnitude;
        var step = (normalized <= 1 ? 1 : normalized <= 2 ? 2 : normalized <= 5 ? 5 : 10) * magnitude;
        var lower = Math.Floor(min / step) * step;
        var upper = Math.Ceiling(max / step) * step;
        var count = Math.Clamp((int)Math.Round((upper - lower) / step), 1, 12);
        return new(lower, upper, Enumerable.Range(0, count + 1).Select(i => lower + i * step).ToArray());
    }
}

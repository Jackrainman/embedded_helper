namespace EmbeddedHelper.Core;

public static class PlotSampling
{
    // Preserve chronological extrema within each pixel bucket, including the first/last sample.
    public static int[] Indices(IReadOnlyList<double> values, int pixels)
    {
        if (values.Count <= 2 || values.Count <= Math.Max(1, pixels) * 2) return Enumerable.Range(0, values.Count).ToArray();
        pixels = Math.Max(1, pixels);
        var result = new List<int>(pixels * 2 + 2) { 0 };
        for (var bucket = 0; bucket < pixels; bucket++)
        {
            var start = bucket * values.Count / pixels;
            var end = (bucket + 1) * values.Count / pixels;
            var min = start; var max = start;
            for (var i = start + 1; i < end; i++)
            {
                if (values[i] < values[min]) min = i;
                if (values[i] > values[max]) max = i;
            }
            foreach (var i in new[] { Math.Min(min, max), Math.Max(min, max) })
                if (result[^1] != i) result.Add(i);
        }
        if (result[^1] != values.Count - 1) result.Add(values.Count - 1);
        return result.ToArray();
    }
}

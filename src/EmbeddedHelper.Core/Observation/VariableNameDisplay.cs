namespace EmbeddedHelper.Core;

public static class VariableNameDisplay
{
    // Rendering only: identities and exported headers always retain the original name.
    public static string Fit(string name, double width, Func<string, double> measure)
    {
        if (string.IsNullOrEmpty(name) || measure(name) <= width) return name;
        const string dots = "…";
        if (width <= 0 || measure(dots) > width) return "";
        var split = name.LastIndexOf('.');
        if (split < 1) split = Math.Max(1, name.Length / 2);
        var tail = name[split..];
        if (measure(dots + tail) <= width)
        {
            var low = 0; var high = split;
            while (low < high)
            {
                var middle = (low + high + 1) / 2;
                if (measure(name[..middle] + dots + tail) <= width) low = middle; else high = middle - 1;
            }
            return name[..low] + dots + tail;
        }
        // A field itself can exceed the entire cell. Retain its ending and expose the full tooltip.
        var count = 0; var limit = tail.Length;
        while (count < limit)
        {
            var middle = (count + limit + 1) / 2;
            if (measure(dots + tail[^middle..]) <= width) count = middle; else limit = middle - 1;
        }
        return dots + (count == 0 ? "" : tail[^count..]);
    }
}

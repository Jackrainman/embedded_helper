namespace EmbeddedHelper.Core;

public static class DiagnosticArguments
{
    public static int? PlotCount(string[] args)
    {
        if (args.FirstOrDefault() != "--plot-preview") return null;
        if (args is [_, var count] && int.TryParse(count, out var rows) && rows is >= 1 and <= 16) return rows;
        throw new ArgumentException("--plot-preview 需要一个1～16的整数参数，例如 --plot-preview 2。");
    }
}

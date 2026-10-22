using EmbeddedHelper.Core;
using Xunit;

namespace EmbeddedHelper.Tests;

public sealed class DiagnosticArgumentsTests
{
    [Theory]
    [InlineData("abc")]
    [InlineData("0")]
    [InlineData("17")]
    public void InvalidPlotPreviewIsRejected(string value)
    {
        Assert.Contains("1～16", Assert.Throws<ArgumentException>(() => DiagnosticArguments.PlotCount(["--plot-preview", value])).Message);
        Assert.Throws<ArgumentException>(() => DiagnosticArguments.PlotCount(["--plot-preview"]));
        Assert.Throws<ArgumentException>(() => DiagnosticArguments.PlotCount(["--plot-preview", "1", "extra"]));
        Assert.Null(DiagnosticArguments.PlotCount([]));
        Assert.Equal(1, DiagnosticArguments.PlotCount(["--plot-preview", "1"]));
        Assert.Equal(16, DiagnosticArguments.PlotCount(["--plot-preview", "16"]));
    }
}

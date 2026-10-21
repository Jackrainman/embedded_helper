using EmbeddedHelper.Core;
using Xunit;

namespace EmbeddedHelper.Tests;

public class ProbeSelectionTests
{
    [Fact]
    public void NoProbeDoesNotInventASelection()
    {
        Assert.Null(ProbeSelection.Resolve([], "").Selected);
    }
    [Fact]
    public void UniqueProbeWithoutHistoryCanBeSelected()
    {
        var probe = new UsbProbe(1234, "J-Link V9");
        Assert.Equal(probe, ProbeSelection.Resolve([probe], "").Selected);
    }
    [Fact]
    public void MultipleProbesRequireAnExplicitChoice()
    {
        Assert.Null(ProbeSelection.Resolve([new(1234, "V9"), new(5678, "V11")], "").Selected);
    }
    [Fact]
    public void RememberedProbeWinsRegardlessOfDiscoveryOrder()
    {
        var chosen = ProbeSelection.Resolve([new(5678, "V11"), new(1234, "V9")], "1234");
        Assert.Equal(1234, chosen.Selected!.SerialNumber);
    }
    [Theory]
    [InlineData("1234")]
    [InlineData("invalid")]
    public void MissingOrInvalidHistoryNeverFallsBackToAnotherBoard(string remembered)
    {
        var result = ProbeSelection.Resolve([new(5678, "V11")], remembered);
        Assert.Null(result.Selected);
        Assert.Contains("没有自动改用", result.Message);
    }
}

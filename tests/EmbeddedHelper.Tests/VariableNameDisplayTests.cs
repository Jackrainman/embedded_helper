using EmbeddedHelper.Core;
using Xunit;

namespace EmbeddedHelper.Tests;

public sealed class VariableNameDisplayTests
{
    [Fact]
    public void MiddleTrimmingRetainsTheLeafAndTooltipSourceIsUnchanged()
    {
        const string name = "drive.controller.feedback.motor.encoder.position";
        var text = VariableNameDisplay.Fit(name, 22, s => s.Length);
        Assert.StartsWith("drive", text); Assert.EndsWith(".position", text); Assert.Contains("…", text); Assert.True(text.Length <= 22);
        Assert.Equal(name, VariableNameDisplay.Fit(name, 200, s => s.Length));
        Assert.Equal("…ion", VariableNameDisplay.Fit(name, 4, s => s.Length));
        Assert.Equal("", VariableNameDisplay.Fit(name, 0, s => s.Length));
    }
    [Fact]
    public void SameLeafKeepsDistinctParentsWhenSpaceAllowsAndUsesActualGlyphWidths()
    {
        var a = VariableNameDisplay.Fit("left.motor.sensor.position", 20, s => s.Sum(c => c == 'm' ? 3 : 1));
        var b = VariableNameDisplay.Fit("right.motor.sensor.position", 20, s => s.Sum(c => c == 'm' ? 3 : 1));
        Assert.NotEqual(a, b); Assert.EndsWith(".position", a); Assert.EndsWith(".position", b);
        Assert.Equal("counter", VariableNameDisplay.Fit("counter", 7, s => s.Length));
    }
}

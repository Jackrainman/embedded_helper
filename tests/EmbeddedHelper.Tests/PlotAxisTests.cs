using EmbeddedHelper.Core;
using EmbeddedHelper.App;
using Xunit;

namespace EmbeddedHelper.Tests;

public sealed class PlotAxisTests
{
    [Theory]
    [InlineData(0)]
    [InlineData(128)]
    [InlineData(-128)]
    [InlineData(4294967295)]
    [InlineData(0.00000001)]
    public void ConstantValuesHaveVisibleNonzeroRange(double value)
    {
        var axis = PlotAxis.Create([value, value], 2);
        Assert.True(axis.Minimum < value);
        Assert.True(axis.Maximum > value);
        Assert.InRange(axis.Fraction(value), 0.01, 0.99);
        Assert.InRange(axis.Ticks.Length, 2, 13);
    }

    [Theory]
    [InlineData(4294967290, 4294967295)]
    [InlineData(-0.000000001, 0.000000001)]
    public void NarrowRangesRetainDistinctTickLabels(double min, double max)
    {
        var axis = PlotAxis.Create([min, max]);
        Assert.Equal(axis.Ticks.Length, axis.Ticks.Select(PlotAxis.Label).Distinct().Count());
        Assert.InRange(axis.Maximum - axis.Minimum, max - min, (max - min) * 2);
    }

    [Fact]
    public void ThirdSelectedVariableDisablesIndependentScaleUntilUserChoosesItAgain()
    {
        var model = new ObservationViewModel();
        model.LoadIndex(Enumerable.Range(0, 3).Select(i => new Variable($"v{i}", $"v{i}", (uint)(0x20000000 + i * 4), "float", 4, "")).ToArray(), ["v0", "v1"]);
        Assert.True(model.CanScaleIndependently);
        Assert.False(model.IndependentScale);
        model.IndependentScale = true;
        Assert.True(model.IndependentScale);
        model.Variables[2].Selected = true;
        Assert.False(model.CanScaleIndependently);
        Assert.False(model.IndependentScale);
        Assert.Equal("仅显示不超过两个参数时可用", model.ScaleHint);
        model.IndependentScale = true;
        Assert.False(model.IndependentScale);
        model.Variables[2].Selected = false;
        Assert.True(model.CanScaleIndependently);
        Assert.False(model.IndependentScale);
        Assert.Null(model.ScaleHint);
        model.IndependentScale = true;
        Assert.True(model.IndependentScale);
        model.LoadIndex([], []);
        Assert.True(model.CanScaleIndependently);
    }

    [Fact]
    public void MissingValuesDoNotCorruptScaleAndNegativeRangeIsCovered()
    {
        var axis = PlotAxis.Create([double.NaN, -0.0037, 0.0014, double.PositiveInfinity]);
        Assert.True(axis.Minimum <= -0.0037);
        Assert.True(axis.Maximum >= 0.0014);
        Assert.All(axis.Ticks, value => Assert.True(double.IsFinite(value)));
        Assert.True(axis.Ticks.Zip(axis.Ticks.Skip(1)).All(pair => pair.First < pair.Second));
        Assert.Equal(0, axis.Fraction(axis.Minimum));
        Assert.Equal(1, axis.Fraction(axis.Maximum));
        Assert.Equal(0, PlotAxis.Create([double.NaN]).Minimum);
        Assert.Equal(1, PlotAxis.Create([]).Maximum);
    }
}

using EmbeddedHelper.Core;
using Xunit;

namespace EmbeddedHelper.Tests;

public sealed class SamplingHealthTests
{
    [Fact]
    public void OnlySustainedWholeRoundFailureFaultsAndSuccessResets()
    {
        var health = new SamplingHealth();
        health.Observe(2, 0, TimeSpan.Zero);
        health.Observe(2, 0, TimeSpan.FromSeconds(1));
        health.Observe(2, 1, TimeSpan.FromSeconds(4));
        health.Observe(2, 0, TimeSpan.FromSeconds(5));
        health.Observe(2, 0, TimeSpan.FromSeconds(6));
        Assert.Throws<IOException>(() => health.Observe(2, 0, TimeSpan.FromSeconds(8)));
        health.Reset();
        health.Observe(2, 0, TimeSpan.FromSeconds(10));
        health.Observe(0, 0, TimeSpan.FromSeconds(100));
        health.Observe(2, 0, TimeSpan.FromSeconds(101));
        health.Observe(2, 2, TimeSpan.FromSeconds(105));
    }
}

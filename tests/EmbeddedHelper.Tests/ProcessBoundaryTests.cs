using System.Diagnostics;
using System.Text.Json;
using EmbeddedHelper.Core;
using Xunit;
using Xunit.Abstractions;

namespace EmbeddedHelper.Tests;

[Collection("Offline client")]
public sealed class ProcessBoundaryTests(ITestOutputHelper output) : TestWorkspace
{
    private static string Worker => Path.Combine(AppContext.BaseDirectory, "offline-worker", "OfflineWorker.exe");
    private (string Settings, string Pid) Scenario(string mode)
    {
        var file = Path.Combine(root, Guid.NewGuid() + ".json"); var pid = file + ".pid";
        File.WriteAllText(file, JsonSerializer.Serialize(new { mode, pidFile = pid })); return (file, pid);
    }
    private void AssertExited(string pidFile)
    {
        var pid = int.Parse(File.ReadAllText(pidFile));
        try { using var child = Process.GetProcessById(pid); Assert.True(child.HasExited, "Controlled child remained alive: " + pid); }
        catch (ArgumentException) { }
        output.WriteLine("controlled PID {0}: exited; no SDK/device access", pid);
    }
    [Theory]
    [InlineData("wrong-session")]
    [InlineData("overlong")]
    [InlineData("no-ack")]
    [InlineData("update-no-ack")]
    [InlineData("silent-after-start")]
    public async Task ProtocolFailuresAreVisibleAndUnresponsiveChildIsReaped(string mode)
    {
        var scenario = Scenario(mode); await using var session = new ObservationSession();
        try
        {
            var start = session.StartAsync(Worker, new(scenario.Settings, 1, "", 1, "offline", "SWD", 1, 200, false, []));
            if (mode is "wrong-session" or "overlong")
                await Assert.ThrowsAsync<InvalidDataException>(() => start);
            else if (mode == "no-ack") await Assert.ThrowsAsync<TimeoutException>(() => start);
            else
            {
                await start;
                if (mode == "update-no-ack") await Assert.ThrowsAsync<TimeoutException>(() => session.UpdateAsync([], 200));
                else
                {
                    var wait = Stopwatch.StartNew();
                    while (!session.CanRetry && wait.Elapsed < TimeSpan.FromSeconds(18)) await Task.Delay(50);
                    Assert.Contains("超时", session.State);
                    using var child = Process.GetProcessById(int.Parse(File.ReadAllText(scenario.Pid)));
                    Assert.False(child.HasExited); // Watchdog marks fault; Stop owns process release.
                }
            }
            Assert.True(session.CanRetry);
        }
        finally { await session.DisposeAsync(); }
        Assert.Equal("已释放", session.State); AssertExited(scenario.Pid);
    }
    [Theory]
    [InlineData("probe-hang")]
    [InlineData("probe-error")]
    [InlineData("probe-empty")]
    public async Task ProbeHelperTimeoutFailureEmptyAndRetryUseOnlyControlledProcesses(string mode)
    {
        for (var attempt = 0; attempt < 2; attempt++)
        {
            var scenario = Scenario(mode);
            if (mode == "probe-hang") Assert.Contains("超时", (await Assert.ThrowsAsync<TimeoutException>(() => ProbeDiscoveryProcess.ReadAsync(Worker, scenario.Settings))).Message);
            else if (mode == "probe-error") Assert.Contains("controlled enumeration failure", (await Assert.ThrowsAsync<IOException>(() => ProbeDiscoveryProcess.ReadAsync(Worker, scenario.Settings))).Message);
            else Assert.Empty(await ProbeDiscoveryProcess.ReadAsync(Worker, scenario.Settings));
            AssertExited(scenario.Pid);
        }
    }
}

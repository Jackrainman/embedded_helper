using System.Threading.Channels;
using EmbeddedHelper.Core;
using Xunit;

namespace EmbeddedHelper.Tests;

[Collection("Offline client")]
public sealed class ObservationCoordinationTests : TestWorkspace
{
    private static readonly Variable value = new("global:value", "value", 0x20000000, "int32", 4, "可读取");
    private static ObservationRequest Request => new("unused.dll", 123, "", 19011, "STM32G474VE", "SWD", 8000, 100, true, [value]);
    private (ProjectInfo Project, BuildTarget Target, string Dll, string Elf) Inputs()
    {
        var dll = Path.Combine(root, "not-a-library.dll"); File.WriteAllText(dll, "never loaded");
        var elf = Path.Combine(root, "not-an-elf.elf"); File.WriteAllText(elf, "index supplied by test");
        var target = new BuildTarget("Debug", "STM32G474VE", "SWD", 8000, elf);
        return (new ProjectInfo(root, "test", "G4", target.Device, [target], Path.Combine(root, "remote.json"), []), target, dll, elf);
    }
    [Fact]
    public async Task PreparationUsesSavedSnapshotAndLocalDoesNotReadRemoteFile()
    {
        var input = Inputs();
        File.WriteAllText(input.Project.RemotePath, "not json");
        WatchSettingsStore.Save(root, new("Debug", [value.Identity], 75) { ProbeSerial = "123456" });
        var result = await ObservationPreparation.PrepareAsync(input.Project, input.Target, input.Elf, input.Dll, _ => Task.FromResult(new[] { value }));
        WatchSettingsStore.Save(root, new("Debug", [], 999) { ProbeSerial = "999" });
        Assert.Equal(75, result.Request.SampleRate); Assert.Equal(123456, result.Request.ProbeSerial);
        Assert.Equal("", result.Request.Host); Assert.False(result.Request.Rtt); Assert.Equal(value, Assert.Single(result.Request.Variables));
    }
    [Fact]
    public async Task RemotePreparationReadsSavedEndpointAndSuppressesRtt()
    {
        var input = Inputs();
        File.WriteAllText(input.Project.RemotePath, "{\"ServerName\":\"saved.invalid\",\"Port\":19012}");
        WatchSettingsStore.Save(root, new("Debug", [value.Identity], 100) { Transport = "remote", EnableRtt = true });
        var result = await ObservationPreparation.PrepareAsync(input.Project, input.Target, input.Elf, input.Dll, _ => Task.FromResult(new[] { value }));
        Assert.Equal("saved.invalid", result.Request.Host); Assert.Equal(19012, result.Request.Port); Assert.False(result.Request.Rtt);
    }
    [Fact]
    public async Task RttOnlyPreparationDoesNotReadElfAndReportsMissingSdkInSettings()
    {
        var input = Inputs();
        WatchSettingsStore.Save(root, new("Debug", [], 100) { ProbeSerial = "123" });
        Task<Variable[]> Never(string _) => throw new InvalidOperationException("must not index");
        var result = await ObservationPreparation.PrepareAsync(input.Project, input.Target, "missing.elf", input.Dll, Never, true);
        Assert.Empty(result.Index); Assert.Empty(result.Request.Variables); Assert.True(result.Request.Rtt);
        var error = await Assert.ThrowsAsync<ObservationPreparationException>(() => ObservationPreparation.PrepareAsync(input.Project, input.Target, "missing.elf", "missing.dll", Never, true));
        Assert.Equal(ObservationConfigurationArea.Settings, error.Area);
    }
    [Theory]
    [InlineData("Other", 100, "local", "123", "global:value")]
    [InlineData("Debug", 1001, "local", "123", "global:value")]
    [InlineData("Debug", 100, "unknown", "123", "global:value")]
    [InlineData("Debug", 100, "local", "", "global:value")]
    [InlineData("Debug", 100, "local", "123", "missing:value")]
    public async Task InvalidSavedSettingsCannotBecomeReady(string target, int rate, string transport, string probe, string identity)
    {
        var input = Inputs();
        WatchSettingsStore.Save(root, new(target, [identity], rate) { Transport = transport, ProbeSerial = probe });
        var error = await Assert.ThrowsAsync<ObservationPreparationException>(() => ObservationPreparation.PrepareAsync(input.Project, input.Target, input.Elf, input.Dll, _ => Task.FromResult(new[] { value })));
        Assert.Equal(transport == "unknown" || probe == "" ? ObservationConfigurationArea.Connection : ObservationConfigurationArea.Observation, error.Area);
    }
    [Fact]
    public async Task PreparationFailureRoutesRemoteAndElfErrorsWithoutWritingInputs()
    {
        var input = Inputs();
        WatchSettingsStore.Save(root, new("Debug", [value.Identity], 100) { Transport = "remote" });
        var watch = File.ReadAllBytes(Path.Combine(root, ".embedded_helper", "watch.json"));
        var error = await Assert.ThrowsAsync<ObservationPreparationException>(() => ObservationPreparation.PrepareAsync(input.Project, input.Target, input.Elf, input.Dll, _ => Task.FromResult(new[] { value })));
        Assert.Equal(ObservationConfigurationArea.Remote, error.Area);
        Assert.Equal(value.Identity, Assert.Single(error.SavedSettings!.Identities));
        Assert.Empty(error.Index);
        File.WriteAllText(input.Project.RemotePath, "{\"ServerName\":\"saved.invalid\",\"Port\":19011}");
        error = await Assert.ThrowsAsync<ObservationPreparationException>(() => ObservationPreparation.PrepareAsync(input.Project, input.Target, input.Elf, input.Dll, _ => throw new IOException("ELF changed")));
        Assert.Equal(ObservationConfigurationArea.Project, error.Area);
        Assert.Equal(watch, File.ReadAllBytes(Path.Combine(root, ".embedded_helper", "watch.json")));
    }
    [Fact]
    public async Task MissingSdkPreservesIndexedSavedVariablesWithoutCreatingReadyRequest()
    {
        var input = Inputs();
        WatchSettingsStore.Save(root, new("Debug", [value.Identity], 100) { ProbeSerial = "123", EnableRtt = false });
        var error = await Assert.ThrowsAsync<ObservationPreparationException>(() => ObservationPreparation.PrepareAsync(input.Project, input.Target, input.Elf, "missing.dll", _ => Task.FromResult(new[] { value })));
        Assert.Equal(ObservationConfigurationArea.Settings, error.Area);
        Assert.False(error.SavedSettings!.EnableRtt);
        Assert.Equal(value, Assert.Single(error.Index));
        WatchSettingsStore.Save(root, new("Other", [value.Identity], 100) { ProbeSerial = "123" });
        error = await Assert.ThrowsAsync<ObservationPreparationException>(() => ObservationPreparation.PrepareAsync(input.Project, input.Target, input.Elf, input.Dll, _ => Task.FromResult(new[] { value })));
        Assert.Null(error.SavedSettings); Assert.Empty(error.Index);
    }
    [Fact]
    public async Task MissingSdkKeepsOneEditableIndexAndSavedStructSelection()
    {
        var input = Inputs();
        var index = ElfIndex.Read(Path.Combine(fixtures, "structures.elf"));
        var identity = "global:motors[1].feedback.speed";
        WatchSettingsStore.Save(root, new("Debug", [identity], 100) { ProbeSerial = "123" });
        var error = await Assert.ThrowsAsync<ObservationPreparationException>(() => ObservationPreparation.PrepareAsync(
            input.Project, input.Target, input.Elf, "missing.dll", _ => Task.FromResult(index)));
        var view = new EmbeddedHelper.App.ObservationViewModel();
        view.LoadIndex(error.Index, error.SavedSettings!.Identities);
        var selected = Assert.Single(view.VisibleRows);
        Assert.Same(selected, Assert.Single(view.Variables, v => v.Selected));
        Assert.True(selected.Variable.Supported); Assert.Equal(identity, selected.Variable.Identity);
        Assert.False(view.CanStart); Assert.True(view.CanEdit);
        Assert.Contains(view.Variables, v => v.Variable.Name == "motors[0].feedback.speed" && !v.Selected);
        selected.Value = "123"; Assert.Equal("123", view.Variables.Single(v => v.Selected).Value);
        WatchSettingsStore.Save(root, new("Debug", [selected.Variable.Identity], 75) { ProbeSerial = "123" });
        var ready = await ObservationPreparation.PrepareAsync(input.Project, input.Target, input.Elf, input.Dll, _ => Task.FromResult(index));
        Assert.Equal(selected.Variable, Assert.Single(ready.Request.Variables));
        view.LoadIndex([], [identity]);
        Assert.False(Assert.Single(view.VisibleRows).Variable.Supported);
        Assert.Same(view.VisibleRows[0], Assert.Single(view.Variables));
        view.LoadIndex(index, [identity]);
        Assert.True(Assert.Single(view.VisibleRows).Variable.Supported);
    }
    [Fact]
    public async Task SharedConnectionMigrationIsExplicitAndTargetScoped()
    {
        Inputs();
        WatchSettingsStore.Save(root, new("Debug", [value.Identity], 75) { ProbeSerial = "123" });
        var path = Path.Combine(root, ".embedded_helper", "watch.json");
        var before = File.ReadAllBytes(path);
        Assert.Equal("123", (await ConnectionSettingsStore.LoadAsync(root, "Debug"))!.ProbeSerial);
        Assert.False(File.Exists(Path.Combine(root, ".embedded_helper", "connection.json")));
        ConnectionSettingsStore.Save(root, new("Debug", "local", "456"));
        Assert.Equal("456", (await ConnectionSettingsStore.LoadAsync(root, "Debug"))!.ProbeSerial);
        Assert.Null(await ConnectionSettingsStore.LoadAsync(root, "Other"));
        Assert.Equal(before, File.ReadAllBytes(path));
        File.WriteAllText(Path.Combine(root, ".embedded_helper", "connection.json"), "{}");
        await Assert.ThrowsAsync<InvalidDataException>(() => ConnectionSettingsStore.LoadAsync(root, ""));
    }
    [Fact]
    public async Task RttNeedsOnlySharedConnectionAndRemoteUsesSavedHttpUrl()
    {
        var input = Inputs();
        ConnectionSettingsStore.Save(root, new("Debug", "local", "123"));
        var ready = await ObservationPreparation.PrepareAsync(input.Project, input.Target, "", input.Dll,
            _ => throw new InvalidOperationException("no ELF required"), true);
        Assert.True(ready.Request.Rtt); Assert.Empty(ready.Request.Variables);
        ConnectionSettingsStore.Save(root, new("Debug", "remote", ""));
        File.WriteAllText(input.Project.RemotePath, "{\"WebUrl\":\"http://saved.invalid:8000\"}");
        Assert.Equal("saved.invalid", (await ConnectionSettingsStore.RemoteRttUriAsync(input.Project, "Debug")).Host);
        File.WriteAllText(input.Project.RemotePath, "{\"WebUrl\":\"file:///secret\"}");
        await Assert.ThrowsAsync<InvalidDataException>(() => ConnectionSettingsStore.RemoteRttUriAsync(input.Project, "Debug"));
    }
    [Fact]
    public async Task ExplicitTransportOverridesLegacyWithoutWritingAndRemoteNeedsEndpoint()
    {
        var input = Inputs();
        WatchSettingsStore.Save(root, new("Debug", [value.Identity], 100) { Transport = "remote", ProbeSerial = "123" });
        var path = Path.Combine(root, ".embedded_helper", "watch.json");
        var before = File.ReadAllBytes(path);
        var ready = await ObservationPreparation.PrepareAsync(input.Project, input.Target, input.Elf, input.Dll,
            _ => Task.FromResult(new[] { value }), transport: "local");
        Assert.Equal("", ready.Request.Host); Assert.Equal(123, ready.Request.ProbeSerial);
        var error = await Assert.ThrowsAsync<InvalidDataException>(() => ConnectionSettingsStore.RemoteEndpointAsync(input.Project));
        Assert.Equal("项目未配置远程连接", error.Message);
        File.WriteAllText(input.Project.RemotePath, "{\"ServerName\":\"saved.invalid\",\"Port\":0}");
        await Assert.ThrowsAsync<InvalidDataException>(() => ConnectionSettingsStore.RemoteEndpointAsync(input.Project));
        File.WriteAllText(input.Project.RemotePath, "{\"ServerName\":\"saved.invalid\",\"Port\":19011}");
        ready = await ObservationPreparation.PrepareAsync(input.Project, input.Target, input.Elf, input.Dll,
            _ => Task.FromResult(new[] { value }), transport: "remote");
        Assert.Equal("saved.invalid", ready.Request.Host);
        Assert.Equal(before, File.ReadAllBytes(path));
    }
    [Fact]
    public async Task StopWaitsForStartAndDuplicateStartCannotCreateAnotherSession()
    {
        var session = new FakeSession { StartGate = new(TaskCreationOptions.RunContinuationsAsynchronously) };
        var created = 0;
        var coordinator = new ObservationCoordinator(() => { created++; return session; });
        var start = coordinator.StartAsync("unused", Request);
        await Assert.ThrowsAsync<InvalidOperationException>(() => coordinator.StartAsync("unused", Request));
        var stop = coordinator.StopAsync(); Assert.False(stop.IsCompleted); Assert.False(session.Disposed);
        session.StartGate.SetResult(); await start; await stop;
        Assert.Equal(1, created); Assert.True(session.Disposed); Assert.False(coordinator.HasSession);
    }
    [Fact]
    public async Task FailedReleaseRetainsOwnershipAndPreventsRetryUntilReleaseSucceeds()
    {
        var old = new FakeSession { FailRelease = true };
        var next = new FakeSession(); var created = 0;
        var coordinator = new ObservationCoordinator(() => ++created == 1 ? old : next);
        await coordinator.StartAsync("unused", Request);
        await Assert.ThrowsAsync<IOException>(coordinator.StopAsync);
        Assert.True(coordinator.HasSession); Assert.True(coordinator.CanRetry);
        await Assert.ThrowsAsync<IOException>(() => coordinator.StartAsync("unused", Request)); Assert.Equal(1, created);
        old.FailRelease = false;
        await coordinator.StartAsync("unused", Request);
        Assert.True(old.Disposed); Assert.Equal(2, created); Assert.False(coordinator.CanRetry);
        await coordinator.StopAsync();
    }
    [Fact]
    public async Task FailedStartAlsoRetainsSessionIfItsCleanupFails()
    {
        var session = new FakeSession { FailStart = true, FailRelease = true };
        var coordinator = new ObservationCoordinator(() => session);
        await Assert.ThrowsAsync<IOException>(() => coordinator.StartAsync("unused", Request));
        Assert.True(coordinator.HasSession); Assert.True(coordinator.CanRetry);
        session.FailRelease = false; await coordinator.StopAsync(); Assert.False(coordinator.HasSession);
    }
    [Fact]
    public async Task UpdateAndStopAreOrderedAndOldGenerationEventsAreIgnored()
    {
        var session = new FakeSession { UpdateGate = new(TaskCreationOptions.RunContinuationsAsynchronously) };
        var coordinator = new ObservationCoordinator(() => session); await coordinator.StartAsync("unused", Request);
        var update = coordinator.UpdateAsync([value], 75);
        var stop = coordinator.StopAsync(); Assert.False(stop.IsCompleted);
        session.UpdateGate.SetResult(); await update; await stop; Assert.True(session.Disposed);
        var next = new FakeSession(); coordinator = new(() => next); await coordinator.StartAsync("unused", Request);
        next.Queue.Writer.TryWrite(new("sample", "old source", SessionId: "old"));
        next.Queue.Writer.TryWrite(new("sample", "old generation", SessionId: next.SessionId, Generation: 0));
        next.Queue.Writer.TryWrite(new("sample", "current", SessionId: next.SessionId));
        Assert.True(coordinator.TryRead(out var item)); Assert.Null(item);
        Assert.True(coordinator.TryRead(out item)); Assert.Null(item);
        Assert.True(coordinator.TryRead(out item)); Assert.Equal("current", item!.Message);
        next.Faulted = true;
        next.Queue.Writer.TryWrite(new("sample", "queued before fault", SessionId: next.SessionId));
        Assert.False(coordinator.TryRead(out item)); Assert.Null(item);
        await coordinator.StopAsync();
    }
    [Fact]
    public void PixelSamplingPreservesSpikesAndChronologicalEndpoints()
    {
        var data = new double[2000]; data[1001] = 100; data[1002] = -100;
        var indices = PlotSampling.Indices(data, 100);
        Assert.Contains(1001, indices); Assert.Contains(1002, indices);
        Assert.Equal(0, indices[0]); Assert.Equal(1999, indices[^1]); Assert.True(indices.Length <= 202);
        Assert.Equal(indices.Order().Distinct(), indices); Assert.Empty(PlotSampling.Indices([], 0));
    }
    private sealed class FakeSession : IObservationSession
    {
        public TaskCompletionSource? StartGate { get; init; }
        public TaskCompletionSource? UpdateGate { get; init; }
        public bool FailStart { get; init; }
        public bool FailRelease { get; set; }
        public bool Disposed { get; private set; }
        public long Generation { get; private set; } = 1;
        public string SessionId { get; } = Guid.NewGuid().ToString("N");
        public bool Faulted { get; set; }
        public bool CanRetry => Faulted;
        public long Dropped => 0;
        public string State => "fake";
        public Channel<ObservationEvent> Queue { get; } = Channel.CreateUnbounded<ObservationEvent>();
        public ChannelReader<ObservationEvent> Events => Queue.Reader;
        public async Task StartAsync(string worker, ObservationRequest request)
        { if (StartGate != null) await StartGate.Task; if (FailStart) throw new IOException("start failed"); }
        public async Task UpdateAsync(Variable[] variables, int sampleRate)
        { if (UpdateGate != null) await UpdateGate.Task; Generation++; }
        public ValueTask DisposeAsync()
        { if (FailRelease) throw new IOException("release failed"); Disposed = true; return ValueTask.CompletedTask; }
    }
}

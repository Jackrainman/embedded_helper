using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using EmbeddedHelper.Core;
using Xunit;

namespace EmbeddedHelper.Tests;

[Collection("Offline client")]
public sealed class ObservationTests : TestWorkspace
{
    [Fact]
    public async Task LegacyWatchSettingsRequireExplicitProbeAndNewFieldsRoundTripPerProject()
    {
        var legacy = JsonSerializer.Deserialize<WatchSettings>("{\"Target\":\"Debug\",\"Identities\":[\"global:value\"],\"Rate\":70}", Product.Json)!;
        Assert.Equal("local", legacy.Transport);
        Assert.Equal("", legacy.ProbeSerial);
        Assert.True(legacy.EnableRtt);
        var saved = legacy with { Transport = "remote", ProbeSerial = "123456", EnableRtt = false };
        WatchSettingsStore.Save(root, saved);
        var restored = (await WatchSettingsStore.LoadAsync(root))!;
        Assert.Equal(saved.Target, restored.Target);
        Assert.Equal(saved.Identities, restored.Identities);
        Assert.Equal(70, restored.Rate);
        Assert.Equal("remote", restored.Transport);
        Assert.Equal("123456", restored.ProbeSerial);
        Assert.False(restored.EnableRtt);
        Assert.Null(await WatchSettingsStore.LoadAsync(Path.Combine(root, "another-project")));
    }

    [Fact]
    public async Task MissingSdkAndInvalidSerialSettingsFailBeforeDeviceAccess()
    {
        await using var session = new ObservationSession();
        var request = new ObservationRequest(Path.Combine(root, "missing.dll"), 1, "", 19011, "STM32G474VE", "SWD", 8000, 100, false, []);
        await Assert.ThrowsAsync<FileNotFoundException>(() => session.StartAsync("not-started.exe", request));
        Assert.Equal("未连接", session.State);
        await using var serial = new SerialSession();
        await Assert.ThrowsAsync<ArgumentException>(() => serial.OpenAsync(new SerialSettings("not-a-port", -1), null));
        Assert.False(serial.Connected);
    }
    [Fact]
    public void ReadPlanBoundaryRejectsOldGenerationAndPreservesPendingValidPlan()
    {
        var variable = new Variable("global:value", "value", 0x20000000, "int32", 4, "可读取");
        var initial = new ReadPlan(1, 100, [variable]); initial.Validate();
        var mailbox = new ReadPlanMailbox(initial);
        mailbox.Submit(new ReadPlan(2, 70, [variable with { Address = 0x20000004 }]));
        Assert.Throws<InvalidDataException>(() => mailbox.Submit(new ReadPlan(3, 70, [variable with { Address = uint.MaxValue }])));
        Assert.Throws<InvalidDataException>(() => mailbox.Submit(new ReadPlan(2, 70, [variable])));
        var next = mailbox.Take()!;
        Assert.Equal(2, next.Generation); Assert.Equal(0x20000004UL, next.Variables[0].Address); Assert.Null(mailbox.Take());
        Assert.Throws<InvalidDataException>(() => new ReadPlan(3, 100, [variable, variable]).Validate());
        Assert.Throws<InvalidDataException>(() => new ReadPlan(3, 100, [variable with { Size = 1 }]).Validate());
    }
    [Fact]
    public async Task ActualWorkerRejectsInvalidLibraryAndIsReapedBeforeRetry()
    {
        var worker = Path.Combine(AppContext.BaseDirectory, "worker", "EmbeddedHelper.Worker.exe");
        Assert.True(File.Exists(worker), "Worker output was not prepared by the test build.");
        var invalidLibrary = Path.Combine(root, "not-a-native-library.dll");
        File.WriteAllText(invalidLibrary, "This is deliberately invalid; no SEGGER library or hardware is used.");
        var request = new ObservationRequest(invalidLibrary, 1, "", 19011, "STM32G474VE", "SWD", 8000, 100, false, []);
        await using var session = new ObservationSession();
        await Assert.ThrowsAnyAsync<Exception>(() => session.StartAsync(worker, request));
        Assert.True(session.CanRetry);
        await session.DisposeAsync();
        Assert.Equal("已释放", session.State);
        await using var retry = new ObservationSession();
        await Assert.ThrowsAnyAsync<Exception>(() => retry.StartAsync(worker, request));
        await retry.DisposeAsync();
        Assert.NotEqual(session.SessionId, retry.SessionId);
        Assert.Equal("已释放", retry.State);
    }

    [Fact]
    public async Task ActualWorkerChineseErrorSurvivesLegacyParentEncoding()
    {
        var worker = Path.Combine(AppContext.BaseDirectory, "worker", "EmbeddedHelper.Worker.exe");
        var invalidLibrary = Path.Combine(root, "not-loaded.dll");
        File.WriteAllText(invalidLibrary, "Rejected before loading; no device access.");
        var request = new ObservationRequest(Path.GetRelativePath(Environment.CurrentDirectory, invalidLibrary),
            1, "", 19011, "STM32G474VE", "SWD", 8000, 100, false, []);
        Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);
        var previousEncoding = Console.OutputEncoding;
        await using var session = new ObservationSession();
        try
        {
            Console.OutputEncoding = Encoding.GetEncoding(936);
            var error = await Assert.ThrowsAsync<IOException>(() => session.StartAsync(worker, request));
            Assert.Equal("采集失败：SDK 必须为绝对路径。", error.Message);
        }
        finally
        {
            Console.OutputEncoding = previousEncoding;
            await session.DisposeAsync();
        }
        Assert.Equal("已释放", session.State);
    }
}

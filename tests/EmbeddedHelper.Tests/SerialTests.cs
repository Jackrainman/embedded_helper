using System.Text.Json;
using System.Text.Json.Nodes;
using EmbeddedHelper.Core;
using Xunit;

namespace EmbeddedHelper.Tests;

[Collection("Offline client")]
public sealed class SerialTests : TestWorkspace
{
    [Fact]
    public async Task BidirectionalRecordingKeepsRawBytesAndSegmentSequence()
    {
        var output = Path.Combine(root, "record");
        var bytes = Enumerable.Range(0, 256).Select(i => (byte)i).ToArray();
        var recorder = new OrderedRecorder(output, 500);
        await using (recorder)
        {
            await Task.WhenAll(Task.Run(() => { for (int i = 0; i < 30; i++) recorder.Record("TX", bytes); }), Task.Run(() => { for (int i = 0; i < 30; i++) recorder.Record("RX", bytes); }));
            Assert.Equal(0, recorder.Discarded);
        }
        Assert.Null(recorder.Failure);
        var lines = Directory.GetFiles(output, "*.jsonl").Order().SelectMany(File.ReadAllLines).Select(line => JsonDocument.Parse(line)).ToArray();
        Assert.Equal(60, lines.Length);
        Assert.Equal(Enumerable.Range(1, 60).Select(i => (long)i), lines.Select(d => d.RootElement.GetProperty("sequence").GetInt64()));
        Assert.Equal(30, lines.Count(d => d.RootElement.GetProperty("direction").GetString() == "TX"));
        foreach (var line in lines) { Assert.Equal(bytes, Convert.FromBase64String(line.RootElement.GetProperty("data").GetString()!)); Assert.False(line.RootElement.TryGetProperty("timestamp", out _)); line.Dispose(); }
        Assert.True(Directory.GetFiles(output, "*.index.json").Length > 1);
    }
    [Fact]
    public void SerialHexValidationAndPortableSettingsRoundtrip()
    {
        Assert.Equal(new byte[] { 0, 255, 10 }, SerialSession.ParseSend("00 FF 0A", true, "\n"));
        Assert.Throws<FormatException>(() => SerialSession.ParseSend("zz", true, ""));
        var data = new PortableData(root); data.Save("settings/check.json", new { value = "中文" });
        Assert.Equal("中文", data.Load("settings/check.json", () => new Dictionary<string, string>())["value"]);
    }
    [Fact]
    public async Task RecordingReportsInteriorAndTrailingByteBudgetGaps()
    {
        var output = Path.Combine(root, "bounded-record");
        var recorder = new OrderedRecorder(output, 1024, 4);
        recorder.Record("RX", [1, 2, 3, 4, 5]);
        recorder.Record("TX", [255, 0]);
        recorder.Record("RX", [6, 7, 8, 9, 10]);
        await recorder.DisposeAsync();
        Assert.Equal(2, recorder.Discarded); Assert.Equal(10, recorder.DiscardedBytes);
        Assert.InRange(recorder.PeakBytes, 0, 4); Assert.Equal(0, recorder.QueuedBytes);
        using var line = JsonDocument.Parse(Assert.Single(File.ReadAllLines(Assert.Single(Directory.GetFiles(output, "*.jsonl")))));
        Assert.Equal(2, line.RootElement.GetProperty("sequence").GetInt64());
        Assert.Equal(1, line.RootElement.GetProperty("gapFrom").GetInt64());
        Assert.Equal(1, line.RootElement.GetProperty("gapTo").GetInt64());
        using var summary = JsonDocument.Parse(File.ReadAllText(Assert.Single(Directory.GetFiles(output, "*.summary.json"))));
        Assert.Equal(3, summary.RootElement.GetProperty("trailingGapFrom").GetInt64());
        Assert.Equal(3, summary.RootElement.GetProperty("trailingGapTo").GetInt64());
        Assert.Throws<ObjectDisposedException>(() => recorder.Record("TX", [1]));
    }
    [Fact]
    public void SerialDisplayDecodesAcrossChunksAndBoundsPausedHistory()
    {
        var display = new SerialTextBuffer(40);
        display.Append(new DataBlock(1, "RX", [0xe4]));
        display.Append(new DataBlock(2, "TX", [65]));
        display.Append(new DataBlock(3, "RX", [0xb8, 0xad]));
        Assert.Equal("[TX] A[RX] 中", display.Text);
        for (int i = 0; i < 50; i++) display.Append(new DataBlock(i + 4, "RX", [66]));
        Assert.InRange(display.Text.Length, 1, 40); Assert.True(display.TrimmedCharacters > 0);
        display.Configure("gb18030", false);
        var bytes = SerialSession.ParseSend("中文", false, "\r\n", SerialTextBuffer.GetEncoding("gb18030"));
        display.Append(new DataBlock(55, "TX", bytes));
        Assert.Equal("[TX] 中文\r\n", display.Text);
        Assert.Throws<System.Text.EncoderFallbackException>(() => SerialSession.ParseSend("中文", false, "", SerialTextBuffer.GetEncoding("us-ascii")));
    }
    [Fact]
    public async Task SerialDisposedSessionRejectsReopenWithoutTouchingAPort()
    {
        var session = new SerialSession();
        await Task.WhenAll(session.DisposeAsync().AsTask(), session.DisposeAsync().AsTask());
        await Assert.ThrowsAsync<ObjectDisposedException>(() => session.OpenAsync(new SerialSettings("not-accessed"), null));
        Assert.False(session.Connected);
    }
    [Fact]
    public async Task RecordingReportsActualFileCreationFailure()
    {
        var output = Path.Combine(root, "removed-record-directory");
        var recorder = new OrderedRecorder(output);
        Directory.Move(output, output + "-moved");
        recorder.Record("RX", [1, 2, 3]);
        await recorder.DisposeAsync();
        Assert.NotNull(recorder.Error);
    }
}

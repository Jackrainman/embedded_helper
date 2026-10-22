using System.Diagnostics;
using System.IO;
using System.Threading.Channels;
using System.Windows.Threading;
using EmbeddedHelper.Core;

namespace EmbeddedHelper.App;

public partial class MainWindow
{
    // Opt-in measurement of the real display consumer with bounded synthetic input, never a device session.
    private async Task SmokeObservationBenchmarkAsync(string directory)
    {
        var results = new List<object>();
        foreach (var variables in new[] { 16, 3000 })
            foreach (var requested in new[] { 200, 1000 })
                foreach (var slow in new[] { false, true })
                {
                    var fake = new BenchmarkSession(slow);
                    var coordinator = new ObservationCoordinator(() => fake);
                    var window = new MainWindow(coordinator);
                    try
                    {
                        window.refresh.Stop(); window.Pages.SelectedIndex = 1; window.Show();
                        window.Variables.Clear();
                        for (var i = 0; i < variables; i++) window.Variables.Add(new(new("global:v" + i, "v" + i, (ulong)(0x20000000 + 4 * i), "int32", 4, "可读取")) { Selected = i < 16 });
                        window.ProjectPage.ElfPath.Text = Path.Combine(directory, "scalars.elf");
                        window.elfStamp = File.GetLastWriteTimeUtc(window.ProjectPage.ElfPath.Text);
                        await coordinator.StartAsync("unused", new("unused", 1, "", 1, "offline", "SWD", 1, requested, false, []));
                        var times = new List<double>(); var allocations = new List<long>(); var ui = new List<double>();
                        var gc = new[] { GC.CollectionCount(0), GC.CollectionCount(1), GC.CollectionCount(2) };
                        var total = Stopwatch.StartNew(); var produced = 0;
                        for (var tick = 0; tick < 12; tick++)
                        {
                            await Task.Delay(100);
                            var due = (int)(total.Elapsed.TotalSeconds * requested);
                            for (; produced < due; produced++) fake.Enqueue(new("sample", "", Enumerable.Range(0, 16).ToDictionary(i => "global:v" + i, i => (produced + i).ToString(System.Globalization.CultureInfo.InvariantCulture)), Sequence: produced + 1, SessionId: fake.SessionId, ReadEndTicks: Stopwatch.GetTimestamp(), ObservedAt: DateTimeOffset.UtcNow));
                            var allocated = GC.GetAllocatedBytesForCurrentThread(); var start = Stopwatch.GetTimestamp();
                            window.RefreshObservation();
                            times.Add(Stopwatch.GetElapsedTime(start).TotalMilliseconds); allocations.Add(GC.GetAllocatedBytesForCurrentThread() - allocated);
                            start = Stopwatch.GetTimestamp(); window.UpdateLayout(); await Dispatcher.Yield(DispatcherPriority.Background);
                            ui.Add(Stopwatch.GetElapsedTime(start).TotalMilliseconds);
                        }
                        times.Sort(); var metadata = Stopwatch.StartNew();
                        for (var i = 0; i < 1000; i++) _ = File.GetLastWriteTimeUtc(window.ProjectPage.ElfPath.Text);
                        results.Add(new
                        {
                            variables,
                            selected = 16,
                            requestedHz = requested,
                            slowReadMilliseconds = slow ? 2 : 0,
                            produced,
                            consumed = window.samples,
                            dropped = fake.Dropped,
                            elapsedSeconds = total.Elapsed.TotalSeconds,
                            consumedHz = window.samples / total.Elapsed.TotalSeconds,
                            tickMedianMs = times[times.Count / 2],
                            tickP95Ms = times[(int)Math.Ceiling(times.Count * .95) - 1],
                            tickMaxMs = times[^1],
                            tickMeanAllocatedBytes = allocations.Average(),
                            gcDelta = Enumerable.Range(0, 3).Select(i => GC.CollectionCount(i) - gc[i]).ToArray(),
                            uiYieldMaxMs = ui.Max(),
                            localMetadata1000Ms = metadata.Elapsed.TotalMilliseconds,
                            note = "100ms调度；8ms只约束队列消费，不承诺整轮或实际硬件1000Hz。慢路径为受控读取延迟；ELF元数据为本地文件。"
                        });
                    }
                    finally { await coordinator.StopAsync(); window.refresh.Stop(); window.closing = true; window.Close(); }
                }
        PortableData.SaveFile(Path.Combine(directory, "observation-benchmark.json"), results);
    }
    private sealed class BenchmarkSession(bool slow) : IObservationSession
    {
        private readonly Channel<ObservationEvent> queue = Channel.CreateBounded<ObservationEvent>(512);
        private long dropped;
        private ChannelReader<ObservationEvent>? reader;
        public long Generation => 1;
        public string SessionId => "offline-benchmark";
        public bool CanRetry => false;
        public long Dropped => dropped;
        public string State => "离线合成测量";
        public ChannelReader<ObservationEvent> Events => reader ??= slow ? new DelayedReader(queue.Reader, true) : queue.Reader;
        public void Enqueue(ObservationEvent item) { if (!queue.Writer.TryWrite(item)) dropped++; }
        public Task StartAsync(string worker, ObservationRequest request) => Task.CompletedTask;
        public Task UpdateAsync(Variable[] variables, int sampleRate) => Task.CompletedTask;
        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
        private sealed class DelayedReader(ChannelReader<ObservationEvent> source, bool slow) : ChannelReader<ObservationEvent>
        {
            public override bool TryRead(out ObservationEvent item) { if (slow) Thread.Sleep(2); return source.TryRead(out item!); }
            public override ValueTask<bool> WaitToReadAsync(CancellationToken cancellationToken = default) => source.WaitToReadAsync(cancellationToken);
        }
    }
}

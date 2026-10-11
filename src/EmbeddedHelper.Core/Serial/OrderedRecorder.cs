using System.IO.Ports;
using System.Text;
using System.Text.Json;
using System.Threading.Channels;

namespace EmbeddedHelper.Core;

public sealed class OrderedRecorder : IAsyncDisposable
{
    private sealed record RecordBlock(DataBlock Data, long? GapFrom, long? GapTo);
    private readonly Channel<RecordBlock> queue = Channel.CreateBounded<RecordBlock>(new BoundedChannelOptions(2048) { FullMode = BoundedChannelFullMode.Wait, SingleReader = true });
    private readonly object sync = new();
    private readonly Task writer;
    private long sequence;
    private long discarded;
    private long queuedBytes, peakBytes, discardedBytes;
    private long? gapFrom, gapTo;
    private bool completed;
    private readonly long byteCapacity;
    private Exception? failure;
    public long Discarded => Interlocked.Read(ref discarded);
    public long QueuedBytes => Interlocked.Read(ref queuedBytes);
    public long PeakBytes => Interlocked.Read(ref peakBytes);
    public long DiscardedBytes => Interlocked.Read(ref discardedBytes);
    public string? Error => failure?.Message;
    public Exception? Failure => failure;
    public OrderedRecorder(string directory, int segmentBytes = 16 * 1024 * 1024, int byteCapacity = 8 * 1024 * 1024)
    {
        if (segmentBytes < 1 || byteCapacity < 1) throw new ArgumentOutOfRangeException(nameof(segmentBytes));
        this.byteCapacity = byteCapacity;
        Directory.CreateDirectory(directory);
        writer = Task.Run(async () =>
        {
            var session = Guid.NewGuid().ToString("N");
            var segment = 0;
            long first = 0, last = 0, bytes = 0;
            StreamWriter? output = null;
            string? file = null;
            void CloseSegment(string reason)
            {
                if (output == null) return;
                output.Dispose();
                output = null;
                PortableData.SaveFile(file + ".index.json", new { sessionId = session, segment, fileName = Path.GetFileName(file), firstSequence = first, lastSequence = last, reason, discarded = Discarded });
            }
            try
            {
                await foreach (var record in queue.Reader.ReadAllAsync())
                {
                    var item = record.Data;
                    Interlocked.Add(ref queuedBytes, -item.Bytes.Length);
                    if (output == null)
                    {
                        file = Path.Combine(directory, $"{session}-{++segment:D4}.jsonl");
                        output = new StreamWriter(new FileStream(file, FileMode.CreateNew, FileAccess.Write, FileShare.Read), new UTF8Encoding(false));
                        first = item.Sequence;
                        bytes = 0;
                        PortableData.SaveFile(file + ".index.json", new { sessionId = session, segment, firstSequence = first, reason = "未完成" });
                    }
                    var line = JsonSerializer.Serialize(new { sessionId = session, sequence = item.Sequence, direction = item.Direction, length = item.Bytes.Length, data = Convert.ToBase64String(item.Bytes), gapFrom = record.GapFrom, gapTo = record.GapTo });
                    await output.WriteLineAsync(line);
                    bytes += Encoding.UTF8.GetByteCount(line) + 1;
                    last = item.Sequence;
                    if (bytes >= segmentBytes) CloseSegment("容量分段");
                }
                CloseSegment(Discarded == 0 ? "正常停止" : "队列有缺口");
                PortableData.SaveFile(Path.Combine(directory, session + ".summary.json"), new { sessionId = session, status = "closed", lastSequence = sequence, discarded = Discarded, discardedBytes = DiscardedBytes, trailingGapFrom = gapFrom, trailingGapTo = gapTo, peakQueuedBytes = PeakBytes });
            }
            catch (Exception ex) { failure = ex; output?.Dispose(); queue.Writer.TryComplete(ex); }
        });
    }
    public void Record(string direction, ReadOnlySpan<byte> data)
    {
        lock (sync)
        {
            if (completed) throw new ObjectDisposedException(nameof(OrderedRecorder));
            if (direction is not ("TX" or "RX") || data.Length == 0) throw new ArgumentException("记录须为非空 TX/RX 字节块。");
            var current = ++sequence;
            if (data.Length <= 65536 && QueuedBytes + data.Length <= byteCapacity)
            {
                var size = Interlocked.Add(ref queuedBytes, data.Length);
                if (queue.Writer.TryWrite(new RecordBlock(new DataBlock(current, direction, data.ToArray()), gapFrom, gapTo)))
                {
                    Interlocked.Exchange(ref peakBytes, Math.Max(peakBytes, size)); gapFrom = gapTo = null; return;
                }
                Interlocked.Add(ref queuedBytes, -data.Length);
            }
            gapFrom ??= current; gapTo = current;
            Interlocked.Increment(ref discarded); Interlocked.Add(ref discardedBytes, data.Length);
        }
    }
    public async ValueTask DisposeAsync() { lock (sync) { completed = true; queue.Writer.TryComplete(); } await writer; }
}

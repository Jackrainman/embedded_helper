using System.IO.Ports;
using System.Text;
using System.Text.Json;
using System.Threading.Channels;

namespace EmbeddedHelper.Core;

public sealed record SerialSettings(string Port = "", int Baud = 115200, int DataBits = 8, Parity Parity = Parity.None, StopBits StopBits = StopBits.One, Handshake FlowControl = Handshake.None);
public sealed record DataBlock(long Sequence, string Direction, byte[] Bytes);

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

public sealed class SerialSession : IAsyncDisposable
{
    private SerialPort? port;
    private CancellationTokenSource? cancellation;
    private Task? receiver;
    private readonly SemaphoreSlim lifecycle = new(1, 1);
    private readonly SemaphoreSlim sending = new(1, 1);
    private readonly Channel<DataBlock> display = Channel.CreateBounded<DataBlock>(512);
    private long sequence;
    private long received, sent, clipped;
    private long driverErrors;
    private bool disposed, stopping;
    public ChannelReader<DataBlock> Display => display.Reader;
    public long Received => Interlocked.Read(ref received);
    public long Sent => Interlocked.Read(ref sent);
    public long Clipped => Interlocked.Read(ref clipped);
    public long DriverErrors => Interlocked.Read(ref driverErrors);
    public string LastDriverError { get; private set; } = "未收到驱动错误通知；不代表链路零误码";
    public string? Error { get; private set; }
    public OrderedRecorder? Recorder { get; private set; }
    public bool Connected => !stopping && port?.IsOpen == true && Error == null;

    public async Task OpenAsync(SerialSettings settings, string? recordingDirectory)
    {
        await lifecycle.WaitAsync();
        try
        {
            ObjectDisposedException.ThrowIf(disposed, this);
            if (port != null) throw new InvalidOperationException("请先关闭当前串口。");
            if (settings.Baud < 1 || settings.Baud > 5000000 || settings.DataBits is < 5 or > 8 || !Enum.IsDefined(settings.Parity) || !Enum.IsDefined(settings.FlowControl) || settings.StopBits is not (StopBits.One or StopBits.Two or StopBits.OnePointFive)) throw new ArgumentException("串口参数超出范围。");
            var candidate = new SerialPort(settings.Port, settings.Baud, settings.Parity, settings.DataBits, settings.StopBits)
            { Handshake = settings.FlowControl, ReadTimeout = 200, WriteTimeout = 1500, ReadBufferSize = 1024 * 1024 };
            candidate.ErrorReceived += (_, e) => { Interlocked.Increment(ref driverErrors); LastDriverError = e.EventType.ToString(); };
            try
            {
                await Task.Run(candidate.Open);
                if (recordingDirectory != null) Recorder = new OrderedRecorder(recordingDirectory);
                port = candidate;
                cancellation = new CancellationTokenSource();
                var token = cancellation.Token;
                receiver = Task.Run(() =>
                {
                    var buffer = new byte[16384];
                    while (!token.IsCancellationRequested)
                    {
                        try
                        {
                            var count = candidate.Read(buffer, 0, buffer.Length);
                            if (count > 0) { Interlocked.Add(ref received, count); Publish("RX", buffer.AsSpan(0, count)); }
                        }
                        catch (TimeoutException) { }
                        catch (Exception ex) { if (!token.IsCancellationRequested) Error = ex.Message; break; }
                    }
                });
            }
            catch { candidate.Dispose(); if (Recorder != null) await Recorder.DisposeAsync(); Recorder = null; throw; }
        }
        finally { lifecycle.Release(); }
    }
    private void Publish(string direction, ReadOnlySpan<byte> bytes)
    {
        Recorder?.Record(direction, bytes);
        if (!display.Writer.TryWrite(new DataBlock(Interlocked.Increment(ref sequence), direction, bytes.ToArray()))) Interlocked.Add(ref clipped, bytes.Length);
    }
    public async Task SendAsync(byte[] bytes, CancellationToken token)
    {
        if (bytes.Length > 65536) throw new ArgumentException("单次发送最多 64 KiB。");
        await sending.WaitAsync(token);
        try
        {
            if (!Connected || port == null) throw new IOException("串口未连接。");
            var current = port;
            await Task.Run(() => current.Write(bytes, 0, bytes.Length), token);
            Interlocked.Add(ref sent, bytes.Length);
            Publish("TX", bytes);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            Error = "发送失败，驱动已接收的长度不确定，请勿盲目重发：" + ex.Message;
            throw new IOException(Error, ex);
        }
        finally { sending.Release(); }
    }
    public async ValueTask DisposeAsync()
    {
        await lifecycle.WaitAsync();
        try
        {
            if (disposed) return;
            stopping = true;
            cancellation?.Cancel();
            if (receiver != null) await receiver;
            await sending.WaitAsync();
            try { port?.Dispose(); port = null; }
            finally { sending.Release(); }
            if (Recorder != null) await Recorder.DisposeAsync();
            cancellation?.Dispose();
            display.Writer.TryComplete();
            disposed = true;
        }
        finally { lifecycle.Release(); }
    }
    public static byte[] ParseSend(string text, bool hex, string newline, Encoding? encoding = null) => hex
        ? Convert.FromHexString(string.Concat(text.Where(c => !char.IsWhiteSpace(c))))
        : (encoding ?? Encoding.UTF8).GetBytes(text + newline);
}

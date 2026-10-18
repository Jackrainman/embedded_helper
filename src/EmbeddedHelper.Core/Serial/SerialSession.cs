using System.IO.Ports;
using System.Text;
using System.Text.Json;
using System.Threading.Channels;

namespace EmbeddedHelper.Core;

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
    public long PublishedSequence => Interlocked.Read(ref sequence);
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
    public static byte[] ParseSend(string text, bool hex, string newline, Encoding? encoding = null)
    {
        if (!hex) return (encoding ?? Encoding.UTF8).GetBytes(text + newline);
        var compact = string.Concat(text.Where(c => !char.IsWhiteSpace(c)));
        try { return Convert.FromHexString(compact); }
        catch (FormatException ex)
        {
            var invalid = compact.FirstOrDefault(c => !Uri.IsHexDigit(c));
            throw new FormatException(invalid != default
                ? $"HEX 内容包含非十六进制字符“{invalid}”。请使用 0–9、A–F，例如 45 48 0D 0A。"
                : "HEX 内容必须每两个字符组成一个字节，例如 0A；请补全最后一个字节。", ex);
        }
    }
}

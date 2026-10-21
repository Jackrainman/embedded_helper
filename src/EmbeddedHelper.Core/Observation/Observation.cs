using System.Diagnostics;
using System.Text;
using System.Text.Json;
using System.Threading.Channels;

namespace EmbeddedHelper.Core;

public sealed record ObservationRequest(string Dll, int ProbeSerial, string Host, int Port, string Device, string Interface, int SpeedKHz, int SampleRate, bool Rtt, Variable[] Variables, string SessionId = "", long Generation = 1, string? RecordingPath = null);
public sealed record ObservationEvent(string Kind, string Message, Dictionary<string, string>? Values = null, string? RttBase64 = null, long Sequence = 0, string SessionId = "", long Generation = 1, long ReadStartTicks = 0, long ReadEndTicks = 0, DateTimeOffset? ObservedAt = null);

public sealed class ObservationSession : IObservationSession
{
    private Process? process;
    private Task? reader;
    private Task<string>? errors;
    private readonly SemaphoreSlim control = new(1, 1);
    private TaskCompletionSource<long>? acknowledgement;
    private readonly CancellationTokenSource lifetime = new();
    private Task? watchdog;
    private long lastEvent;
    private bool faulted, stopping;
    private ObservationRecording? recording;
    public long Generation { get; private set; } = 1;
    public string SessionId { get; } = Guid.NewGuid().ToString("N");
    public bool CanRetry => faulted;
    private readonly Channel<ObservationEvent> events = Channel.CreateBounded<ObservationEvent>(512);
    private long dropped;
    public long Dropped => Interlocked.Read(ref dropped);
    public string State { get; private set; } = "未连接";
    public ChannelReader<ObservationEvent> Events => events.Reader;
    public async Task StartAsync(string worker, ObservationRequest request)
    {
        ObjectDisposedException.ThrowIf(stopping, this);
        if (process != null) throw new InvalidOperationException("旧采集进程尚未释放。");
        if (!File.Exists(request.Dll)) throw new FileNotFoundException("未找到 SEGGER JLink_x64.dll，请在设置中选择外部安装的 64 位库。", request.Dll);
        new ReadPlan(Generation, request.SampleRate, request.Variables).Validate();
        if (request.Host.Length == 0 && request.ProbeSerial <= 0) throw new ArgumentException("请输入具体探针序列号，不自动选择其他探针。");
        var start = new ProcessStartInfo(worker)
        {
            UseShellExecute = false,
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            StandardOutputEncoding = Encoding.UTF8,
            StandardErrorEncoding = Encoding.UTF8,
            CreateNoWindow = true
        };
        start.ArgumentList.Add("--observe");
        if (request.RecordingPath is { } path) recording = new(path, request.Variables, request.SampleRate, SessionId, Generation);
        try { process = Process.Start(start) ?? throw new IOException("无法启动采集进程。"); }
        catch { recording?.Dispose(); recording = null; throw; }
        State = "连接中";
        var child = process;
        request = request with { SessionId = SessionId, Generation = Generation };
        acknowledgement = new(TaskCreationOptions.RunContinuationsAsynchronously);
        lastEvent = Stopwatch.GetTimestamp();
        errors = child.StandardError.ReadToEndAsync();
        await child.StandardInput.WriteLineAsync(JsonSerializer.Serialize(request));
        await child.StandardInput.FlushAsync();
        reader = Task.Run(async () =>
        {
            var recordingFailed = false;
            try
            {
                while (await child.StandardOutput.ReadLineAsync() is { } line)
                {
                    if (line.Length > 1024 * 1024) throw new InvalidDataException("采集进程输出超出协议上限。");
                    var item = JsonSerializer.Deserialize<ObservationEvent>(line) ?? throw new InvalidDataException("采集事件为空。");
                    if (item.SessionId != SessionId) throw new InvalidDataException("采集会话身份不匹配。");
                    recording?.Append(item);
                    Interlocked.Exchange(ref lastEvent, Stopwatch.GetTimestamp());
                    if (item.Kind == "plan") acknowledgement?.TrySetResult(item.Generation);
                    if (item.Kind is "state" or "error") State = item.Message;
                    if (!events.Writer.TryWrite(item)) Interlocked.Increment(ref dropped);
                }
                await child.WaitForExitAsync();
                if (!stopping)
                {
                    if (!faulted) State = child.ExitCode != 0 ? "采集失败：" + (await errors).Trim() : "采集进程已退出";
                    faulted = true;
                    acknowledgement?.TrySetException(new IOException(State));
                }
            }
            catch (Exception ex)
            {
                recordingFailed = true;
                State = "采集或数据记录失败：" + ex.Message; faulted = true; acknowledgement?.TrySetException(ex);
                // Stop producing samples when they can no longer be recorded; normal Stop still owns release.
                try { child.StandardInput.Close(); } catch (IOException) { }
            }
            finally
            {
                try { if (!recordingFailed) recording?.Complete(); }
                catch (IOException ex) { State = "数据记录保存失败：" + ex.Message; faulted = true; }
                finally
                {
                    try { recording?.Dispose(); }
                    catch (IOException ex) { State = "数据记录关闭失败：" + ex.Message; faulted = true; }
                }
            }
        });
        watchdog = Task.Run(async () =>
        {
            try
            {
                while (!lifetime.IsCancellationRequested)
                {
                    await Task.Delay(1000, lifetime.Token);
                    if (!stopping && Stopwatch.GetElapsedTime(Interlocked.Read(ref lastEvent)).TotalSeconds > 15)
                    {
                        State = "采集进程超时；请重试，重连前将释放旧进程。"; faulted = true;
                        acknowledgement?.TrySetException(new TimeoutException(State));
                        return;
                    }
                }
            }
            catch (OperationCanceledException) { }
        });
        if (await acknowledgement.Task.WaitAsync(TimeSpan.FromSeconds(16)) != Generation) throw new InvalidDataException("初始采集计划确认不匹配。");
    }
    public async Task UpdateAsync(Variable[] variables, int sampleRate)
    {
        var plan = new ReadPlan(Generation + 1, sampleRate, variables); plan.Validate();
        await control.WaitAsync();
        try
        {
            if (process is not { } child || stopping || faulted || child.HasExited) throw new IOException("采集连接不可用，请先重试。");
            acknowledgement = new(TaskCreationOptions.RunContinuationsAsynchronously);
            recording?.Plan(variables, sampleRate, plan.Generation);
            await child.StandardInput.WriteLineAsync(JsonSerializer.Serialize(plan)); await child.StandardInput.FlushAsync();
            if (await acknowledgement.Task.WaitAsync(TimeSpan.FromSeconds(5)) != plan.Generation) throw new InvalidDataException("计划更新确认不匹配。");
            Generation = plan.Generation;
        }
        catch { faulted = true; State = "计划更新未确认；请停止并重试，禁止继续显示旧地址值。"; throw; }
        finally { control.Release(); }
    }
    public async ValueTask DisposeAsync()
    {
        if (process is not { } child) return;
        stopping = true; lifetime.Cancel();
        State = "停止中";
        try
        {
            if (!child.HasExited)
            {
                child.StandardInput.Close();
                try { await child.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(3)); }
                catch (TimeoutException) { child.Kill(true); await child.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(5)); }
            }
            if (reader != null) await reader;
            else recording?.Dispose();
            if (watchdog != null) await watchdog;
            State = "已释放";
            child.Dispose();
            process = null;
        }
        catch { State = "释放未完成，禁止建立新连接"; throw; }
    }
}

using System.Diagnostics;
using System.Text.Json;
using System.Threading.Channels;

namespace EmbeddedHelper.Core;

public sealed record ObservationRequest(string Dll, int ProbeSerial, string Host, int Port, string Device, string Interface, int SpeedKHz, int SampleRate, bool Rtt, Variable[] Variables);
public sealed record ObservationEvent(string Kind, string Message, Dictionary<string, string>? Values = null, string? RttBase64 = null, long Sequence = 0);

public sealed class ObservationSession : IAsyncDisposable
{
    private Process? process;
    private Task? reader;
    private Task<string>? errors;
    private readonly Channel<ObservationEvent> events = Channel.CreateBounded<ObservationEvent>(512);
    private long dropped;
    public long Dropped => Interlocked.Read(ref dropped);
    public string State { get; private set; } = "未连接";
    public ChannelReader<ObservationEvent> Events => events.Reader;
    public async Task StartAsync(string worker, ObservationRequest request)
    {
        if (process != null) throw new InvalidOperationException("旧采集进程尚未释放。");
        if (!File.Exists(request.Dll)) throw new FileNotFoundException("未找到 SEGGER JLink_x64.dll，请在设置中选择外部安装的 64 位库。", request.Dll);
        if (request.SampleRate is < 1 or > 1000 || request.Variables.Length > 64) throw new ArgumentException("采样率应为 1～1000 Hz，最多 64 个变量。");
        if (request.Host.Length == 0 && request.ProbeSerial <= 0) throw new ArgumentException("请输入具体探针序列号，不自动选择其他探针。");
        var start = new ProcessStartInfo(worker) { UseShellExecute = false, RedirectStandardInput = true, RedirectStandardOutput = true, RedirectStandardError = true, CreateNoWindow = true };
        start.ArgumentList.Add("--observe");
        process = Process.Start(start) ?? throw new IOException("无法启动采集进程。");
        State = "连接中";
        var child = process;
        errors = child.StandardError.ReadToEndAsync();
        await child.StandardInput.WriteLineAsync(JsonSerializer.Serialize(request));
        await child.StandardInput.FlushAsync();
        reader = Task.Run(async () =>
        {
            try
            {
                while (await child.StandardOutput.ReadLineAsync() is { } line)
                {
                    if (line.Length > 1024 * 1024) throw new InvalidDataException("采集进程输出超出协议上限。");
                    var item = JsonSerializer.Deserialize<ObservationEvent>(line) ?? throw new InvalidDataException("采集事件为空。");
                    if (item.Kind is "state" or "error") State = item.Message;
                    if (!events.Writer.TryWrite(item)) Interlocked.Increment(ref dropped);
                }
                await child.WaitForExitAsync();
                if (child.ExitCode != 0) State = "采集失败：" + (await errors).Trim();
                else if (State != "已释放") State = "采集进程已退出";
            }
            catch (Exception ex) { State = "采集协议错误：" + ex.Message; }
        });
    }
    public async ValueTask DisposeAsync()
    {
        if (process is not { } child) return;
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
            State = "已释放";
            child.Dispose();
            process = null;
        }
        catch { State = "释放未完成，禁止建立新连接"; throw; }
    }
}

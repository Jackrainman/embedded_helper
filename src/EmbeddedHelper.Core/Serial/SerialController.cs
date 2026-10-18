namespace EmbeddedHelper.Core;

public sealed record SerialCloseResult(string? RecorderError, long Discarded, string? PeriodicError);

/// <summary>Owns the independent COM session and its periodic sender; never depends on a project or probe.</summary>
public sealed class SerialController
{
    private readonly SemaphoreSlim lifecycle = new(1, 1);
    private readonly PeriodicSender periodic = new();
    public SerialSession? Current { get; private set; }
    public bool IsOpening { get; private set; }
    public bool IsClosing { get; private set; }
    public bool IsPeriodic => periodic.IsRunning;
    public PeriodicSendStatus PeriodicStatus => periodic.Status;
    public event Action? Changed;

    public async Task OpenAsync(SerialSettings settings, string? records)
    {
        if (!await lifecycle.WaitAsync(0)) throw new InvalidOperationException("串口操作尚未完成。");
        try
        {
            if (Current != null) throw new InvalidOperationException("请先关闭当前串口。");
            IsOpening = true;
            Current = new SerialSession(); Changed?.Invoke();
            try { await Current.OpenAsync(settings, records); }
            catch { await Current.DisposeAsync(); Current = null; throw; }
        }
        finally { IsOpening = false; lifecycle.Release(); Changed?.Invoke(); }
    }
    public async Task<SerialCloseResult> CloseAsync()
    {
        await lifecycle.WaitAsync();
        IsClosing = true;
        try
        {
            Changed?.Invoke();
            string? periodicError = null;
            try { await periodic.StopAsync(); } catch (Exception ex) { periodicError = ex.Message; }
            var session = Current;
            if (session != null) await session.DisposeAsync();
            Current = null;
            return new(session?.Recorder?.Error, session?.Recorder?.Discarded ?? 0, periodicError);
        }
        finally { IsClosing = false; lifecycle.Release(); Changed?.Invoke(); }
    }
    public Task SendAsync(byte[] bytes, CancellationToken token)
    {
        if (IsOpening || IsClosing) throw new InvalidOperationException("串口正在切换状态。");
        return (Current ?? throw new InvalidOperationException("请先打开串口。")).SendAsync(bytes, token);
    }
    public async Task StartPeriodicAsync(Func<CancellationToken, Task> send, int milliseconds)
    {
        if (IsOpening || IsClosing || Current?.Connected != true) throw new InvalidOperationException("请先打开串口并等待连接完成。");
        try { var task = periodic.StartAsync(send, milliseconds); Changed?.Invoke(); await task; }
        finally { Changed?.Invoke(); }
    }
    public void StopPeriodic() => periodic.Cancel();
}

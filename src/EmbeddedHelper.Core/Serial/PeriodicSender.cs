namespace EmbeddedHelper.Core;

public enum PeriodicSendState { Idle, Running, Stopping, Stopped, Faulted }
public sealed record PeriodicSendStatus(PeriodicSendState State, int Milliseconds, long Count, string? Error);

/// <summary>Runs one send at a time. Stop waits until the current send has observed cancellation.</summary>
public sealed class PeriodicSender
{
    private readonly object sync = new();
    private CancellationTokenSource? cancellation;
    private Task? running;
    private PeriodicSendState state;
    private int interval;
    private long count;
    private string? error;
    public bool IsRunning { get { lock (sync) return cancellation != null; } }
    public PeriodicSendStatus Status { get { lock (sync) return new(state, interval, count, error); } }
    public Task StartAsync(Func<CancellationToken, Task> send, int milliseconds)
    {
        lock (sync)
        {
            if (IsRunning) throw new InvalidOperationException("周期发送已启动。");
            if (milliseconds < 10) throw new ArgumentException("周期不得小于 10 ms。");
            var source = new CancellationTokenSource();
            state = PeriodicSendState.Running; interval = milliseconds; count = 0; error = null;
            cancellation = source;
            running = RunAsync(source, send, milliseconds);
            return running;
        }
    }
    private async Task RunAsync(CancellationTokenSource source, Func<CancellationToken, Task> send, int milliseconds)
    {
        // Return ownership to StartAsync before a synchronously failing sender can finish the task.
        await Task.Yield();
        try
        {
            while (!source.IsCancellationRequested)
            {
                await send(source.Token);
                lock (sync) count++;
                await Task.Delay(milliseconds, source.Token);
            }
        }
        catch (OperationCanceledException) when (source.IsCancellationRequested) { }
        catch (Exception ex) { lock (sync) { state = PeriodicSendState.Faulted; error = ex.Message; } throw; }
        finally { lock (sync) { if (state != PeriodicSendState.Faulted) state = PeriodicSendState.Stopped; cancellation = null; source.Dispose(); } }
    }
    public void Cancel() { lock (sync) { if (cancellation != null) { state = PeriodicSendState.Stopping; cancellation.Cancel(); } } }
    public async Task StopAsync()
    {
        Task? task;
        lock (sync) { Cancel(); task = running; }
        if (task != null) await task;
    }
}

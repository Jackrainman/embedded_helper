namespace EmbeddedHelper.Core;

/// <summary>Runs one send at a time. Stop waits until the current send has observed cancellation.</summary>
public sealed class PeriodicSender
{
    private readonly object sync = new();
    private CancellationTokenSource? cancellation;
    private Task? running;
    public bool IsRunning { get { lock (sync) return cancellation != null; } }
    public Task StartAsync(Func<CancellationToken, Task> send, int milliseconds)
    {
        lock (sync)
        {
            if (IsRunning) throw new InvalidOperationException("周期发送已启动。");
            if (milliseconds < 10) throw new ArgumentException("周期不得小于 10 ms。");
            var source = new CancellationTokenSource();
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
                await Task.Delay(milliseconds, source.Token);
            }
        }
        catch (OperationCanceledException) when (source.IsCancellationRequested) { }
        finally { lock (sync) { cancellation = null; source.Dispose(); } }
    }
    public void Cancel() { lock (sync) cancellation?.Cancel(); }
    public async Task StopAsync()
    {
        Task? task;
        lock (sync) { cancellation?.Cancel(); task = running; }
        if (task != null) await task;
    }
}

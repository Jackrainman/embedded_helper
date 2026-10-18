using System.Threading.Channels;

namespace EmbeddedHelper.Core;

public interface IObservationSession : IAsyncDisposable
{
    long Generation { get; }
    string SessionId { get; }
    bool CanRetry { get; }
    long Dropped { get; }
    string State { get; }
    ChannelReader<ObservationEvent> Events { get; }
    Task StartAsync(string worker, ObservationRequest request);
    Task UpdateAsync(Variable[] variables, int sampleRate);
}

/// <summary>Owns one session. Stop waits for an in-flight start/update; failed release keeps ownership.</summary>
public sealed class ObservationCoordinator(Func<IObservationSession>? createSession = null)
{
    private readonly SemaphoreSlim operations = new(1, 1);
    private readonly Func<IObservationSession> create = createSession ?? (() => new ObservationSession());
    private IObservationSession? current;
    private bool releaseFailed;
    public bool IsBusy { get; private set; }
    public bool HasSession => current != null;
    public bool CanRetry => current != null && (releaseFailed || current.CanRetry);
    public long Generation => current?.Generation ?? 0;
    public long Dropped => current?.Dropped ?? 0;
    public string State => releaseFailed ? "释放未完成，禁止建立新连接" : current?.State ?? "已释放";
    public event Action? Changed;

    public async Task StartAsync(string worker, ObservationRequest request)
    {
        if (!await operations.WaitAsync(0)) throw new InvalidOperationException("采集操作尚未完成，请稍候。");
        IsBusy = true;
        try
        {
            Changed?.Invoke();
            if (current != null)
            {
                if (!CanRetry) throw new InvalidOperationException("请先停止已有采集。");
                await ReleaseAsync();
            }
            current = create();
            Changed?.Invoke();
            try { await current.StartAsync(worker, request); }
            catch { await ReleaseAsync(); throw; }
        }
        finally { IsBusy = false; operations.Release(); Changed?.Invoke(); }
    }

    public async Task UpdateAsync(Variable[] variables, int sampleRate)
    {
        if (!await operations.WaitAsync(0)) throw new InvalidOperationException("采集操作尚未完成，请稍候。");
        IsBusy = true;
        try
        {
            Changed?.Invoke();
            var session = current ?? throw new InvalidOperationException("采集连接不可用。");
            if (releaseFailed) throw new InvalidOperationException(State);
            await session.UpdateAsync(variables, sampleRate);
        }
        finally { IsBusy = false; operations.Release(); Changed?.Invoke(); }
    }

    public async Task StopAsync()
    {
        await operations.WaitAsync();
        IsBusy = true;
        try { Changed?.Invoke(); await ReleaseAsync(); }
        finally { IsBusy = false; operations.Release(); Changed?.Invoke(); }
    }

    private async Task ReleaseAsync()
    {
        if (current == null) return;
        try { await current.DisposeAsync(); }
        catch { releaseFailed = true; throw; }
        current = null;
        releaseFailed = false;
    }

    public bool TryRead(out ObservationEvent? item)
    {
        var session = current;
        item = null;
        if (session == null || IsBusy || CanRetry) return false;
        // Consume at most one event per call so the presentation's per-tick budget also covers stale events.
        if (!session.Events.TryRead(out var next)) return false;
        if (next.SessionId == session.SessionId && next.Generation == session.Generation) item = next;
        return true;
    }
}

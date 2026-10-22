using System.IO;
using System.Threading.Channels;
using EmbeddedHelper.Core;

namespace EmbeddedHelper.App;

public partial class MainWindow
{
    private async Task SmokeRttLifecycleAsync()
    {
        foreach (var failRelease in new[] { false, true })
        {
            var fake = new RttSmokeSession { FailStart = true, FailRelease = failRelease };
            var coordinator = new ObservationCoordinator(() => fake);
            var window = new MainWindow(coordinator);
            try
            {
                window.rttHistory.Append("历史"); window.RttPage.RttText.Text = "历史";
                var request = new ObservationRequest("unused", 1, "", 0, "unused", "SWD", 1000, 200, true, []);
                try { await window.StartRttSessionAsync(request); throw new InvalidDataException("未注入启动失败。"); }
                catch (IOException) { }
                if (window.rttHistory.ToString() != "历史" || window.RttPage.RttText.Text != "历史" || window.rttSession != failRelease)
                    throw new InvalidDataException("RTT 启动失败丢失历史或会话归属。");
                window.SyncObservationState();
                if (failRelease && !window.RttPage.StopButton.IsEnabled) throw new InvalidDataException("释放失败后丢失停止入口。");
                fake.FailRelease = false;
                await window.StopObservationAsync();
                fake.FailStart = false;
                coordinator.Changed += () =>
                {
                    if (coordinator.HasSession && !window.rttSession) throw new InvalidDataException("启动回调错误地路由到变量页。");
                };
                await window.StartRttSessionAsync(request);
                if (!window.rttSession || window.rttHistory.Length != 0 || window.RttPage.RttText.Text.Length != 0)
                    throw new InvalidDataException("成功启动未清空旧会话显示。");
                await window.StopObservationAsync();
            }
            finally { fake.FailRelease = false; await coordinator.StopAsync(); window.refresh.Stop(); window.closing = true; window.Close(); }
        }
    }

    private sealed class RttSmokeSession : IObservationSession
    {
        public bool FailStart { get; set; }
        public bool FailRelease { get; set; }
        public long Generation => 1;
        public string SessionId => "offline-rtt";
        public bool CanRetry => false;
        public long Dropped => 0;
        public string State => "离线替身";
        public Channel<ObservationEvent> Queue { get; } = Channel.CreateUnbounded<ObservationEvent>();
        public ChannelReader<ObservationEvent> Events => Queue.Reader;
        public Task StartAsync(string worker, ObservationRequest request) => FailStart ? Task.FromException(new IOException("注入启动失败")) : Task.CompletedTask;
        public Task UpdateAsync(Variable[] variables, int sampleRate) => Task.CompletedTask;
        public ValueTask DisposeAsync() => FailRelease ? ValueTask.FromException(new IOException("注入释放失败")) : ValueTask.CompletedTask;
    }
}

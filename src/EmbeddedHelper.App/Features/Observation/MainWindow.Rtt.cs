using System.Diagnostics;
using System.Windows;
using EmbeddedHelper.Core;

namespace EmbeddedHelper.App;

public partial class MainWindow
{
    private async Task RefreshRttConnectionAsync()
    {
        var loaded = project; var target = Target;
        if (loaded == null || target == null) { RttPage.ConnectionSummary.Text = "请先打开工程并选择目标。"; return; }
        try
        {
            var connection = await ConnectionSettingsStore.LoadAsync(loaded.Root, target.Name, activeTransport);
            if (project != loaded || Target != target) return;
            RttPage.ConnectionSummary.Text = connection == null ? "请先保存当前目标的共享连接。" :
                $"{loaded.Name} / {target.Name} · " + (connection.Transport == "remote" ? "远程 RTT 网页" : $"本地探针 {connection.ProbeSerial} · RTT Up 0");
            RttPage.StartButton.Content = connection?.Transport == "remote" ? "打开远程 RTT 网页" : "开始本地 RTT";
            if (acquisition.HasSession && !rttSession) RttPage.Status.Text = "MCU Viewer 正在占用连接，请先停止并释放。";
        }
        catch (Exception ex) { RttPage.ConnectionSummary.Text = ex.Message; }
        SyncObservationState();
    }
    private async void StartRtt(object? sender, RoutedEventArgs e) => await Guard(async () =>
    {
        if (ObservationBusy || configurationBusy) throw new InvalidOperationException("请等待当前操作完成。");
        if (acquisition.HasSession && !rttSession) throw new InvalidOperationException("请先停止 MCU Viewer 采集并释放连接。");
        if (acquisition.CanRetry) await StopObservationAsync();
        if (acquisition.HasSession) throw new InvalidOperationException("请先停止当前 RTT 会话。");
        var loaded = project ?? throw new InvalidOperationException("请先打开工程。");
        var target = Target ?? throw new InvalidOperationException("请选择目标。");
        preparing = true; SyncObservationState();
        try
        {
            var connection = await ConnectionSettingsStore.LoadAsync(loaded.Root, target.Name, activeTransport)
                ?? throw new InvalidOperationException("请先保存当前目标的共享连接。");
            if (connection.Transport == "remote")
            {
                var uri = await ConnectionSettingsStore.RemoteRttUriAsync(loaded, target.Name, activeTransport);
                Process.Start(new ProcessStartInfo(uri.AbsoluteUri) { UseShellExecute = true });
                RttPage.Status.Text = "已打开远程 RTT 网页；日志与停止在网页操作。返回 MCU Viewer 前请手动停止网页 RTT。";
                return;
            }
            var ready = await ObservationPreparation.PrepareAsync(loaded, target, "", SettingsPage.DllPath.Text,
                _ => throw new InvalidOperationException("RTT 不应解析 ELF。"), rttOnly: true, transport: activeTransport);
            if (shutdownRequested) return;
            rttSession = true;
            ResetObservationDisplay(); RttPage.RttText.Clear();
            await acquisition.StartAsync(RuntimePaths.Current.WorkerExecutable, ready.Request);
            RttPage.Status.Text = acquisition.State.ToString();
        }
        finally { preparing = false; SyncObservationState(); }
    });
}

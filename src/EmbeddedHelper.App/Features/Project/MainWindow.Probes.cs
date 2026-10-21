using System.IO;
using EmbeddedHelper.Core;

namespace EmbeddedHelper.App;

public partial class MainWindow
{
    private bool discoveringProbes;
    private async Task RefreshProbeListAsync()
    {
        if (discoveringProbes || acquisition.HasSession || configurationBusy || shutdownRequested) return;
        var loaded = project; var target = Target;
        if (loaded == null || target == null) return;
        discoveringProbes = true; SyncObservationState();
        ConnectionPage.SetupStatus.Text = "正在枚举本机 USB 探针…";
        ConnectionPage.Probes.ItemsSource = null;
        try
        {
            var remembered = await ConnectionSettingsStore.LoadAsync(loaded.Root, target.Name, "local", validate: false);
            var probes = await ProbeDiscoveryProcess.ReadAsync(RuntimePaths.Current.WorkerExecutable, settings.Dll);
            if (project != loaded || Target != target || shutdownRequested) return;
            ConnectionPage.SetProbes(probes, remembered?.ProbeSerial ?? "");
        }
        catch (Exception ex) { ConnectionPage.SetupStatus.Text = "无法列出探针：" + ex.Message; }
        finally { discoveringProbes = false; SyncObservationState(); }
    }
    private async Task VerifySelectedProbeAsync(ObservationRequest request)
    {
        if (request.Host.Length > 0) return;
        var probes = await ProbeDiscoveryProcess.ReadAsync(RuntimePaths.Current.WorkerExecutable, request.Dll);
        var selection = ProbeSelection.Resolve(probes, request.ProbeSerial.ToString(System.Globalization.CultureInfo.InvariantCulture));
        if (selection.Selected == null) throw new IOException(selection.Message + " 请打开连接配置刷新或重新选择。");
    }
}

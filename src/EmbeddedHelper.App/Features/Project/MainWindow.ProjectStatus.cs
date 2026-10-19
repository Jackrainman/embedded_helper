using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using EmbeddedHelper.Core;

namespace EmbeddedHelper.App;

public partial class MainWindow
{
    private string activeTransport = "local";
    private string connectionIssue = "";
    private bool settingTransport;
    private long connectionRevision;
    private void SetTransportSelection()
    {
        settingTransport = true;
        GlobalTransport.SelectedIndex = activeTransport == "remote" ? 1 : 0;
        settingTransport = false;
    }
    private void UpdateProjectHeader()
    {
        if (GlobalTransport == null) return;
        GlobalTransport.IsEnabled = project != null && Target != null && !ObservationBusy && !configurationBusy && !acquisition.HasSession;
        ProjectStateLabel.Text = project == null ? "● 未打开项目" :
            $"● {project.Name} / {Target?.Name ?? "未选择目标"} · {(connectionIssue.Length == 0 ? "项目已就绪" : "连接配置错误")}";
        ProjectStateLabel.Foreground = project == null ? Brushes.LightGray : connectionIssue.Length == 0 ? Brushes.LightGreen : Brushes.Salmon;
        ProjectStateLabel.ToolTip = project == null ? "请打开工程" : project.Root + "\n" + (connectionIssue.Length == 0 ? "配置有效不代表设备已经连接。" : connectionIssue);
        ConnectionStateLabel.Text = acquisition.HasSession
            ? $"{(rttSession ? "RTT 监视" : "MCU Viewer")} · {acquisition.State}"
            : ObservationBusy || configurationBusy ? "正在处理，请稍候" : connectionIssue.Length == 0 ? "未连接" : connectionIssue;
        ConnectionStateLabel.ToolTip = ConnectionStateLabel.Text;
    }
    private async Task RefreshProjectConnectionAsync()
    {
        var revision = ++connectionRevision;
        var loaded = project; var target = Target; var mode = activeTransport;
        var issue = "";
        try
        {
            if (loaded != null)
            {
                if (target == null) throw new InvalidOperationException("请选择工程目标。");
                if (string.IsNullOrWhiteSpace(target.Device) || target.Interface is not ("SWD" or "JTAG") || target.SpeedKHz <= 0)
                    throw new InvalidOperationException("工程目标的 J-Link 参数不完整。");
                _ = await ConnectionSettingsStore.LoadAsync(loaded.Root, target.Name, mode)
                    ?? throw new InvalidOperationException("请先保存本地探针序列号。");
                if (mode == "remote") await ConnectionSettingsStore.RemoteEndpointAsync(loaded);
            }
        }
        catch (Exception ex) { issue = ex.Message; }
        if (revision != connectionRevision || project != loaded || Target != target || activeTransport != mode) return;
        connectionIssue = issue;
        if (issue.Length > 0 && !acquisition.HasSession)
            ShowViewerIssue(issue, false, mode == "remote" ? ObservationConfigurationArea.Remote : ObservationConfigurationArea.Connection);
        UpdateProjectHeader();
    }
    private async void GlobalTransportChanged(object sender, SelectionChangedEventArgs e)
    {
        if (!pagesReady || settingTransport) return;
        var requested = GlobalTransport.SelectedIndex == 1 ? "remote" : "local";
        SetTransportSelection();
        await Guard(() => SelectTransportAsync(requested));
    }
    private async Task SelectTransportAsync(string requested)
    {
        if (requested is not ("local" or "remote")) throw new ArgumentException("无效的连接方式。");
        if (ObservationBusy || configurationBusy || acquisition.HasSession) throw new InvalidOperationException("采集中或正在处理连接，禁止切换。");
        var loaded = project ?? throw new InvalidOperationException("未打开项目");
        if (Target == null) throw new InvalidOperationException("请选择工程目标。");
        preparing = true; SyncObservationState();
        try
        {
            if (requested == "remote") await ConnectionSettingsStore.RemoteEndpointAsync(loaded);
            if (shutdownRequested) return;
            activeTransport = requested;
            SetTransportSelection();
            InvalidateViewer();
        }
        finally { preparing = false; SyncObservationState(); }
        await RefreshProjectConnectionAsync();
        await RefreshRttConnectionAsync();
        if (Pages.SelectedIndex == 1) await PrepareViewerAsync();
        Report("已选择" + (activeTransport == "remote" ? "远程" : "本地") + "连接；尚未连接设备。");
    }
}

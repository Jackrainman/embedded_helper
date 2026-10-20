using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using EmbeddedHelper.Core;

namespace EmbeddedHelper.App;

public partial class MainWindow
{
    private string activeTransport = "local";
    private ConnectionConfiguration connectionConfiguration = new(ConnectionConfigurationState.Pending, "请打开工程");
    private string connectionFailure = "";
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
        ConnectionButton.IsEnabled = GlobalTransport.IsEnabled;
        ConnectionButton.ToolTip = project == null || Target == null ? "请先打开项目并选择目标" : activeTransport == "remote" ? "前往远程配置" : "配置当前项目的共享连接";
        ProjectStateLabel.Text = project == null ? "● 未打开项目" :
            $"● 已打开 · {project.Name} / {Target?.Name ?? "未选择目标"}";
        ProjectStateLabel.Foreground = project == null ? Brushes.LightGray : Brushes.LightGreen;
        ProjectStateLabel.ToolTip = project?.Root ?? "请打开工程";
        var configuration = connectionConfiguration.State switch
        {
            ConnectionConfigurationState.Valid => "连接配置有效",
            ConnectionConfigurationState.Pending => "连接待设置：" + connectionConfiguration.Message,
            _ => "连接配置无效：" + connectionConfiguration.Message
        };
        var session = acquisition.HasSession
            ? $"{(rttSession ? "RTT 监视" : "MCU Viewer")} · {acquisition.State}"
            : ObservationBusy || configurationBusy ? "正在处理，请稍候" : connectionFailure.Length > 0 ? "连接失败：" + connectionFailure : "未连接";
        ConnectionStateLabel.Text = project == null ? "未连接" : configuration + " · " + session;
        ConnectionStateLabel.Foreground = connectionConfiguration.State == ConnectionConfigurationState.Invalid || connectionFailure.Length > 0
            ? Brushes.Salmon : (Brush)FindResource("HeaderMuted");
        ConnectionStateLabel.ToolTip = ConnectionStateLabel.Text;
    }
    private async Task RefreshProjectConnectionAsync()
    {
        var revision = ++connectionRevision;
        var loaded = project; var target = Target; var mode = activeTransport;
        var result = loaded == null ? new ConnectionConfiguration(ConnectionConfigurationState.Pending, "请打开工程")
            : await ConnectionSettingsStore.InspectAsync(loaded, target, mode);
        if (revision != connectionRevision || project != loaded || Target != target || activeTransport != mode) return;
        connectionConfiguration = result;
        if (result.State != ConnectionConfigurationState.Valid && !acquisition.HasSession)
        {
            ShowViewerIssue(result.Message, false, mode == "remote" ? ObservationConfigurationArea.Remote : ObservationConfigurationArea.Connection);
            RefreshViewerRows();
        }
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
            if (shutdownRequested) return;
            activeTransport = requested;
            connectionFailure = "";
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

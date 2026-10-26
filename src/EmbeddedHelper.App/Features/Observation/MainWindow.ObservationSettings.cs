using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using EmbeddedHelper.Core;

namespace EmbeddedHelper.App;

public partial class MainWindow
{
    private bool pagesReady, preparing;
    private PreparedObservation? prepared;
    private WatchSettings? preparedWatch => prepared?.Settings;
    private Task viewerPreparation = Task.CompletedTask;
    private ObservationConfigurationArea configurationDestination = ObservationConfigurationArea.Observation;
    private bool rttSession;

    private async void PagesChanged(object? sender, SelectionChangedEventArgs e)
    {
        if (!pagesReady || e.OriginalSource != Pages) return;
        if (Pages.SelectedIndex == 1) await PrepareViewerAsync();
        if (Pages.SelectedIndex == 2) await RefreshRttConnectionAsync();
    }
    private async void ConfigureConnection(object? sender, RoutedEventArgs e)
    {
        if (project == null || Target == null) { Report("请先打开项目并选择目标。"); return; }
        if (ObservationBusy || configurationBusy || acquisition.HasSession) { Report("请先停止并释放当前采集，再修改连接配置。"); return; }
        if (activeTransport == "remote") { Pages.SelectedIndex = 0; ProjectSections.SelectedIndex = 1; return; }
        await RefreshProbeListAsync();
        if (shutdownRequested) return;
        var dialog = new Window
        {
            Title = "连接配置",
            Owner = this,
            Width = 640,
            Height = 440,
            MinWidth = 480,
            MinHeight = 360,
            WindowStartupLocation = WindowStartupLocation.CenterOwner,
            Background = (System.Windows.Media.Brush)FindResource("MainBackground"),
            Content = new ScrollViewer { Content = ConnectionPage, Margin = new Thickness(18), VerticalScrollBarVisibility = ScrollBarVisibility.Auto }
        };
        try { dialog.ShowDialog(); }
        finally { ((ScrollViewer)dialog.Content).Content = null; }
        if (Pages.SelectedIndex == 1) await PrepareViewerAsync();
        else if (Pages.SelectedIndex == 2) await RefreshRttConnectionAsync();
    }
    private void ConfigureObservation(object? sender, RoutedEventArgs e)
    {
        if (configurationDestination == ObservationConfigurationArea.Observation)
        { ObservationPage.ShowAddVariables(); return; }
        if (configurationDestination == ObservationConfigurationArea.Connection) { ConfigureConnection(sender, e); return; }
        Pages.SelectedIndex = configurationDestination == ObservationConfigurationArea.Settings ? 4 : 0;
        ProjectSections.SelectedIndex = configurationDestination switch { ObservationConfigurationArea.Project => 0, ObservationConfigurationArea.Remote => 1, _ => 0 };
    }
    private void ApplyWatchToSetup(WatchSettings? watch)
    {
        var previous = suppressWatchAutosave; suppressWatchAutosave = true;
        try { ObservationPage.SampleRate.Text = (watch?.Rate ?? 200).ToString(CultureInfo.InvariantCulture); }
        finally { suppressWatchAutosave = previous; }
    }
    private async Task LoadConnectionAsync()
    {
        var loaded = project; var target = Target;
        ConnectionPage.ProbeSerial.Text = "";
        if (loaded == null || target == null) { ConnectionPage.ProbeSerial.Text = ""; return; }
        try
        {
            var saved = await ConnectionSettingsStore.LoadAsync(loaded.Root, target.Name, "local", validate: false);
            if (project != loaded || Target != target) return;
            ConnectionPage.ProbeSerial.Text = saved?.ProbeSerial ?? "";
            ConnectionPage.SetupStatus.Text = saved == null ? "当前目标尚未保存连接。" : "已读取共享连接；旧 watch.json 连接字段可兼容使用。修改后自动保存。";
        }
        catch (Exception ex) { ConnectionPage.SetupStatus.Text = ex.Message; Report(ex.Message); }
    }
    private async void SaveConnection(object? sender, RoutedEventArgs e) => await Guard(async () =>
    {
        if (!observationView.CanEdit) throw new InvalidOperationException("请先停止采集并等待当前操作结束。");
        var loaded = project ?? throw new InvalidOperationException("请先打开工程。");
        var target = Target ?? throw new InvalidOperationException("请选择目标。");
        if (activeTransport == "local" && ConnectionPage.Probes.SelectedItem is not UsbProbe)
            throw new InvalidOperationException("请先刷新并选择要连接的实体 J-Link。");
        var chosen = new ConnectionSettings(target.Name, activeTransport, ConnectionPage.ProbeSerial.Text.Trim());
        ConnectionSettingsStore.Save(loaded.Root, chosen);
        if (await ConnectionSettingsStore.LoadAsync(loaded.Root, target.Name, activeTransport) != chosen)
            throw new InvalidOperationException("连接写入后回读不一致，请重试。");
        connectionFailure = "";
        InvalidateViewer();
        ConnectionPage.SetupStatus.Text = "共享连接已保存；变量列表和采样率未改动。";
        await RefreshRttConnectionAsync();
        await RefreshProjectConnectionAsync();
    });
    private void InvalidateViewer()
    {
        prepared = null;
        if (ObservationPage == null) return;
        ShowViewerIssue("请确认当前目标的连接配置和变量设置。", false);
        observationView.VisibleRows = [];
        observationView.ClearPlot();
    }
    private void ShowViewerIssue(string message, bool report = true, ObservationConfigurationArea destination = ObservationConfigurationArea.Observation)
    {
        prepared = null; observationView.Ready = false;
        configurationDestination = destination;
        observationView.ConfigureText = destination switch { ObservationConfigurationArea.Project => "去工程概览", ObservationConfigurationArea.Remote => "去远程配置", ObservationConfigurationArea.Settings => "去设置与诊断", ObservationConfigurationArea.Connection => "去连接配置", _ => "设置观察变量" };
        observationView.Issue = message;
        observationView.Summary = "MCU Viewer 尚未就绪";
        observationView.VariablesEmpty = "尚无已配置变量\n请搜索添加变量。";
        observationView.PlotEmpty = "配置就绪并开始采集后显示曲线";
        observationView.Status = "未采集 · ELF 与固件匹配未核验";
        if (report) Report(message);
    }
    private void RefreshViewerRows() => observationView.VisibleRows = Variables.Where(v => v.Selected).ToArray();
    private Task PrepareViewerAsync()
    {
        if (acquisition.HasSession || shutdownRequested || watchDirty) return Task.CompletedTask;
        if (!viewerPreparation.IsCompleted) return viewerPreparation;
        viewerPreparation = PrepareViewerCoreAsync();
        return viewerPreparation;
    }
    private async Task PrepareViewerCoreAsync()
    {
        prepared = null; observationView.Ready = false;
        preparing = true; SyncObservationState();
        observationView.VisibleRows = [];
        var loaded = project; var target = Target;
        try
        {
            if (indexing || acquisition.IsBusy || configurationBusy) throw new InvalidOperationException("请等待当前操作完成。");
            if (loaded == null || target == null) throw new ObservationPreparationException(ObservationConfigurationArea.Project, "请先打开工程并选择目标。");
            var tool = SettingsPage.ReadElfPath.Text;
            var ready = await ObservationPreparation.PrepareAsync(loaded, target, RefreshElfPath(), SettingsPage.DllPath.Text, path => ReadElfWithFeedbackAsync(path, tool), transport: activeTransport);
            if (shutdownRequested || project != loaded || Target != target) return;
            observationView.LoadIndex(ready.Index, ready.Settings.Identities);
            elfStamp = ready.ElfStamp; RefreshViewerRows(); ApplyWatchToSetup(ready.Settings);
            prepared = ready; observationView.Ready = true;
            var request = ready.Request;
            var connection = request.Host.Length > 0 ? $"远程 {request.Host}:{request.Port}" : "本地探针 " + request.ProbeSerial;
            observationView.Summary = $"{loaded.Name} / {target.Name} · {connection} · {request.SampleRate} Hz · {request.Variables.Length} 个变量";
            observationView.PlotEmpty = "开始采集后显示曲线";
            observationView.Status = request.Host.Length > 0 ? "未连接 · 开始前请先在远程网页手动停止 RTT" : "未连接 · ELF 与固件匹配未核验";
            observationView.ClearPlot();
            Report("已读取变量与共享连接配置；尚未连接设备。");
        }
        catch (ObservationPreparationException ex)
        {
            if (project != loaded || Target != target || shutdownRequested) return;
            ShowViewerIssue(ex.Message, true, ex.Area);
            RefreshViewerRows();
            if (ex.SavedSettings is { } saved)
            {
                ApplyWatchToSetup(saved);
                if (ex.Index.Length > 0 || Variables.Count == 0)
                    observationView.LoadIndex(ex.Index, saved.Identities);
                observationView.Summary = $"已保存变量 · {saved.Rate} Hz · {saved.Identities.Length} 项 · 配置未就绪";
            }
        }
        catch (Exception ex) { ShowViewerIssue(ex.Message); }
        finally { preparing = false; SyncObservationState(); }
    }
    private async void SaveObservationSettings(object? sender, RoutedEventArgs e) => await Guard(SaveVariablesAsync);
    private async Task SaveVariablesAsync()
    {
        try
        {
            if (!observationView.CanEdit) throw new InvalidOperationException("请先停止采集并等待当前操作完成。");
            var loaded = project ?? throw new InvalidOperationException("请先打开工程。");
            var target = Target ?? throw new InvalidOperationException("请选择工程目标。");
            if (!int.TryParse(ObservationPage.SampleRate.Text, NumberStyles.Integer, CultureInfo.InvariantCulture, out var rate) || rate is < 1 or > 1000)
                throw new InvalidOperationException("采样频率须为 1～1000 的整数，请修正后离开输入框。");
            if (watchEdit == null || watchEdit.Root != loaded.Root) watchEdit = new WatchSettingsEdit(loaded.Root);
            var saved = watchEdit.Save(target.Name, Variables.Where(v => v.Selected).Select(v => v.Variable.Identity).ToArray(), rate);
            watchDirty = false; observationView.SettingsError = false;
            observationView.SettingsStatus = $"已自动保存 · {saved.Identities.Length} 个变量 · {saved.Rate} Hz";
            InvalidateViewer();
            await PrepareViewerAsync();
        }
        catch (Exception ex)
        {
            watchDirty = true; observationView.SettingsError = true; observationView.Ready = false;
            observationView.SettingsStatus = "未保存：" + ex.Message;
            SyncObservationState();
            throw;
        }
    }
}

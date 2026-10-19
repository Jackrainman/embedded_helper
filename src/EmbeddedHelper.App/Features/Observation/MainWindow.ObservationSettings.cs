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
    private void ConfigureConnection(object? sender, RoutedEventArgs e) { Pages.SelectedIndex = 0; ProjectSections.SelectedIndex = 1; }
    private void ConfigureObservation(object? sender, RoutedEventArgs e)
    {
        if (configurationDestination == ObservationConfigurationArea.Observation)
        { ObservationPage.VariableEditor.IsExpanded = true; return; }
        Pages.SelectedIndex = configurationDestination == ObservationConfigurationArea.Settings ? 4 : 0;
        ProjectSections.SelectedIndex = configurationDestination switch { ObservationConfigurationArea.Project => 0, ObservationConfigurationArea.Remote => 2, _ => 1 };
    }
    private void ApplyWatchToSetup(WatchSettings? watch)
    { ObservationPage.SampleRate.Text = (watch?.Rate ?? 100).ToString(CultureInfo.InvariantCulture); }
    private async Task LoadConnectionAsync()
    {
        var loaded = project; var target = Target;
        ConnectionPage.ProbeSerial.Text = "";
        if (loaded == null || target == null) { ConnectionPage.ProbeSerial.Text = ""; return; }
        try
        {
            var saved = await ConnectionSettingsStore.LoadAsync(loaded.Root, target.Name);
            if (project != loaded || Target != target) return;
            ConnectionPage.ProbeSerial.Text = saved?.ProbeSerial ?? "";
            ConnectionPage.SetupStatus.Text = saved == null ? "当前目标尚未保存连接。" : "已读取共享连接；旧 watch.json 连接字段可兼容使用。修改后请保存。";
        }
        catch (Exception ex) { ConnectionPage.SetupStatus.Text = ex.Message; Report(ex.Message); }
    }
    private async void SaveConnection(object? sender, RoutedEventArgs e) => await Guard(async () =>
    {
        if (!observationView.CanEdit) throw new InvalidOperationException("请先停止采集并等待当前操作结束。");
        var loaded = project ?? throw new InvalidOperationException("请先打开工程。");
        var target = Target ?? throw new InvalidOperationException("请选择目标。");
        ConnectionSettingsStore.Save(loaded.Root, new(target.Name, activeTransport, ConnectionPage.ProbeSerial.Text.Trim()));
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
        observationView.Series = [];
    }
    private void ShowViewerIssue(string message, bool report = true, ObservationConfigurationArea destination = ObservationConfigurationArea.Observation)
    {
        prepared = null; observationView.Ready = false;
        configurationDestination = destination;
        observationView.ConfigureText = destination switch { ObservationConfigurationArea.Project => "去工程概览", ObservationConfigurationArea.Remote => "去远程配置", ObservationConfigurationArea.Settings => "去设置与诊断", ObservationConfigurationArea.Connection => "去连接配置", _ => "设置观察变量" };
        observationView.Issue = message;
        observationView.Summary = "MCU Viewer 尚未就绪";
        observationView.VariablesEmpty = "尚无已配置变量\n请搜索添加并保存。";
        observationView.PlotEmpty = "配置就绪并开始采集后显示曲线";
        observationView.Status = "未采集 · ELF 与固件匹配未核验";
        if (report) Report(message);
    }
    private void RefreshViewerRows() => observationView.VisibleRows = Variables.Where(v => v.Selected).ToArray();
    private Task PrepareViewerAsync()
    {
        if (acquisition.HasSession || shutdownRequested) return Task.CompletedTask;
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
            var ready = await ObservationPreparation.PrepareAsync(loaded, target, ProjectPage.ElfPath.Text, SettingsPage.DllPath.Text, path => ElfIndexService.ReadAsync(path, tool), transport: activeTransport);
            if (shutdownRequested || project != loaded || Target != target) return;
            Variables.Clear();
            foreach (var variable in ready.Index) Variables.Add(new VariableRow(variable) { Selected = ready.Settings.Identities.Contains(variable.Identity) });
            elfStamp = ready.ElfStamp; RefreshViewerRows(); ApplyWatchToSetup(ready.Settings);
            prepared = ready; observationView.Ready = true;
            var request = ready.Request;
            var connection = request.Host.Length > 0 ? $"远程 {request.Host}:{request.Port}" : "本地探针 " + request.ProbeSerial;
            observationView.Summary = $"{loaded.Name} / {target.Name} · {connection} · {request.SampleRate} Hz · {request.Variables.Length} 个变量";
            observationView.PlotEmpty = "开始采集后显示曲线";
            observationView.Status = request.Host.Length > 0 ? "未连接 · 开始前请先在远程网页手动停止 RTT" : "未连接 · ELF 与固件匹配未核验";
            observationView.Series = [];
            Report("已读取变量与共享连接配置；尚未连接设备。");
        }
        catch (ObservationPreparationException ex)
        {
            if (project != loaded || Target != target || shutdownRequested) return;
            ShowViewerIssue(ex.Message, true, ex.Area);
            if (ex.SavedSettings is { } saved)
            {
                ApplyWatchToSetup(saved);
                var indexed = ex.Index.ToDictionary(v => v.Identity);
                observationView.VisibleRows = saved.Identities.Select(id => new VariableRow(indexed.GetValueOrDefault(id) ?? new Variable(id, id, 0, "未解析", 0, "未采集")) { Selected = true }).ToArray();
                observationView.Summary = $"已保存变量 · {saved.Rate} Hz · {saved.Identities.Length} 项 · 配置未就绪";
            }
        }
        catch (Exception ex) { ShowViewerIssue(ex.Message); }
        finally { preparing = false; SyncObservationState(); }
    }
    private async void SaveObservationSettings(object? sender, RoutedEventArgs e) => await Guard(SaveVariablesAsync);
    private async Task SaveVariablesAsync()
    {
        if (!observationView.CanEdit) throw new InvalidOperationException("请先停止采集并等待当前操作完成。");
        var loaded = project ?? throw new InvalidOperationException("请先打开工程。");
        var target = Target ?? throw new InvalidOperationException("请选择工程目标。");
        var rate = int.Parse(ObservationPage.SampleRate.Text, CultureInfo.InvariantCulture);
        var variables = Variables.Where(v => v.Selected).Select(v => v.Variable).ToArray();
        new ReadPlan(1, rate, variables).Validate();
        if (variables.Length == 0) throw new InvalidOperationException("请至少添加一个变量；RTT 请到 RTT 监视。");
        var legacy = await WatchSettingsStore.LoadAsync(loaded.Root);
        if (project != loaded || Target != target || !observationView.CanEdit) throw new InvalidOperationException("工程或会话已变化，请重试保存。");
        if (legacy?.Target != target.Name) legacy = null;
        var watch = new WatchSettings(target.Name, variables.Select(v => v.Identity).ToArray(), rate)
        { Transport = legacy?.Transport ?? "local", ProbeSerial = legacy?.ProbeSerial ?? "", EnableRtt = false };
        WatchSettingsStore.Save(loaded.Root, watch);
        InvalidateViewer();
        await PrepareViewerAsync();
    }
}

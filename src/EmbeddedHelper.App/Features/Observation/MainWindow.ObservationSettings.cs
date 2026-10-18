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

    private async void PagesChanged(object? sender, SelectionChangedEventArgs e)
    {
        if (!pagesReady || e.OriginalSource != Pages || Pages.SelectedIndex != 1) return;
        await PrepareViewerAsync();
    }
    private void ConfigureObservation(object? sender, RoutedEventArgs e)
    {
        Pages.SelectedIndex = configurationDestination == ObservationConfigurationArea.Settings ? 3 : 0;
        ProjectSections.SelectedIndex = configurationDestination switch { ObservationConfigurationArea.Project => 0, ObservationConfigurationArea.Remote => 2, _ => 1 };
    }
    private void ApplyWatchToSetup(WatchSettings? watch)
    {
        ObservationSetupPage.TransportBox.SelectedIndex = watch?.Transport == "remote" ? 1 : 0;
        ObservationSetupPage.ProbeSerial.Text = watch?.ProbeSerial ?? "";
        ObservationSetupPage.SampleRate.Text = (watch?.Rate ?? 100).ToString(CultureInfo.InvariantCulture);
        ObservationSetupPage.EnableRtt.IsChecked = watch?.Transport != "remote" && (watch?.EnableRtt ?? true);
        ObservationSetupPage.SetupStatus.Text = watch == null ? "尚未保存工程观察设置。解析 ELF、选择变量或 RTT 后保存。" : "已读取工程观察设置；修改后请保存，再进入查看变量。";
    }
    private void InvalidateViewer()
    {
        prepared = null;
        if (ObservationPage == null) return;
        ShowViewerIssue("请先在工程中保存当前目标的观察设置。", false);
        observationView.VisibleRows = [];
        observationView.Series = [];
        rttHistory.Clear(); observationView.RttText = "";
    }
    private void ShowViewerIssue(string message, bool report = true, ObservationConfigurationArea destination = ObservationConfigurationArea.Observation)
    {
        prepared = null; observationView.Ready = false;
        configurationDestination = destination;
        observationView.ConfigureText = destination switch { ObservationConfigurationArea.Project => "去工程概览", ObservationConfigurationArea.Remote => "去远程配置", ObservationConfigurationArea.Settings => "去设置与诊断", _ => "去观察设置" };
        observationView.Issue = message;
        observationView.Summary = "工程观察设置未就绪";
        observationView.VariablesEmpty = "尚无已配置变量\n请先在工程的观察设置中勾选并保存。";
        observationView.PlotEmpty = "工程设置就绪后可查看变量曲线";
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
        var loaded = project;
        var target = Target;
        try
        {
            if (indexing || acquisition.IsBusy || configurationBusy) throw new InvalidOperationException("当前操作尚未完成；请结束后重新进入查看变量。");
            if (loaded == null || target == null) throw new ObservationPreparationException(ObservationConfigurationArea.Project, "请先打开工程并选择构建目标，再保存工程观察设置。");
            var tool = SettingsPage.ReadElfPath.Text;
            var ready = await ObservationPreparation.PrepareAsync(loaded, target, ProjectPage.ElfPath.Text, SettingsPage.DllPath.Text, path => ElfIndexService.ReadAsync(path, tool));
            if (shutdownRequested || project != loaded || Target != target) return;
            if (ready.Index.Length > 0)
            {
                Variables.Clear();
                foreach (var variable in ready.Index) Variables.Add(new VariableRow(variable) { Selected = ready.Settings.Identities.Contains(variable.Identity) });
            }
            else foreach (var row in Variables) row.Selected = false;
            elfStamp = ready.ElfStamp;
            RefreshViewerRows();
            prepared = ready; observationView.Ready = true;
            var request = ready.Request;
            var connection = request.Host.Length > 0 ? $"远程 {request.Host}:{request.Port}" : "本地探针 " + request.ProbeSerial;
            observationView.Summary = $"{loaded.Name} / {target.Name} · {connection} · {request.SampleRate} Hz · {request.Variables.Length} 个变量 · RTT {(request.Rtt ? "开启" : "关闭")}";
            observationView.RttEmpty = request.Rtt ? "等待本地 RTT 输出" : "工程设置未启用本地 RTT";
            observationView.VariablesEmpty = "本工程仅观察 RTT\n未配置观察变量。";
            observationView.PlotEmpty = request.Variables.Length == 0 ? "本工程仅观察 RTT，未启用变量曲线" : "开始采集后显示曲线";
            observationView.Status = "已读取工程设置 · 未连接 · ELF 与固件匹配未核验";
            observationView.Series = [];
            Report("已读取工程观察设置；尚未连接设备。");
        }
        catch (ObservationPreparationException ex) { ShowViewerIssue(ex.Message, true, ex.Area); }
        catch (Exception ex) { ShowViewerIssue(ex.Message); }
        finally { preparing = false; SyncObservationState(); }
    }

    private void SaveObservationSettings(object? sender, RoutedEventArgs e)
    {
        try
        {
            if (acquisition.HasSession || ObservationBusy || configurationBusy) throw new InvalidOperationException("请先停止采集并等待当前操作完成，再保存工程设置。");
            var loaded = project ?? throw new InvalidOperationException("请先打开工程。");
            var target = Target ?? throw new InvalidOperationException("请先选择工程目标。");
            ObservationSetupPage.VariableGrid.CommitEdit(DataGridEditingUnit.Cell, true);
            ObservationSetupPage.VariableGrid.CommitEdit(DataGridEditingUnit.Row, true);
            var rate = int.Parse(ObservationSetupPage.SampleRate.Text, CultureInfo.InvariantCulture);
            var variables = Variables.Where(v => v.Selected).Select(v => v.Variable).ToArray();
            new ReadPlan(1, rate, variables).Validate();
            var remote = ObservationSetupPage.TransportBox.SelectedIndex == 1;
            var rtt = !remote && ObservationSetupPage.EnableRtt.IsChecked == true;
            var probe = ObservationSetupPage.ProbeSerial.Text.Trim();
            if (!remote && (!int.TryParse(probe, out var serialNumber) || serialNumber <= 0)) throw new InvalidOperationException("请填写具体的本地探针序列号。");
            if (variables.Length == 0 && !rtt) throw new InvalidOperationException("请勾选观察变量，或启用本地 RTT。");
            var watch = new WatchSettings(target.Name, variables.Select(v => v.Identity).ToArray(), rate) { Transport = remote ? "remote" : "local", ProbeSerial = probe, EnableRtt = rtt };
            WatchSettingsStore.Save(loaded.Root, watch);
            InvalidateViewer();
            ObservationSetupPage.SetupStatus.Text = "已保存到工程 .embedded_helper/watch.json。进入查看变量即可读取；本次保存未连接设备。";
            Report(ObservationSetupPage.SetupStatus.Text);
        }
        catch (Exception ex) { ObservationSetupPage.SetupStatus.Text = "设置未保存：" + ex.Message; Report(ObservationSetupPage.SetupStatus.Text); }
    }
}

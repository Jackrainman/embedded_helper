using System.Globalization;
using System.IO;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using EmbeddedHelper.Core;

namespace EmbeddedHelper.App;

public partial class MainWindow
{
    private bool pagesReady;
    private WatchSettings? preparedWatch;
    private Task viewerPreparation = Task.CompletedTask;
    private enum ConfigurationDestination { Project, Observation, Remote, Settings }
    private ConfigurationDestination configurationDestination = ConfigurationDestination.Observation;

    private async void PagesChanged(object? sender, SelectionChangedEventArgs e)
    {
        if (!pagesReady || e.OriginalSource != Pages || Pages.SelectedIndex != 1) return;
        await PrepareViewerAsync();
    }

    private void ConfigureObservation(object? sender, RoutedEventArgs e)
    {
        Pages.SelectedIndex = configurationDestination == ConfigurationDestination.Settings ? 3 : 0;
        ProjectSections.SelectedIndex = configurationDestination switch { ConfigurationDestination.Project => 0, ConfigurationDestination.Remote => 2, _ => 1 };
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
        preparedWatch = null;
        if (ObservationPage == null) return;
        ShowViewerIssue("请先在工程中保存当前目标的观察设置。", false);
        ObservationPage.VariableGrid.ItemsSource = null;
        ObservationPage.VariablesEmpty.Visibility = Visibility.Visible;
        ObservationPage.Plot.Children.Clear();
        ObservationPage.PlotEmpty.Visibility = Visibility.Visible;
        rttHistory.Clear();
        ObservationPage.RttText.Clear();
    }

    private void ShowViewerIssue(string message, bool report = true, ConfigurationDestination destination = ConfigurationDestination.Observation)
    {
        preparedWatch = null;
        configurationDestination = destination;
        ObservationPage.ConfigureButton.Content = destination switch { ConfigurationDestination.Project => "去工程概览", ConfigurationDestination.Remote => "去远程配置", ConfigurationDestination.Settings => "去设置与诊断", _ => "去观察设置" };
        ObservationPage.ConfigurationRequired.Visibility = Visibility.Visible;
        ObservationPage.ConfigurationIssue.Text = message;
        ObservationPage.ConfigurationSummary.Text = "工程观察设置未就绪";
        ObservationPage.StartButton.IsEnabled = false;
        ObservationPage.VariablesEmpty.Text = "尚无已配置变量\n请先在工程的观察设置中勾选并保存。";
        ObservationPage.PlotEmpty.Text = "工程设置就绪后可查看变量曲线";
        if (report) Report(message);
    }

    private void RefreshViewerRows()
    {
        var rows = Variables.Where(v => v.Selected).ToArray();
        ObservationPage.VariableGrid.ItemsSource = rows;
        ObservationPage.VariablesEmpty.Visibility = rows.Length == 0 ? Visibility.Visible : Visibility.Collapsed;
    }

    private Task PrepareViewerAsync()
    {
        if (observation != null) return Task.CompletedTask;
        if (!viewerPreparation.IsCompleted) return viewerPreparation;
        viewerPreparation = PrepareViewerCoreAsync();
        return viewerPreparation;
    }

    private async Task PrepareViewerCoreAsync()
    {
        preparedWatch = null;
        ProjectPage.TargetBox.IsEnabled = false;
        EnableConnectionFields(false);
        ObservationPage.StartButton.IsEnabled = false;
        ObservationPage.VariableGrid.ItemsSource = null;
        ObservationPage.VariablesEmpty.Visibility = Visibility.Visible;
        var destination = ConfigurationDestination.Project;
        try
        {
            if (transition) throw new InvalidOperationException("当前操作尚未完成；请结束后重新进入查看变量。");
            var loaded = project ?? throw new InvalidOperationException("请先打开工程，再在工程的观察设置中选择变量或 RTT 并保存。");
            var target = Target ?? throw new InvalidOperationException("请先在工程概览中选择构建目标。");
            destination = ConfigurationDestination.Observation;
            var watch = await WatchSettingsStore.LoadAsync(loaded.Root) ?? throw new InvalidOperationException("工程还没有观察设置。请在工程的观察设置中配置并保存。");
            if (watch.Target != target.Name) throw new InvalidOperationException("当前目标没有已保存的观察设置，请在工程中为此目标重新选择并保存。");
            if (watch.Transport is not ("local" or "remote")) throw new InvalidDataException("工程中的连接方式无效，请重新保存观察设置。");
            if (watch.Rate is < 1 or > 1000 || watch.Identities == null || watch.Identities.Length > 64 || watch.Identities.Distinct().Count() != watch.Identities.Length) throw new InvalidDataException("工程采样率或变量列表无效，请在观察设置中重新保存。");
            ApplyWatchToSetup(watch);
            if (watch.Transport == "local")
            {
                if (!int.TryParse(watch.ProbeSerial, out var probe) || probe <= 0) throw new InvalidOperationException("工程中未设置具体本地探针序列号，请在观察设置中填写并保存。");
            }
            else
            {
                destination = ConfigurationDestination.Remote;
                if (!File.Exists(loaded.RemotePath)) throw new InvalidOperationException("工程没有远程配置。请在工程的远程配置中预览并应用连接参数。");
                using var remote = JsonDocument.Parse(await File.ReadAllTextAsync(loaded.RemotePath), ProjectReader.JsonOptions);
                var host = remote.RootElement.GetProperty("ServerName").GetString() ?? "";
                var port = remote.RootElement.GetProperty("Port").GetInt32();
                if (string.IsNullOrWhiteSpace(host) || port is < 1 or > 65535) throw new InvalidDataException("工程远程主机或端口无效，请重新配置。");
                RemotePage.RemoteHost.Text = host;
                RemotePage.RemotePort.Text = port.ToString(CultureInfo.InvariantCulture);
            }
            var useRtt = watch.EnableRtt && watch.Transport == "local";
            destination = ConfigurationDestination.Observation;
            if (watch.Identities.Length == 0 && !useRtt) throw new InvalidOperationException("工程尚未选择观察变量或本地 RTT，请在观察设置中选择并保存。");
            if (watch.Identities.Length > 0)
            {
                destination = ConfigurationDestination.Project;
                if (!File.Exists(ProjectPage.ElfPath.Text)) throw new InvalidOperationException("工程目标的 ELF 不存在。请先在工程概览中确认 ELF 并解析变量。");
                await ParseElfAsync(true);
                foreach (var row in Variables) row.Selected = watch.Identities.Contains(row.Variable.Identity);
                destination = ConfigurationDestination.Observation;
                if (Variables.Count(v => v.Selected) != watch.Identities.Distinct().Count()) throw new InvalidDataException("已保存的变量在当前 ELF 中缺失，请回工程检查变量选择后重新保存。");
                new ReadPlan(1, watch.Rate, Variables.Where(v => v.Selected).Select(v => v.Variable).ToArray()).Validate();
            }
            else foreach (var row in Variables) row.Selected = false;
            RefreshViewerRows();
            destination = ConfigurationDestination.Settings;
            if (!File.Exists(SettingsPage.DllPath.Text)) throw new InvalidOperationException("本机尚未配置 JLink_x64.dll。请先在设置与诊断中选择已安装的库，再返回查看变量。");
            preparedWatch = watch;
            ObservationPage.ConfigurationRequired.Visibility = Visibility.Collapsed;
            var connection = watch.Transport == "remote" ? $"远程 {RemotePage.RemoteHost.Text}:{RemotePage.RemotePort.Text}" : "本地探针 " + watch.ProbeSerial;
            ObservationPage.ConfigurationSummary.Text = $"{loaded.Name} / {target.Name} · {connection} · {watch.Rate} Hz · {watch.Identities.Length} 个变量 · RTT {(useRtt ? "开启" : "关闭")}";
            ObservationPage.RttEmpty.Text = useRtt ? "等待本地 RTT 输出" : "工程设置未启用本地 RTT";
            ObservationPage.VariablesEmpty.Text = "本工程仅观察 RTT\n未配置观察变量。";
            ObservationPage.PlotEmpty.Text = watch.Identities.Length == 0 ? "本工程仅观察 RTT，未启用变量曲线" : "开始采集后显示曲线";
            ObservationPage.ObservationStatus.Text = "已读取工程设置 · 未连接 · ELF 与固件匹配未核验";
            ObservationPage.StartButton.IsEnabled = true;
            Report("已读取工程观察设置；尚未连接设备。");
        }
        catch (Exception ex) { ShowViewerIssue(ex.Message, true, destination); }
        finally { ProjectPage.TargetBox.IsEnabled = observation == null; EnableConnectionFields(observation == null); }
    }

    private void SaveObservationSettings(object? sender, RoutedEventArgs e)
    {
        try
        {
            if (observation != null || transition || !viewerPreparation.IsCompleted) throw new InvalidOperationException("请先停止采集并等待当前操作完成，再保存工程设置。");
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

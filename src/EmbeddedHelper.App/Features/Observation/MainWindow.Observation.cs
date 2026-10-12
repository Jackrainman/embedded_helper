using System.Collections.ObjectModel;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Text;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Shapes;
using EmbeddedHelper.Core;
using Microsoft.Win32;

namespace EmbeddedHelper.App;

public partial class MainWindow
{
    private readonly ObservationViewModel observationView = new();
    public ObservableCollection<VariableRow> Variables => observationView.Variables;
    private ObservationSession? observation;
    private readonly Decoder rttDecoder = Encoding.UTF8.GetDecoder();
    private readonly StringBuilder rttHistory = new();
    private DateTime elfStamp;
    private long samples;
    private readonly Stopwatch sampling = new();

    private async void BrowseElf(object? sender, RoutedEventArgs e)
    {
        if (observation != null || transition || !viewerPreparation.IsCompleted) { Report("请先停止采集或等待当前解析结束再更换 ELF。"); return; }
        var dialog = new OpenFileDialog { Filter = "固件 ELF|*.elf;*.axf|所有文件|*.*" };
        if (dialog.ShowDialog(this) == true) { ProjectPage.ElfPath.Text = dialog.FileName; await Guard(ParseElfAsync); }
    }
    private async void ParseElf(object? sender, RoutedEventArgs e) => await Guard(ParseElfAsync);
    private Task ParseElfAsync() => ParseElfAsync(false);
    private async Task ParseElfAsync(bool preparingViewer)
    {
        if (transition || (!preparingViewer && !viewerPreparation.IsCompleted)) throw new InvalidOperationException("正在切换连接或读取计划，请稍候。");
        transition = true; ProjectPage.TargetBox.IsEnabled = false;
        try
        {
            var path = ProjectPage.ElfPath.Text;
            var tool = SettingsPage.ReadElfPath.Text;
            var indexed = await ElfIndexService.ReadAsync(path, tool);
            var selected = Variables.Where(v => v.Selected).Select(v => v.Variable.Identity).ToHashSet();
            if (selected.Count == 0 && project != null)
            {
                var watch = await WatchSettingsStore.LoadAsync(project.Root);
                if (watch != null && watch.Target == Target?.Name) selected.UnionWith(watch.Identities);
            }
            var missing = Variables.Where(v => v.Selected && !indexed.Any(n => n.Identity == v.Variable.Identity)).Select(v => v.Variable with { Status = "新 ELF 中缺失，地址未使用" }).ToArray();
            var rows = indexed.Concat(missing).Select(v => new VariableRow(v) { Selected = selected.Contains(v.Identity) }).ToArray();
            if (observation != null)
            {
                await observation.UpdateAsync(rows.Where(v => v.Selected && v.Variable.Supported).Select(v => v.Variable).ToArray(), int.Parse(ObservationSetupPage.SampleRate.Text, CultureInfo.InvariantCulture));
                samples = 0; sampling.Restart(); rttDecoder.Reset(); rttHistory.Clear();
            }
            Variables.Clear();
            foreach (var row in rows) Variables.Add(row);
            elfStamp = File.GetLastWriteTimeUtc(path);
            RefreshViewerRows();
            ObservationPage.ObservationStatus.Text = $"ELF 已解析：{indexed.Length} 项，{indexed.Count(v => v.Supported)} 项可读取 · 与目标固件匹配未核验";
            Report(observation == null ? "变量索引完成；地址来自 ELF，尚未连接硬件。" : $"读计划第 {observation.Generation} 代已在批次边界应用；新曲线重新开始，缺失项标为无效。固件与 ELF 仍未核验。");
        }
        finally { transition = false; ProjectPage.TargetBox.IsEnabled = observation == null; }
    }
    private async void StartObservation(object? sender, RoutedEventArgs e) => await Guard(async () =>
    {
        if (transition) throw new InvalidOperationException("正在切换连接，请稍候。");
        if (observation?.CanRetry == true) await StopObservationAsync();
        if (observation != null) throw new InvalidOperationException("请先停止已有采集。");
        if (Target is not { } target) throw new InvalidOperationException("请先选择工程目标。");
        var source = project;
        var variables = Variables.Where(v => v.Selected).Select(v => v.Variable).ToArray();
        await PrepareViewerAsync();
        if (preparedWatch == null) return;
        if (Target != target || project != source) { InvalidateViewer(); return; }
        variables = Variables.Where(v => v.Selected).Select(v => v.Variable).ToArray();
        transition = true; ObservationPage.StartButton.IsEnabled = false; ProjectPage.TargetBox.IsEnabled = false; EnableConnectionFields(false);
        var candidate = new ObservationSession();
        observation = candidate;
        try
        {
            var remote = ObservationSetupPage.TransportBox.SelectedIndex == 1;
            var request = new ObservationRequest(SettingsPage.DllPath.Text, int.TryParse(ObservationSetupPage.ProbeSerial.Text, out var number) ? number : 0,
                remote ? RemotePage.RemoteHost.Text : "", int.Parse(RemotePage.RemotePort.Text, CultureInfo.InvariantCulture), target.Device, target.Interface, target.SpeedKHz,
                int.Parse(ObservationSetupPage.SampleRate.Text, CultureInfo.InvariantCulture), ObservationSetupPage.EnableRtt.IsChecked == true && !remote, variables);
            await candidate.StartAsync(RuntimePaths.Current.WorkerExecutable, request);
            samples = 0; sampling.Restart(); rttDecoder.Reset(); rttHistory.Clear();
            foreach (var row in Variables) { row.History.Clear(); row.LatestValue = null; row.LastObservedAt = null; row.Value = row.Variable.Supported ? "等待新会话数据" : row.Variable.Status; }
            PersistSettings();
        }
        catch { await candidate.DisposeAsync(); observation = null; throw; }
        finally { transition = false; ObservationPage.StartButton.IsEnabled = observation == null && preparedWatch != null; ObservationPage.StopButton.IsEnabled = observation != null; ProjectPage.TargetBox.IsEnabled = observation == null; EnableConnectionFields(observation == null); }
    });
    private async void StopObservation(object? sender, RoutedEventArgs e) => await Guard(StopObservationAsync);
    private async Task StopObservationAsync()
    {
        if (transition) return;
        transition = true;
        try
        {
            if (observation != null) await observation.DisposeAsync();
            observation = null; sampling.Stop();
            foreach (var row in Variables.Where(v => v.Selected)) row.Value = "已停止 / 过期";
            ObservationPage.ObservationStatus.Text = "已释放 · 可使用外部烧录/调试工具";
            ObservationPage.StartButton.IsEnabled = preparedWatch != null; ObservationPage.StopButton.IsEnabled = false; ProjectPage.TargetBox.IsEnabled = true; EnableConnectionFields(true);
        }
        finally { transition = false; }
    }
    private void EnableConnectionFields(bool enabled)
    {
        ObservationSetupPage.IsEnabled = enabled;
        foreach (var field in new UIElement[] { SettingsPage.DllPath, RemotePage.RemoteHost, RemotePage.RemotePort }) field.IsEnabled = enabled;
    }
    private static string Decode(Decoder decoder, byte[] data)
    { var chars = new char[Encoding.UTF8.GetMaxCharCount(data.Length)]; var count = decoder.GetChars(data, chars, false); return new string(chars, 0, count); }
    private static void AppendBounded(TextBox box, string text)
    { if (box.Text.Length + text.Length > 65536) box.Text = box.Text[Math.Min(box.Text.Length, 32768)..]; box.AppendText(text); box.ScrollToEnd(); }
    private void DrawPlot()
    {
        ObservationPage.Plot.Children.Clear();
        var width = ObservationPage.Plot.ActualWidth; var height = ObservationPage.Plot.ActualHeight;
        if (width < 5 || height < 5) return;
        var rows = Variables.Where(v => v.Selected && v.History.Count > 1).Take(16).ToArray();
        var all = rows.SelectMany(r => r.History).ToArray();
        ObservationPage.PlotEmpty.Visibility = all.Length == 0 ? Visibility.Visible : Visibility.Collapsed;
        if (all.Length == 0) return;
        var min = all.Min(); var range = Math.Max(0.001, all.Max() - min);
        Brush[] colors = [Brushes.DeepSkyBlue, Brushes.MediumAquamarine, Brushes.Gold, Brushes.Orchid];
        for (int i = 0; i < rows.Length; i++)
        {
            var values = rows[i].History.ToArray();
            var line = new Polyline { Stroke = colors[i % colors.Length], StrokeThickness = 1.5 };
            for (var n = 0; n < values.Length; n++) line.Points.Add(new Point(n * width / Math.Max(1, values.Length - 1), height - 8 - (values[n] - min) / range * (height - 16)));
            ObservationPage.Plot.Children.Add(line);
        }
    }
    private void PlotSizeChanged(object? sender, SizeChangedEventArgs e) => DrawPlot();
    private void ClearRtt(object? sender, RoutedEventArgs e) { rttHistory.Clear(); ObservationPage.RttText.Clear(); ObservationPage.RttEmpty.Visibility = Visibility.Visible; }
    private void RefreshObservation()
    {
        if (observation != null)
        {
            while (observation.Events.TryRead(out var item))
            {
                if (item.SessionId != observation.SessionId || item.Generation != observation.Generation) continue;
                if (item.Kind != "sample") { if (item.Message.Length > 0) Report(item.Message); continue; }
                samples++;
                if (item.Values != null) foreach (var row in Variables)
                    if (item.Values.TryGetValue(row.Variable.Identity, out var value))
                    {
                        row.LatestValue = value; row.LastObservedAt = item.ObservedAt;
                        if (double.TryParse(value, CultureInfo.InvariantCulture, out var number) && double.IsFinite(number))
                        { row.History.Enqueue(number); if (row.History.Count > 2000) row.History.Dequeue(); }
                    }
                if (item.RttBase64 != null)
                {
                    rttHistory.Append(Decode(rttDecoder, Convert.FromBase64String(item.RttBase64)));
                    if (rttHistory.Length > 65536) rttHistory.Remove(0, rttHistory.Length - 65536);
                }
            }
            ObservationPage.ObservationStatus.Text = $"{observation.State} · 第 {observation.Generation} 代 · 接收批次 {samples / Math.Max(1, sampling.Elapsed.TotalSeconds):F1} Hz · 显示缺口 {observation.Dropped}";
            ObservationPage.StartButton.IsEnabled = !transition && observation.CanRetry;
            ObservationPage.StartButton.Content = observation.CanRetry ? "清理并重试" : "开始采集";
            if (ObservationPage.PauseObservation.IsChecked != true)
            {
                foreach (var row in Variables.Where(v => v.LatestValue != null)) row.Value = row.LatestValue + (observation.CanRetry || DateTimeOffset.UtcNow - row.LastObservedAt > TimeSpan.FromSeconds(3) ? " / 过期" : "");
                ObservationPage.RttText.Text = rttHistory.ToString(); ObservationPage.RttText.ScrollToEnd(); ObservationPage.RttEmpty.Visibility = rttHistory.Length == 0 ? Visibility.Visible : Visibility.Collapsed; DrawPlot();
            }
        }
        if (!string.IsNullOrEmpty(ProjectPage.ElfPath.Text) && elfStamp != default && File.GetLastWriteTimeUtc(ProjectPage.ElfPath.Text) != elfStamp)
            ObservationPage.ObservationStatus.Text += " · ELF 已变化，当前地址未替换";
    }
}

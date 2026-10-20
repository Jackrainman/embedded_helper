using System.Collections.ObjectModel;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Text;
using System.Windows;
using System.Windows.Controls;
using EmbeddedHelper.Core;
using Microsoft.Win32;

namespace EmbeddedHelper.App;

public partial class MainWindow
{
    private readonly ObservationViewModel observationView = new();
    public ObservableCollection<VariableRow> Variables => observationView.Variables;
    private readonly ObservationCoordinator acquisition = new();
    private readonly Decoder rttDecoder = Encoding.UTF8.GetDecoder();
    private readonly StringBuilder rttHistory = new();
    private DateTime elfStamp;
    private bool indexing, displayDirty, rttDirty, wasPaused;
    private long samples;
    private readonly Stopwatch sampling = new();
    private bool ObservationBusy => indexing || loadingProject || acquisition.IsBusy || preparing || shutdownRequested;

    private void SyncObservationState()
    {
        observationView.Busy = ObservationBusy || configurationBusy || (rttSession && acquisition.HasSession);
        observationView.HasSession = acquisition.HasSession && !rttSession;
        observationView.CanRetry = acquisition.CanRetry && !rttSession;
        RttPage.StartButton.IsEnabled = !ObservationBusy && !configurationBusy && (!acquisition.HasSession || (rttSession && acquisition.CanRetry));
        RttPage.StopButton.IsEnabled = rttSession && acquisition.HasSession && !acquisition.IsBusy;
        ProjectPage.TargetBox.IsEnabled = observationView.CanEdit;
        EnableConnectionFields(observationView.CanEdit);
        UpdateProjectHeader();
    }
    private async void BrowseElf(object? sender, RoutedEventArgs e)
    {
        if (acquisition.HasSession || ObservationBusy) { Report("请先停止采集或等待当前解析结束再更换 ELF。"); return; }
        var dialog = new OpenFileDialog { Filter = "固件 ELF|*.elf;*.axf|所有文件|*.*" };
        if (dialog.ShowDialog(this) == true) { ProjectPage.ElfPath.Text = dialog.FileName; await Guard(ParseElfAsync); }
    }
    private async void ParseElf(object? sender, RoutedEventArgs e) => await Guard(ParseElfAsync);
    private async Task ParseElfAsync()
    {
        if ((rttSession && acquisition.HasSession) || ObservationBusy || configurationBusy) throw new InvalidOperationException("正在切换连接或读取计划，请稍候。");
        indexing = true; SyncObservationState();
        try
        {
            var path = ProjectPage.ElfPath.Text;
            var indexed = await ElfIndexService.ReadAsync(path, SettingsPage.ReadElfPath.Text);
            var selected = Variables.Where(v => v.Selected).Select(v => v.Variable.Identity).ToHashSet();
            if (selected.Count == 0 && project != null)
            {
                var watch = await WatchSettingsStore.LoadAsync(project.Root);
                if (watch != null && watch.Target == Target?.Name) selected.UnionWith(watch.Identities);
            }
            var missing = Variables.Where(v => v.Selected && !indexed.Any(n => n.Identity == v.Variable.Identity)).Select(v => v.Variable with { Status = "新 ELF 中缺失，地址未使用" });
            var rows = indexed.Concat(missing).Select(v => new VariableRow(v) { Selected = selected.Contains(v.Identity) }).ToArray();
            if (acquisition.HasSession)
            {
                await acquisition.UpdateAsync(rows.Where(v => v.Selected && v.Variable.Supported).Select(v => v.Variable).ToArray(), prepared!.Request.SampleRate);
                ResetObservationDisplay();
            }
            observationView.ReplaceRows(rows);
            elfStamp = File.GetLastWriteTimeUtc(path);
            RefreshProjectDiagnostics(path);
            RefreshViewerRows(); displayDirty = true;
            observationView.Status = $"ELF 已解析：{indexed.Length} 项，{indexed.Count(v => v.Supported)} 项可读取 · 与目标固件匹配未核验";
            Report(!acquisition.HasSession ? "变量索引完成；地址来自 ELF，尚未连接硬件。" : $"读计划第 {acquisition.Generation} 代已在批次边界应用；新曲线重新开始，缺失项标为无效。固件与 ELF 仍未核验。");
        }
        finally { indexing = false; SyncObservationState(); }
    }
    private async void StartObservation(object? sender, RoutedEventArgs e) => await Guard(async () =>
    {
        if ((rttSession && acquisition.HasSession) || ObservationBusy || configurationBusy) throw new InvalidOperationException("正在执行其他操作，请稍候。");
        if (acquisition.CanRetry) await StopObservationAsync();
        if (acquisition.HasSession) throw new InvalidOperationException("请先停止已有采集。");
        await PrepareViewerAsync();
        var ready = prepared;
        if (ready == null || shutdownRequested) return;
        rttSession = false;
        connectionFailure = "";
        try { await acquisition.StartAsync(RuntimePaths.Current.WorkerExecutable, ready.Request); }
        catch (Exception ex)
        {
            connectionFailure = ex.Message;
            observationView.Status = "连接失败：" + ex.Message + "；可重试连接。";
            SyncObservationState();
            throw;
        }
        if (!acquisition.HasSession || shutdownRequested) return;
        ResetObservationDisplay();
        foreach (var row in Variables) { row.History.Clear(); row.LatestValue = null; row.LastObservedAt = null; row.Value = row.Variable.Supported ? "等待新会话数据" : row.Variable.Status; }
        displayDirty = rttDirty = true;
    });
    private void ResetObservationDisplay()
    { samples = 0; sampling.Restart(); rttDecoder.Reset(); rttHistory.Clear(); displayDirty = rttDirty = true; }
    private async void StopObservation(object? sender, RoutedEventArgs e) => await Guard(StopObservationAsync);
    private async Task StopObservationAsync()
    {
        await acquisition.StopAsync();
        sampling.Stop();
        foreach (var row in Variables.Where(v => v.Selected)) row.Value = "已停止 / 过期";
        if (rttSession) RttPage.Status.Text = "已释放 · 可使用外部烧录/调试工具";
        else observationView.Status = "已释放 · 可使用外部烧录/调试工具";
        if (!acquisition.HasSession) rttSession = false;
        SyncObservationState();
    }
    private void EnableConnectionFields(bool enabled)
    {
        ConnectionPage.IsEnabled = enabled;
        foreach (var field in new UIElement[] { SettingsPage.DllPath, SettingsPage.ReadElfPath, ProjectPage.ElfPath, RemotePage.RemoteHost, RemotePage.RemotePort }) field.IsEnabled = enabled;
    }
    private static string Decode(Decoder decoder, byte[] data)
    { var chars = new char[Encoding.UTF8.GetMaxCharCount(data.Length)]; var count = decoder.GetChars(data, chars, false); return new string(chars, 0, count); }
    private static void AppendBounded(TextBox box, string text)
    { if (box.Text.Length + text.Length > 65536) box.Text = box.Text[Math.Min(box.Text.Length, 32768)..]; box.AppendText(text); box.ScrollToEnd(); }
    private void ClearRtt(object? sender, RoutedEventArgs e) { rttHistory.Clear(); RttPage.RttText.Text = ""; rttDirty = false; }
    private void RefreshObservation()
    {
        SyncObservationState();
        if (acquisition.HasSession)
        {
            var started = Stopwatch.GetTimestamp();
            var byIdentity = Variables.ToDictionary(v => v.Variable.Identity);
            for (var count = 0; count < 256 && Stopwatch.GetElapsedTime(started).TotalMilliseconds < 8 && acquisition.TryRead(out var item); count++)
            {
                if (item == null) continue;
                if (item.Kind != "sample") { if (item.Message.Length > 0) Report(item.Message); continue; }
                samples++;
                if (item.Values != null) foreach (var (identity, value) in item.Values)
                    if (byIdentity.TryGetValue(identity, out var row))
                    {
                        row.LatestValue = value; row.LastObservedAt = item.ObservedAt;
                        if (double.TryParse(value, CultureInfo.InvariantCulture, out var number) && double.IsFinite(number))
                        { row.History.Enqueue(number); if (row.History.Count > 2000) row.History.Dequeue(); displayDirty = true; }
                    }
                if (item.RttBase64 != null)
                {
                    rttHistory.Append(Decode(rttDecoder, Convert.FromBase64String(item.RttBase64)));
                    if (rttHistory.Length > 65536) rttHistory.Remove(0, rttHistory.Length - 65536);
                    rttDirty = true;
                }
            }
            var status = $"{acquisition.State} · 第 {acquisition.Generation} 代 · 接收批次 {samples / Math.Max(1, sampling.Elapsed.TotalSeconds):F1} Hz · 显示缺口 {acquisition.Dropped}";
            if (rttSession) { RttPage.Status.Text = status; observationView.Status = "RTT 正在占用连接，请先到 RTT 监视停止并释放。"; }
            else observationView.Status = status;
            if (rttDirty && RttPage.IsVisible) { RttPage.RttText.Text = rttHistory.ToString(); rttDirty = false; }
            if (!rttSession && !observationView.Paused && ObservationPage.IsVisible)
            {
                foreach (var row in Variables.Where(v => v.LatestValue != null)) row.Value = row.LatestValue + (acquisition.CanRetry || DateTimeOffset.UtcNow - row.LastObservedAt > TimeSpan.FromSeconds(3) ? " / 过期" : "");
                if (displayDirty || wasPaused) { var plotted = Variables.Where(v => v.Selected && v.History.Count > 1).Take(16).ToArray(); observationView.PlotLabels = plotted.Select(v => v.Variable.Name).ToArray(); observationView.Series = plotted.Select(v => v.History.ToArray()).ToArray(); displayDirty = false; }
            }
            wasPaused = observationView.Paused;
        }
        if (!string.IsNullOrEmpty(ProjectPage.ElfPath.Text) && elfStamp != default && File.GetLastWriteTimeUtc(ProjectPage.ElfPath.Text) != elfStamp && !observationView.Status.Contains("ELF 已变化", StringComparison.Ordinal))
            observationView.Status += " · ELF 已变化，当前地址未替换";
    }
}

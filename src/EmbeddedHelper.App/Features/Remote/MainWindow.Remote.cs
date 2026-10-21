using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Windows;
using EmbeddedHelper.Core;

namespace EmbeddedHelper.App;

public partial class MainWindow
{
    private readonly RemoteViewModel remoteView = new();
    private ConfigPlan? plan => remoteView.Plan;
    private bool configurationBusy;
    private readonly ConfigurationService configuration = new(App.Data.Root);
    private ConfigPlan? toolPlan;
    private ToolRelease? planRelease;
    private string? planZip;

    private void PreviewConfig(object? sender, RoutedEventArgs e)
    {
        try
        {
            ShowPlan(RemoteConfiguration.Plan(project ?? throw new InvalidOperationException("请先打开工程。"), Target ?? throw new InvalidOperationException("请选择目标。"), remoteView.Host, int.Parse(remoteView.Port, CultureInfo.InvariantCulture), remoteView.Web));
        }
        catch (Exception ex) { remoteView.Invalidate(); Report(ex.Message); }
    }
    private async void ApplyConfig(object? sender, RoutedEventArgs e) => await Guard(ApplyConfigAsync);
    private async Task ApplyConfigAsync()
    {
        if (acquisition.HasSession || ObservationBusy || configurationBusy) throw new InvalidOperationException("请先停止采集并等待当前操作完成。");
        if (toolBusy) throw new InvalidOperationException("工具正在准备，请稍候。");
        var ready = plan ?? throw new InvalidOperationException("请先预览修改。");
        if (ready.Root != project?.Root) throw new InvalidOperationException("工程已变化，请重新预览。");
        configurationBusy = true; ++integrationStatusRevision; remoteView.Busy = true; SyncObservationState();
        try
        {
            if (ready == toolPlan && planRelease is { } release)
            {
                remoteView.ToolStatus = "正在准备 LAN_Debug " + release.Version + "…";
                await EnsureProjectToolAsync(ready.Root, release, planZip);
                remoteView.ToolStatus = "工具已准备，正在写入工程配置…";
            }
            await configuration.ApplyAsync(ready);
            remoteView.Invalidate(); InvalidateViewer();
            remoteView.Preview = "已应用预览中的配置。保留本地条目；未执行构建、烧录或调试。";
            Report(remoteView.Preview);
            await RefreshIntegrationStatusAsync(afterApply: true);
        }
        catch (Exception ex) { remoteView.ToolStatus = "接入未完成：" + ex.Message + "；可修正后重试。"; throw; }
        finally { configurationBusy = false; remoteView.Busy = false; SyncObservationState(); await RefreshProjectConnectionAsync(); }
    }
    private void PreviewIntegration(object? sender, RoutedEventArgs e)
    {
        try
        {
            var loaded = project ?? throw new InvalidOperationException("请先打开工程。");
            var release = remoteView.SelectedRelease ?? throw new InvalidOperationException("请选择 LAN_Debug 版本。");
            var candidate = VsCodeIntegration.Plan(loaded, Target ?? throw new InvalidOperationException("请选择目标。"), remoteView.Host, int.Parse(remoteView.Port, CultureInfo.InvariantCulture), int.Parse(remoteView.GdbPort, CultureInfo.InvariantCulture), release, remoteView.EideUpload, remoteView.Web);
            ShowPlan(candidate); toolPlan = candidate; planRelease = release; planZip = null;
        }
        catch (Exception ex) { remoteView.Invalidate(); Report(ex.Message); }
    }
    private void PreviewRemoveIntegration(object? sender, RoutedEventArgs e)
    {
        try { ShowPlan(VsCodeIntegration.Remove(project?.Root ?? throw new InvalidOperationException("请先打开工程。"))); }
        catch (Exception ex) { remoteView.Invalidate(); Report(ex.Message); }
    }
    private void ShowPlan(ConfigPlan candidate)
    {
        if (configurationBusy || shutdownRequested) throw new InvalidOperationException("当前配置操作尚未结束。");
        remoteView.ShowPlan(candidate);
    }
    private async void RecoverConfig(object? sender, RoutedEventArgs e) => await Guard(async () =>
    {
        if (acquisition.HasSession || ObservationBusy || configurationBusy) throw new InvalidOperationException("请先停止采集并等待当前操作完成。");
        var root = project?.Root ?? throw new InvalidOperationException("请先打开工程。");
        configurationBusy = true; remoteView.Busy = true; SyncObservationState();
        try
        {
            var conflicts = await configuration.RecoverAsync(root);
            remoteView.Invalidate();
            remoteView.Preview = conflicts.Length == 0 ? "已检查未完成事务，能确认归属的写入已恢复。" : "以下文件有外部修改，未覆盖；请按事务备份人工处理：\n" + string.Join("\n", conflicts) + "\n备份：" + configuration.JournalFor(root);
            InvalidateViewer(); Report(remoteView.Preview);
        }
        finally { configurationBusy = false; remoteView.Busy = false; SyncObservationState(); await RefreshProjectConnectionAsync(); }
    });
    private async void OpenWeb(object? sender, RoutedEventArgs e)
    {
        Pages.SelectedIndex = 2;
        await RefreshRttConnectionAsync();
    }
}

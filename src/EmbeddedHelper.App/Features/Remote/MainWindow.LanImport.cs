using System.IO;
using System.Windows;
using EmbeddedHelper.Core;
using Microsoft.Win32;

namespace EmbeddedHelper.App;

public partial class MainWindow
{
    private int integrationStatusRevision;
    private async void ImportLanZip(object? sender, RoutedEventArgs e)
    {
        var dialog = new OpenFileDialog { Filter = "LAN_Debug 官方发布包|DebugLAN-*.zip|ZIP|*.zip" };
        if (dialog.ShowDialog(this) == true) await Guard(() => ImportLanAsync(dialog.FileName));
    }
    private async Task ImportLanAsync(string? zip)
    {
        if (configurationBusy || toolBusy || ObservationBusy || acquisition.HasSession) throw new InvalidOperationException("请等待当前操作完成并停止采集。");
        // Detect project conflicts before downloading or preparing any files.
        PreviewIntegration(null, new RoutedEventArgs());
        if (plan == null) { remoteView.ToolStatus = "尚未导入：" + StatusLabel.Text; return; }
        planZip = zip;
        try { await ApplyConfigAsync(); }
        finally { planZip = null; }
    }
    private static async Task EnsureProjectToolAsync(string root, ToolRelease release, string? zip)
    {
        if (zip != null) await ToolPreparationService.PrepareAsync(App.Data.Root, release, zip);
        else
        {
            try { await Task.Run(() => new ToolCache(App.Data.Root).Resolve(release)); }
            catch (FileNotFoundException) { await ToolPreparationService.PrepareAsync(App.Data.Root, release, null); }
        }
        await Task.Run(() => ProjectToolCache.Prepare(root, App.Data.Root, release));
    }
    private async Task RefreshIntegrationStatusAsync(bool afterApply = false)
    {
        if (configurationBusy && !afterApply) return;
        var revision = ++integrationStatusRevision;
        var loaded = project; var selected = remoteView.SelectedRelease;
        if (selected == null) return;
        var status = await Task.Run(() =>
        {
            var available = "包可用 " + selected.Version;
            try { new ToolCache(App.Data.Root).Resolve(selected); available += " · 本机已准备"; }
            catch (FileNotFoundException) { available += " · 本机未准备（导入时自动下载）"; }
            catch (Exception ex) { available += " · 本机工具需检查：" + ex.Message; }
            if (loaded == null) return available + "\n请先打开工程。";
            try
            {
                var state = VsCodeIntegration.ReadState(loaded.Root);
                if (state == null || state.Entries.Length == 0) return available + "\n工程尚未接入。";
                _ = VsCodeIntegration.Remove(loaded.Root); // Read-only ownership check; do not report externally edited tasks as healthy.
                var pinned = ToolCache.ReadLock(loaded.Root);
                if (!state.Entries.Any(e => e.Value["command"]?.GetValue<string>() == "${workspaceFolder}/" + ProjectToolCache.RelativeEntry(pinned)))
                    return available + "\n工程使用旧版入口；点击导入可更新到 LAN_Debug。";
                ProjectToolCache.Resolve(loaded.Root, pinned);
                return available + $"\n工程已接入 {pinned.Version} · 目标 {state.Target}" + (pinned != selected ? "；所选版本尚未应用。" : "。");
            }
            catch (Exception ex) { return available + "\n工程接入待修复：" + ex.Message; }
        });
        if (revision == integrationStatusRevision && project == loaded && remoteView.SelectedRelease == selected) remoteView.ToolStatus = status;
    }
}

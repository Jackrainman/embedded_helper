using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Windows;
using EmbeddedHelper.Core;

namespace EmbeddedHelper.App;

public partial class MainWindow
{
    private ConfigPlan? plan;
    private readonly ConfigurationService configuration = new(App.Data.Root);

    private void PreviewConfig(object? sender, RoutedEventArgs e)
    {
        try
        {
            plan = RemoteConfiguration.Plan(project ?? throw new InvalidOperationException("请先打开工程。"), Target ?? throw new InvalidOperationException("请选择目标。"), RemotePage.RemoteHost.Text, int.Parse(RemotePage.RemotePort.Text, CultureInfo.InvariantCulture), RemotePage.WebAddress.Text);
            RemotePage.ConfigPreview.Text = string.Join("\n\n", plan.Changes.Select(c => c.Path + "\n" + c.After));
            RemotePage.ApplyConfigButton.IsEnabled = true;
        }
        catch (Exception ex) { plan = null; RemotePage.ApplyConfigButton.IsEnabled = false; Report(ex.Message); }
    }
    private async void ApplyConfig(object? sender, RoutedEventArgs e) => await Guard(ApplyConfigAsync);
    private async Task ApplyConfigAsync()
    {
        if (observation != null || transition) throw new InvalidOperationException("请先停止采集再修改连接配置。");
        if (plan == null) throw new InvalidOperationException("请先预览修改。");
        var ready = plan;
        await configuration.ApplyAsync(ready);
        plan = null; RemotePage.ApplyConfigButton.IsEnabled = false; Report("已应用预览中的配置。保留本地条目；未执行构建、烧录或调试。VS Code 入口需先在设置页绑定。");
    }
    private void PreviewIntegration(object? sender, RoutedEventArgs e)
    {
        try
        {
            var loaded = project ?? throw new InvalidOperationException("请先打开工程。");
            var release = SettingsPage.ToolVersionBox.SelectedItem as ToolRelease ?? throw new InvalidOperationException("请在设置页选择工具版本。");
            new ToolCache(App.Data.Root).Resolve(release);
            ShowPlan(VsCodeIntegration.Plan(loaded, Target ?? throw new InvalidOperationException("请选择目标。"), RemotePage.RemoteHost.Text, int.Parse(RemotePage.RemotePort.Text, CultureInfo.InvariantCulture), int.Parse(RemotePage.GdbPort.Text, CultureInfo.InvariantCulture), release, RemotePage.UseEideUpload.IsChecked == true, RemotePage.WebAddress.Text));
        }
        catch (Exception ex) { plan = null; RemotePage.ApplyConfigButton.IsEnabled = false; Report(ex.Message); }
    }
    private void PreviewRemoveIntegration(object? sender, RoutedEventArgs e)
    {
        try { ShowPlan(VsCodeIntegration.Remove(project?.Root ?? throw new InvalidOperationException("请先打开工程。"))); }
        catch (Exception ex) { plan = null; RemotePage.ApplyConfigButton.IsEnabled = false; Report(ex.Message); }
    }
    private void ShowPlan(ConfigPlan candidate)
    {
        plan = candidate;
        RemotePage.ConfigPreview.Text = candidate.Changes.Length == 0 ? "无需修改；当前配置已一致。" : string.Join("\n\n", candidate.Changes.Select(c => c.Path + "\n原内容：\n" + (c.Before ?? "（不存在）") + "\n修改后：\n" + c.After));
        RemotePage.ApplyConfigButton.IsEnabled = candidate.Changes.Length > 0;
    }
    private async void RecoverConfig(object? sender, RoutedEventArgs e) => await Guard(async () =>
    {
        var root = project?.Root ?? throw new InvalidOperationException("请先打开工程。");
        var conflicts = await configuration.RecoverAsync(root);
        plan = null; RemotePage.ApplyConfigButton.IsEnabled = false;
        RemotePage.ConfigPreview.Text = conflicts.Length == 0 ? "已检查未完成事务，能确认归属的写入已恢复。" : "以下文件有外部修改，未覆盖；请按事务备份人工处理：\n" + string.Join("\n", conflicts) + "\n备份：" + configuration.JournalFor(root);
        Report(RemotePage.ConfigPreview.Text);
    });
    private async void OpenWeb(object? sender, RoutedEventArgs e) => await Guard(async () =>
    {
        if (!Uri.TryCreate(RemotePage.WebAddress.Text, UriKind.Absolute, out var uri) || uri.Scheme is not ("http" or "https")) throw new ArgumentException("请输入完整 http:// 或 https:// 网页地址。");
        if (transition) throw new InvalidOperationException("连接正在切换，请稍后重试。");
        await StopObservationAsync();
        if (observation != null) throw new IOException("采集连接尚未释放，未打开网页。");
        Process.Start(new ProcessStartInfo(uri.AbsoluteUri) { UseShellExecute = true });
        Report("已释放客户端采集并打开网页；返回远程变量前，请先在网页手动停止 RTT。");
    });
}

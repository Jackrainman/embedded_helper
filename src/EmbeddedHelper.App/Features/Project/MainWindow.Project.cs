using System.Globalization;
using System.IO;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using EmbeddedHelper.Core;
using Microsoft.Win32;

namespace EmbeddedHelper.App;

public partial class MainWindow
{
    private ProjectInfo? project;
    private BuildTarget? Target => ProjectPage.TargetBox.SelectedItem as BuildTarget;

    private void BrowseProject(object? sender, RoutedEventArgs e)
    {
        var dialog = new OpenFolderDialog { Title = "选择 EIDE 工程目录" };
        if (dialog.ShowDialog(this) == true) ProjectPage.ProjectPath.Text = dialog.FolderName;
    }
    private async void OpenProject(object? sender, RoutedEventArgs e) => await Guard(() => LoadProjectAsync(ProjectPage.ProjectPath.Text));
    private async void OpenRecent(object? sender, System.Windows.Input.MouseButtonEventArgs e)
    { if (ProjectPage.RecentProjects.SelectedItem is string path) { ProjectPage.ProjectPath.Text = path; await Guard(() => LoadProjectAsync(path)); } }
    private async Task LoadProjectAsync(string path)
    {
        if (observation != null || transition) throw new InvalidOperationException("请先停止采集再切换工程。");
        var loaded = await Task.Run(() => ProjectReader.Read(path));
        project = loaded;
        ProjectPage.ProjectPath.Text = loaded.Root;
        ProjectPage.ProjectSummary.Text = loaded.Name + " · " + loaded.Family;
        ProjectPage.McuLabel.Text = "MCU：" + loaded.Mcu + "\n来源：.eide/eide.yml";
        ProjectPage.ProjectDiagnostics.Text = string.Join("\n", loaded.Diagnostics.Prepend("读取完成；未运行工程钩子、构建或设备命令。"));
        ProjectPage.TargetBox.ItemsSource = loaded.Targets;
        ProjectPage.TargetBox.SelectedIndex = loaded.Targets.Length == 1 ? 0 : -1;
        var watch = await WatchSettingsStore.LoadAsync(loaded.Root);
        if (watch != null)
        {
            ProjectPage.TargetBox.SelectedItem = loaded.Targets.FirstOrDefault(t => t.Name == watch?.Target);
        }
        RemotePage.RemoteHost.Text = ""; RemotePage.RemotePort.Text = "19011"; RemotePage.WebAddress.Text = "";
        var integration = VsCodeIntegration.ReadState(loaded.Root);
        RemotePage.GdbPort.Text = (integration?.GdbPort ?? 2331).ToString(CultureInfo.InvariantCulture);
        RemotePage.UseEideUpload.IsChecked = integration?.EideFields?.Length > 0;
        if (File.Exists(System.IO.Path.Combine(loaded.Root, ".embedded_helper", "tools.lock.json")))
            SettingsPage.ToolVersionBox.SelectedItem = ToolCache.ReadLock(loaded.Root);
        if (File.Exists(loaded.RemotePath))
        {
            using var remote = JsonDocument.Parse(await File.ReadAllTextAsync(loaded.RemotePath), ProjectReader.JsonOptions);
            RemotePage.RemoteHost.Text = remote.RootElement.GetProperty("ServerName").GetString();
            RemotePage.RemotePort.Text = remote.RootElement.GetProperty("Port").GetInt32().ToString(CultureInfo.InvariantCulture);
            RemotePage.WebAddress.Text = remote.RootElement.TryGetProperty("WebUrl", out var web) ? web.GetString() ?? "" : "";
        }
        settings = settings with { Recent = new[] { loaded.Root }.Concat(settings.Recent ?? []).Distinct(StringComparer.OrdinalIgnoreCase).Take(8).ToArray() };
        ProjectPage.RecentProjects.ItemsSource = settings.Recent;
        PersistSettings();
        plan = null; RemotePage.ApplyConfigButton.IsEnabled = false;
        Report("已打开 " + loaded.Name + "。请选择 ELF 解析变量，或维护远程配置。");
        if (configuration.HasPending(loaded.Root)) Report("该工程有未完成写入，请到远程配置页恢复；外部修改不会被覆盖。");
    }
    private void TargetChanged(object? sender, SelectionChangedEventArgs e)
    {
        if (Target is not { } target) { ProjectPage.ElfPath.Text = ""; return; }
        ProjectPage.ElfPath.Text = target.ElfPath;
        Variables.Clear();
        plan = null; RemotePage.ApplyConfigButton.IsEnabled = false;
    }
}

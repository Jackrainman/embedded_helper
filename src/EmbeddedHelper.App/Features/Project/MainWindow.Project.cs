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
    private bool loadingProject;
    private string? automaticElfPath;
    private BuildTarget? Target => ProjectPage.TargetBox.SelectedItem as BuildTarget;
    private string RefreshElfPath()
    {
        if (Target is { } target && automaticElfPath != null &&
            string.Equals(ProjectPage.ElfPath.Text, automaticElfPath, StringComparison.OrdinalIgnoreCase))
            ProjectPage.ElfPath.Text = automaticElfPath = ProjectReader.ResolveElfPath(target.ElfPath);
        return ProjectPage.ElfPath.Text;
    }
    private void RefreshProjectDiagnostics(string? selectedElf = null)
    {
        if (project == null) return;
        project = ProjectReader.RefreshFileDiagnostics(project, Target?.Name, selectedElf);
        ProjectPage.ProjectDiagnostics.Text = string.Join("\n", project.Diagnostics.Prepend("读取完成；未运行工程钩子、构建或设备命令。"));
    }

    internal async Task RestoreLastProjectAsync()
    {
        var path = string.IsNullOrWhiteSpace(settings.LastProject) ? settings.Recent?.FirstOrDefault() : settings.LastProject;
        if (string.IsNullOrWhiteSpace(path)) return;
        ProjectPage.ProjectPath.Text = path;
        Report("正在恢复上次工程…");
        try
        {
            await LoadProjectAsync(path);
            Report("已恢复上次工程；尚未连接设备。");
        }
        catch (Exception ex)
        {
            Report("上次工程无法恢复，请重新选择工程：" + ex.Message);
        }
    }
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
        if (acquisition.HasSession || ObservationBusy || configurationBusy) throw new InvalidOperationException("请先停止采集并等待设置读取完成，再切换工程。");
        if (watchDirty) throw new InvalidOperationException("变量设置尚未保存；请重试保存或重新读取后再切换工程。");
        loadingProject = true; SyncObservationState();
        try
        {
            var loaded = await Task.Run(() => ProjectReader.Read(path)).WaitAsync(TimeSpan.FromSeconds(5));
            project = loaded;
            connectionFailure = "";
            activeTransport = "local";
            SetTransportSelection();
            ProjectPage.ProjectPath.Text = loaded.Root;
            ProjectPage.ProjectSummary.Text = loaded.Name + " · " + loaded.Family;
            ProjectPage.McuLabel.Text = "MCU：" + loaded.Mcu;
            ProjectPage.McuLabel.ToolTip = "来源：.eide/eide.yml";
            ProjectPage.ProjectDiagnostics.Text = string.Join("\n", loaded.Diagnostics.Prepend("读取完成；未运行工程钩子、构建或设备命令。"));
            ProjectPage.TargetBox.ItemsSource = loaded.Targets;
            ProjectPage.TargetBox.SelectedIndex = loaded.Targets.Length == 1 ? 0 : -1;
            var watch = await WatchSettingsStore.LoadAsync(loaded.Root);
            if (watch != null)
            {
                ProjectPage.TargetBox.SelectedItem = loaded.Targets.FirstOrDefault(t => t.Name == watch?.Target);
            }
            if (string.Equals(settings.LastProject, loaded.Root, StringComparison.OrdinalIgnoreCase) &&
                loaded.Targets.FirstOrDefault(t => t.Name == settings.LastTarget) is { } rememberedTarget)
                ProjectPage.TargetBox.SelectedItem = rememberedTarget;
            ApplyWatchToSetup(watch);
            ResetWatchEditing();
            await LoadConnectionAsync();
            remoteView.Host = ""; remoteView.Port = "19011"; remoteView.Web = "";
            IntegrationState? integration = null;
            remoteView.ReadOnlyReason = "";
            try { integration = VsCodeIntegration.ReadState(loaded.Root); }
            catch (InvalidDataException ex)
            {
                remoteView.ReadOnlyReason = ex.Message + "；工程接入只读，请修复该文件后重新打开工程。";
                ProjectPage.ProjectDiagnostics.AppendText("\n" + remoteView.ReadOnlyReason);
            }
            remoteView.GdbPort = (integration?.GdbPort ?? 2331).ToString(CultureInfo.InvariantCulture);
            remoteView.KeepEideUpload = false;
            if (File.Exists(System.IO.Path.Combine(loaded.Root, ".embedded_helper", "tools.lock.json")))
            {
                try { remoteView.SelectedRelease = ToolCache.ReadLock(loaded.Root); }
                catch (InvalidDataException ex) { ProjectPage.ProjectDiagnostics.AppendText("\n" + ex.Message); }
            }
            if (File.Exists(loaded.RemotePath))
            {
                try
                {
                    using var remote = JsonDocument.Parse(await File.ReadAllTextAsync(loaded.RemotePath), ProjectReader.JsonOptions);
                    remoteView.Host = remote.RootElement.GetProperty("ServerName").GetString() ?? "";
                    remoteView.Port = remote.RootElement.GetProperty("Port").GetInt32().ToString(CultureInfo.InvariantCulture);
                    remoteView.Web = remote.RootElement.TryGetProperty("WebUrl", out var web) ? web.GetString() ?? "" : "";
                }
                catch (Exception ex) { ProjectPage.ProjectDiagnostics.AppendText("\n远程配置读取失败，可继续使用本地连接及解析 ELF：" + ex.Message); }
            }
            settings = settings with { LastProject = loaded.Root, LastTarget = Target?.Name ?? "", Recent = new[] { loaded.Root }.Concat(settings.Recent ?? []).Distinct(StringComparer.OrdinalIgnoreCase).Take(8).ToArray() };
            ProjectPage.RecentProjects.ItemsSource = settings.Recent;
            PersistSettings();
            remoteView.Invalidate();
            await RefreshIntegrationStatusAsync();
            Report("已打开 " + loaded.Name + "。请选择 ELF 解析变量，或维护远程配置。");
            if (remoteView.ReadOnlyReason.Length > 0) Report(remoteView.ReadOnlyReason);
            if (configuration.HasPending(loaded.Root)) Report("该工程有未完成写入，请到远程配置页恢复；外部修改不会被覆盖。");
        }
        finally { loadingProject = false; SyncObservationState(); await RefreshProjectConnectionAsync(); }
    }
    private async void TargetChanged(object? sender, SelectionChangedEventArgs e)
    {
        automaticElfPath = null;
        connectionFailure = "";
        SetElfFeedback(new("尚未解析当前目标 ELF"));
        InvalidateViewer(); remoteView.Invalidate();
        Variables.Clear();
        if (Target is not { } target) { ProjectPage.ElfPath.Text = ""; return; }
        ProjectPage.ElfPath.Text = automaticElfPath = ProjectReader.ResolveElfPath(target.ElfPath);
        Variables.Clear();
        remoteView.Invalidate();
        if (!loadingProject)
        {
            try { ResetWatchEditing(); ApplyWatchToSetup(watchEdit?.Value?.Target == target.Name ? watchEdit.Value : null); }
            catch (Exception ex) { watchEdit = null; observationView.SettingsError = true; observationView.SettingsStatus = "变量设置读取失败：" + ex.Message; Report(observationView.SettingsStatus); }
            await LoadConnectionAsync(); await RefreshProjectConnectionAsync();
            if (project != null && Target == target)
            {
                settings = settings with { LastProject = project.Root, LastTarget = target.Name };
                try { PersistSettings(); }
                catch (Exception ex) { Report("目标已选择，但记忆保存失败：" + ex.Message); }
            }
        }
    }
}

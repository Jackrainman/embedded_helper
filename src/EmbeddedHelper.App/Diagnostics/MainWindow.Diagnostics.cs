using System.IO;
using System.IO.Ports;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using EmbeddedHelper.Core;

namespace EmbeddedHelper.App;

public partial class MainWindow
{
    public async Task SmokeAsync(string directory)
    {
        try
        {
            Directory.CreateDirectory(directory);
            UpdateProjectHeader();
            if (GlobalTransport.IsEnabled || !ProjectStateLabel.Text.Contains("未打开项目")) throw new InvalidDataException("未打开项目状态错误。");
            await LoadProjectAsync(System.IO.Path.Combine(directory, "project"));
            ProjectPage.ElfPath.Text = System.IO.Path.Combine(directory, "scalars.elf");
            await ParseElfAsync();
            if (!ProjectPage.ElfStatus.Text.Contains("解析成功：7") || observationView.HasElfDetails)
                throw new InvalidDataException("ELF 成功反馈未就地显示。");
            if (ProjectPage.ProjectDiagnostics.Text.Contains($"目标 {Target!.Name} 的 ELF 不存在")) throw new InvalidDataException("成功解析后仍保留旧ELF缺失诊断。");
            if (Variables.Count(v => v.Variable.Supported) != 7) throw new InvalidDataException("标量类型自检失败。");
            remoteView.Host = "test.example.invalid"; PreviewConfig(null, null!);
            if (plan == null) throw new InvalidDataException("远程配置预览失败。");
            var toolPackage = System.IO.Path.Combine(directory, "debug-lan.zip");
            if (File.Exists(toolPackage))
            {
                await PrepareToolAsync(toolPackage);
                remoteView.EideUpload = true; remoteView.GdbPort = "2450"; remoteView.Web = "http://test.example.invalid:8000";
                PreviewIntegration(null, null!);
                if (plan == null || plan.Changes.Length == 0) throw new InvalidDataException("一键接入预览失败。");
                await ApplyConfigAsync();
                await LoadProjectAsync(project!.Root);
                if (remoteView.EideUpload != true || remoteView.GdbPort != "2450" || remoteView.Web != "http://test.example.invalid:8000") throw new InvalidDataException("远程配置读回失败。");
                ProjectPage.ElfPath.Text = System.IO.Path.Combine(directory, "scalars.elf"); await ParseElfAsync();
                PreviewIntegration(null, null!);
                if (plan == null || plan.Changes.Length != 0) throw new InvalidDataException("重复接入非幂等。");
                PreviewRemoveIntegration(null, null!);
                if (plan == null || plan.Changes.Length == 0) throw new InvalidDataException("共享移除预览失败。");
                await ApplyConfigAsync();
            }
            PersistSettings();
            var restored = settingsStore.Load();
            if (restored.Recent?.Contains(project!.Root) != true) throw new InvalidDataException("设置回读失败。");
            var savedElf = ProjectPage.ElfPath.Text;
            ProjectPage.ElfPath.Text = System.IO.Path.Combine(directory, "missing.elf");
            await Guard(ParseElfAsync);
            if (!StatusLabel.Text.StartsWith("操作未完成：", StringComparison.Ordinal)) throw new InvalidDataException("ELF 错误未反馈到界面。");
            if (!ProjectPage.ElfStatus.Text.StartsWith("解析失败") || !observationView.HasElfDetails || Variables.Any(v => v.Variable.Supported))
                throw new InvalidDataException("解析失败后仍显示旧成功或可读取地址。");
            SaveSmokeScreenshot(directory, "elf-missing-feedback.png");
            ProjectPage.ElfPath.Text = savedElf;
            await ParseElfAsync();
            if (!ProjectPage.ElfStatus.Text.Contains("解析成功：7") || observationView.HasElfDetails)
                throw new InvalidDataException("ELF 失败后重试未恢复成功反馈。");
            await Guard(() => SendSerialAsync(CancellationToken.None));
            if (!StatusLabel.Text.Contains("请先打开串口")) throw new InvalidDataException("未连接串口的错误未反馈。");
            SerialPage.SerialPortBox.Text = "";
            await Guard(ConnectSerialAsync);
            if (serial != null || SerialPage.ConnectSerialButton.Content?.ToString() != "打开串口" || !SerialPage.ConnectSerialButton.IsEnabled || SerialPage.SerialConnectionLabel.Text != "未连接" || SerialPage.SerialConnectionLight.Fill != Brushes.Firebrick) throw new InvalidDataException("打开失败后串口按钮和红灯未恢复。");
            await SmokeObservationSettingsAsync(directory);
            await SmokeProjectStatusAsync(directory);
            await SmokePresentationAsync(directory);
            await SmokeRecordingAsync(directory);
            await SmokeSerialAsync(directory);
            SmokeLocalToolSettings(directory);
            await SmokeProjectRestoreAsync(directory);
            SmokeProbeSelection(directory);
            Report("离线 GUI 自检：工程、ELF、设置与错误反馈通过；硬件未验收。");
            ProjectSections.SelectedIndex = 0;
            for (int i = 0; i < Pages.Items.Count; i++)
            {
                Pages.SelectedIndex = i;
                if (i == 1) await viewerPreparation;
                await Dispatcher.InvokeAsync(() => { }, DispatcherPriority.ApplicationIdle);
                SaveSmokeScreenshot(directory, $"page-{i}.png");
            }
            Pages.SelectedIndex = 0;
            for (int i = 0; i < ProjectSections.Items.Count; i++)
            {
                ProjectSections.SelectedIndex = i;
                await Dispatcher.InvokeAsync(() => { }, DispatcherPriority.ApplicationIdle);
                SaveSmokeScreenshot(directory, $"project-section-{i}.png");
            }
            ProjectSections.SelectedIndex = 0;
            Width = MinWidth; Height = MinHeight;
            for (int i = 0; i < Pages.Items.Count; i++)
            {
                Pages.SelectedIndex = i;
                if (i == 1) await viewerPreparation;
                await Dispatcher.InvokeAsync(() => { }, DispatcherPriority.ApplicationIdle);
                SaveSmokeScreenshot(directory, $"page-{i}-minimum.png");
            }
            Pages.SelectedIndex = 0;
            for (int i = 0; i < ProjectSections.Items.Count; i++)
            {
                ProjectSections.SelectedIndex = i;
                await Dispatcher.InvokeAsync(() => { }, DispatcherPriority.ApplicationIdle);
                SaveSmokeScreenshot(directory, $"project-section-{i}-minimum.png");
            }
            PortableData.SaveFile(System.IO.Path.Combine(directory, "smoke.json"), new { status = "passed", exitCode = 0, version = Product.Version, pages = Pages.Items.Count, projectSections = ProjectSections.Items.Count, scalarCount = Variables.Count, observationSettings = "roundtrip-and-missing-config-passed", toolIntegration = File.Exists(toolPackage), method = "automated", hardware = "not-tested", presentation = "bindings-and-render-passed", plotSmoke, serialSmoke });
            Close();
        }
        catch (Exception ex)
        {
            PortableData.SaveFile(System.IO.Path.Combine(directory, "smoke.json"), new { status = "failed", exitCode = 1, error = ex.ToString() });
            closing = true; refresh.Stop(); Application.Current.Shutdown(1);
        }
    }

    private async Task SmokeObservationSettingsAsync(string directory)
    {
        var root = project!.Root;
        // All files below belong to the caller's isolated smoke project; no SDK is loaded.
        var watchPath = System.IO.Path.Combine(root, ".embedded_helper", "watch.json");
        if (File.Exists(watchPath)) File.Delete(watchPath);
        ApplyWatchToSetup(null);
        Pages.SelectedIndex = 1;
        await viewerPreparation;
        if (preparedWatch != null || ObservationPage.StartButton.IsEnabled || ObservationPage.ConfigurationRequired.Visibility != Visibility.Visible) throw new InvalidDataException("缺少工程观察设置时未阻止采集。");
        await Dispatcher.InvokeAsync(() => { }, DispatcherPriority.ApplicationIdle);
        SaveSmokeScreenshot(directory, "page-1-missing.png");
        Exception? dialogError = null;
        var dialogChecked = false;
        _ = Dispatcher.BeginInvoke(DispatcherPriority.ApplicationIdle, new Action(() =>
        {
            var dialog = OwnedWindows.Cast<Window>().SingleOrDefault(w => w.Title == "添加变量");
            try
            {
                if (dialog?.Content is not System.Windows.Controls.DockPanel panel) throw new InvalidDataException("添加变量弹窗未打开。");
                var picker = panel.Children.OfType<VariablePicker>().Single();
                if (!ReferenceEquals(picker.Variables, Variables) || picker.AddedVariables.Visibility != Visibility.Collapsed || ObservationPage.Picker.SearchBox.Visibility != Visibility.Collapsed)
                    throw new InvalidDataException("变量弹窗与主页面职责或集合不一致。");
                var candidate = Variables.First(v => v.Variable.Supported);
                candidate.Selected = false;
                picker.SearchBox.Text = candidate.Variable.Name;
                if (picker.Results.Items.Count == 0) throw new InvalidDataException("弹窗搜索未返回变量。");
                dialogChecked = true;
            }
            catch (Exception ex) { dialogError = ex; }
            finally { dialog?.Close(); }
        }));
        ConfigureObservation(null, null!);
        if (dialogError != null) throw new InvalidDataException("添加变量弹窗自检失败。", dialogError);
        if (!dialogChecked || Pages.SelectedIndex != 1 || ObservationPage.SampleRate.Text != "200") throw new InvalidDataException("变量弹窗或默认200Hz错误。");
        ConnectionPage.ProbeSerial.Text = "123456";
        ObservationPage.SampleRate.Text = "75";
        foreach (var row in Variables) row.Selected = false;
        foreach (var row in Variables.Where(v => v.Variable.Supported).Take(2)) row.Selected = true;
        WatchSettingsStore.Save(root, new(Target!.Name, [], 100) { ProbeSerial = "123456" });
        await SaveVariablesAsync();
        var saved = await WatchSettingsStore.LoadAsync(root) ?? throw new InvalidDataException("工程观察设置未保存。");
        if (saved.ProbeSerial != "123456" || saved.Rate != 75 || saved.Identities.Length != 2) throw new InvalidDataException("工程观察设置保存值错误。");
        var sdk = System.IO.Path.Combine(directory, "not-a-native-library.dll");
        File.WriteAllText(sdk, "UI readiness test only. This file is never loaded and no worker is started.");
        SettingsPage.DllPath.Text = sdk;
        ConnectionPage.ProbeSerial.Text = "999";
        ObservationPage.SampleRate.Text = "999";
        foreach (var row in Variables) row.Selected = false;
        await PrepareViewerAsync();
        if (prepared == null || !ObservationPage.StartButton.IsEnabled || prepared.Request.ProbeSerial != 123456 || prepared.Request.SampleRate != 75 || Variables.Count(v => v.Selected) != 2) throw new InvalidDataException("查看页未读取保存值，或使用了未保存的界面值。");
        var remotePath = project.RemotePath;
        var remoteText = File.Exists(remotePath) ? await File.ReadAllTextAsync(remotePath) : null;
        activeTransport = "remote"; SetTransportSelection(); // Simulate a previously selected remote configuration being removed.
        File.Delete(remotePath);
        WatchSettingsStore.Save(root, saved with { Transport = "remote", EnableRtt = false });
        await PrepareViewerAsync();
        if (preparedWatch != null || ObservationPage.StartButton.IsEnabled) throw new InvalidDataException("缺少远程配置时未阻止采集。");
        if (observationView.VisibleRows.Length != 2 || observationView.VisibleRows.Any(r => r.Address == "未解析") || !observationView.Summary.Contains("已保存")) throw new InvalidDataException("缺远程配置时丢失已解析变量或显示错误RTT状态。");
        await SaveObservationSmokeStateAsync(directory, "page-1-missing-remote.png");
        ConfigureObservation(null, null!);
        if (Pages.SelectedIndex != 0 || ProjectSections.SelectedIndex != 1) throw new InvalidDataException("缺少远程配置时恢复入口未到远程配置。");
        await File.WriteAllTextAsync(remotePath, "{\"ServerName\":\"saved.example.invalid\",\"Port\":19011}");
        remoteView.Host = "unsaved.example.invalid";
        await PrepareViewerAsync();
        if (preparedWatch == null || prepared.Request.Host != "saved.example.invalid" || prepared.Request.Rtt) throw new InvalidDataException("未读取工程远程参数或远程RTT状态错误。");
        if (remoteText == null) File.Delete(remotePath); else await File.WriteAllTextAsync(remotePath, remoteText);
        activeTransport = "local"; SetTransportSelection();
        WatchSettingsStore.Save(root, saved with { Identities = ["missing:variable"] });
        await PrepareViewerAsync();
        if (preparedWatch != null || ObservationPage.StartButton.IsEnabled) throw new InvalidDataException("ELF缺少保存变量时未阻止采集。");
        WatchSettingsStore.Save(root, saved with { Rate = 1001 });
        await PrepareViewerAsync();
        if (preparedWatch != null) throw new InvalidDataException("无效采样率未阻止采集。");
        WatchSettingsStore.Save(root, saved);
        SettingsPage.DllPath.Text = System.IO.Path.Combine(directory, "missing-sdk.dll");
        await PrepareViewerAsync();
        if (preparedWatch != null || ObservationPage.StartButton.IsEnabled) throw new InvalidDataException("缺少本机SDK时未阻止采集。");
        if (observationView.VisibleRows.Length != 2 || observationView.VisibleRows.Any(r => r.Address == "未解析")) throw new InvalidDataException("缺SDK时丢失已解析变量。");
        await SaveObservationSmokeStateAsync(directory, "page-1-missing-sdk.png");
        ConfigureObservation(null, null!);
        if (Pages.SelectedIndex != 4) throw new InvalidDataException("缺少SDK时恢复入口未到设置与诊断。");
        SettingsPage.DllPath.Text = sdk;
        WatchSettingsStore.Save(root, saved with { Identities = [], EnableRtt = true });
        await PrepareViewerAsync();
        if (preparedWatch != null || ObservationPage.StartButton.IsEnabled) throw new InvalidDataException("MCU Viewer 不应接受仅 RTT 配置。");
        var rttReady = await ObservationPreparation.PrepareAsync(project, Target!, "", sdk, _ => throw new InvalidDataException("RTT 不应解析 ELF"), true);
        if (!rttReady.Request.Rtt || rttReady.Request.Variables.Length != 0) throw new InvalidDataException("RTT 独立准备失败。");
        await SaveObservationSmokeStateAsync(directory, "page-1-rtt-only.png");
        var width = Width; var height = Height;
        Width = MinWidth; Height = MinHeight;
        await SaveObservationSmokeStateAsync(directory, "page-1-rtt-only-minimum.png");
        Width = width; Height = height;
        WatchSettingsStore.Save(root, saved);
        await PrepareViewerAsync();
        if (preparedWatch == null || acquisition.HasSession) throw new InvalidDataException("配置读回失败或自检意外创建了采集会话。");
    }

    private async Task SmokeProjectStatusAsync(string directory)
    {
        var loaded = project!;
        var remotePath = loaded.RemotePath;
        var remoteText = File.Exists(remotePath) ? await File.ReadAllTextAsync(remotePath) : null;
        File.Delete(remotePath);
        await RefreshProjectConnectionAsync();
        if (activeTransport != "local" || GlobalTransport.SelectedIndex != 0 || ProjectStateLabel.Foreground != Brushes.LightGreen || !ConnectionStateLabel.Text.Contains("连接配置有效 · 未连接"))
            throw new InvalidDataException("本地默认状态或已就绪状态错误。");
        await Guard(() => SelectTransportAsync("remote"));
        if (activeTransport != "remote" || GlobalTransport.SelectedIndex != 1 || !ConnectionStateLabel.Text.Contains("连接待设置") || ProjectStateLabel.Foreground != Brushes.LightGreen)
            throw new InvalidDataException("缺少远程连接时未显示待设置或污染项目状态。");
        await File.WriteAllTextAsync(remotePath, "{\"ServerName\":\"saved.invalid\",\"Port\":19011,\"WebUrl\":\"http://saved.invalid:8000\"}");
        await SelectTransportAsync("remote");
        if (activeTransport != "remote" || GlobalTransport.SelectedIndex != 1 || ProjectStateLabel.Foreground != Brushes.LightGreen)
            throw new InvalidDataException("有效远程连接切换失败。");
        await PrepareViewerAsync();
        if (prepared?.Request.Host != "saved.invalid" || prepared.Request.Rtt) throw new InvalidDataException("MCU Viewer未使用顶部连接选择。");
        preparing = true; SyncObservationState();
        await Guard(() => SelectTransportAsync("local"));
        if (GlobalTransport.IsEnabled || activeTransport != "remote") throw new InvalidDataException("连接处理中允许切换。");
        preparing = false; SyncObservationState();
        File.Delete(remotePath);
        await RefreshProjectConnectionAsync();
        if (ProjectStateLabel.Foreground != Brushes.LightGreen || !ConnectionStateLabel.Text.Contains("连接待设置") || ObservationPage.StartButton.IsEnabled) throw new InvalidDataException("远程配置丢失未显示待设置或未阻止采集。");
        SaveSmokeScreenshot(directory, "header-invalid-remote.png");
        await SelectTransportAsync("local");
        if (remoteText != null) await File.WriteAllTextAsync(remotePath, remoteText);
        await RefreshProjectConnectionAsync();
        ConnectionSettingsStore.Save(loaded.Root, new(Target!.Name, "local", "123456"));
        var watchBefore = await File.ReadAllTextAsync(System.IO.Path.Combine(loaded.Root, ".embedded_helper", "watch.json"));
        await LoadConnectionAsync();
        await PrepareViewerAsync();
        if (prepared?.Request.ProbeSerial != 123456 || ConnectionPage.ProbeSerial.Text != "123456") throw new InvalidDataException("共享连接未同步。");
        if (watchBefore != await File.ReadAllTextAsync(System.IO.Path.Combine(loaded.Root, ".embedded_helper", "watch.json"))) throw new InvalidDataException("连接切换修改了变量配置。");
    }

    private async Task SaveObservationSmokeStateAsync(string directory, string file)
    {
        Pages.SelectedIndex = 1;
        await viewerPreparation;
        await Dispatcher.InvokeAsync(() => { }, DispatcherPriority.ApplicationIdle);
        SaveSmokeScreenshot(directory, file);
    }

    private void SaveSmokeScreenshot(string directory, string file)
    {
        UpdateLayout();
        var content = (FrameworkElement)Content;
        var bitmap = new RenderTargetBitmap((int)content.ActualWidth, (int)content.ActualHeight, 96, 96, PixelFormats.Pbgra32);
        bitmap.Render(content);
        var encoder = new PngBitmapEncoder(); encoder.Frames.Add(BitmapFrame.Create(bitmap));
        using var output = File.Create(System.IO.Path.Combine(directory, file)); encoder.Save(output);
    }
}

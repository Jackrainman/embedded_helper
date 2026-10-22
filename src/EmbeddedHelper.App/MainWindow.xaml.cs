using System.ComponentModel;
using System.IO;
using System.Windows;
using System.Windows.Threading;
using EmbeddedHelper.Core;

namespace EmbeddedHelper.App;

public partial class MainWindow : Window
{
    private readonly DispatcherTimer refresh = new() { Interval = TimeSpan.FromMilliseconds(100) };
    private bool closing, shutdownRequested;
    private readonly ConnectionView ConnectionPage = new();

    public MainWindow() : this(new ObservationCoordinator()) { }

    internal MainWindow(ObservationCoordinator coordinator)
    {
        acquisition = coordinator;
        InitializeComponent();
        ConnectionPage.SaveRequested += SaveConnection;
        ConnectionPage.RefreshRequested += async (_, _) => await RefreshProbeListAsync();
        SettingsPage.DiscoverToolsRequested += DiscoverLocalTools;
        SettingsPage.JLinkCandidates.SelectionChanged += (_, _) => ToolSelectionChanged(LocalToolKind.JLink);
        SettingsPage.ReadElfCandidates.SelectionChanged += (_, _) => ToolSelectionChanged(LocalToolKind.ReadElf);
        SettingsPage.BrowseReadElfRequested += BrowseReadElf;
        SettingsPage.DllPath.TextChanged += (_, _) => RefreshLocalToolStatus();
        SettingsPage.ReadElfPath.TextChanged += (_, _) => RefreshLocalToolStatus();
        SettingsPage.DllPath.LostKeyboardFocus += (_, _) => { if (SettingsPage.DllPath.Text != settings.Dll) SaveToolPath(LocalToolKind.JLink, SettingsPage.DllPath.Text); };
        SettingsPage.ReadElfPath.LostKeyboardFocus += (_, _) => { if (SettingsPage.ReadElfPath.Text != settings.ReadElf) SaveToolPath(LocalToolKind.ReadElf, SettingsPage.ReadElfPath.Text); };
        ObservationPage.DataContext = observationView;
        ObservationPage.SaveVariablesRequested += SaveObservationSettings;
        ObservationPage.ReloadVariablesRequested += ReloadWatchSettings;
        observationView.SelectionEdited += (_, _) => QueueWatchSave();
        ObservationPage.SampleRate.TextChanged += (_, _) => { if (!suppressWatchAutosave && project != null && Target != null && ObservationPage.SampleRate.IsKeyboardFocused && observationView.CanEdit) { watchDirty = true; observationView.SettingsStatus = "频率编辑中；按 Enter 或离开输入框后自动保存。"; SyncObservationState(); } };
        ObservationPage.SampleRate.LostKeyboardFocus += (_, _) => CommitFrequency();
        ObservationPage.SampleRate.PreviewKeyDown += (_, e) => { if (e.Key == System.Windows.Input.Key.Enter) { CommitFrequency(); e.Handled = true; } };
        ObservationPage.ExportRecordingRequested += ExportRecording;
        ObservationPage.ConnectionRequested += ConfigureConnection;
        RemotePage.DataContext = remoteView;
        RemotePage.ImportLanRequested += async (_, _) => await Guard(() => ImportLanAsync(null));
        RemotePage.ImportLanZipRequested += ImportLanZip;
        RemotePage.ToolVersionBox.SelectionChanged += async (_, _) => await RefreshIntegrationStatusAsync();
        SerialPage.DataContext = serialView;
        VersionLabel.Text = "v" + Product.Version;
        Title = "Embedded Helper " + Product.Version;
        acquisition.Changed += SyncObservationState;
        serialController.Changed += UpdateSerialConnection;
        InitializePageSettings();
        RefreshPorts(null, null!);
        EnvironmentCheck(null, null!);
        refresh.Tick += Refresh;
        refresh.Start();
        pagesReady = true;
        DiscoverLocalTools(null, new RoutedEventArgs());
    }

    private void Report(string text) { StatusLabel.Text = text; try { App.Data.Log(text); } catch (Exception logError) when (logError is IOException or UnauthorizedAccessException) { } }
    private async Task Guard(Func<Task> action)
    {
        try { await action(); }
        catch (Exception ex)
        {
            Report("操作未完成：" + ex.Message);
            try { App.Data.Log(ex.ToString()); } catch (Exception logError) when (logError is IOException or UnauthorizedAccessException) { }
        }
    }
    private void Refresh(object? sender, EventArgs e)
    {
        UpdateSerialConnection();
        RefreshObservation();
        RefreshSerial();
    }
    private async void WindowClosing(object? sender, CancelEventArgs e)
    {
        if (closing) return;
        e.Cancel = true;
        if (toolBusy || observationView.Exporting) { Report("正在保存数据或准备工具，请等待完成后关闭。"); return; }
        if (watchDirty) { Report("变量设置尚未保存；请修正后重试，或重新读取已保存设置后关闭。"); return; }
        if (shutdownRequested) return;
        shutdownRequested = true; SyncObservationState();
        // Let the cancelled Closing event return before calling Close again, even when cleanup completes synchronously.
        await Dispatcher.Yield(DispatcherPriority.Background);
        await Guard(async () => { await viewerPreparation; while (indexing || loadingProject || configurationBusy || preparing || discoveringProbes) await Task.Delay(25); await CloseSerialAsync(); await StopObservationAsync(); try { PersistSettings(); } catch (Exception ex) { Report("输入无效，保留上次设置：" + ex.Message); } refresh.Stop(); closing = true; Close(); });
        if (!closing) { shutdownRequested = false; SyncObservationState(); }
    }
}

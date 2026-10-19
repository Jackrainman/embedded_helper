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

    public MainWindow()
    {
        InitializeComponent();
        ObservationPage.DataContext = observationView;
        ObservationPage.SaveVariablesRequested += SaveObservationSettings;
        ObservationPage.ConnectionRequested += ConfigureConnection;
        RemotePage.DataContext = remoteView;
        SerialPage.DataContext = serialView;
        SettingsPage.ToolVersionBox.SelectionChanged += (_, _) => remoteView.Invalidate();
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
    }

    private void Report(string text) { StatusLabel.Text = text; try { App.Data.Log(text); } catch (IOException) { } }
    private async Task Guard(Func<Task> action)
    {
        try { await action(); }
        catch (Exception ex)
        {
            Report("操作未完成：" + ex.Message);
            try { App.Data.Log(ex.ToString()); } catch (IOException) { }
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
        if (toolBusy) { Report("正在准备工具，请等待下载或校验完成后关闭。"); return; }
        if (shutdownRequested) return;
        shutdownRequested = true; SyncObservationState();
        // Let the cancelled Closing event return before calling Close again, even when cleanup completes synchronously.
        await Dispatcher.Yield(DispatcherPriority.Background);
        await Guard(async () => { await viewerPreparation; while (indexing || loadingProject || configurationBusy || preparing) await Task.Delay(25); await CloseSerialAsync(); await StopObservationAsync(); try { PersistSettings(); } catch (Exception ex) { Report("输入无效，保留上次设置：" + ex.Message); } refresh.Stop(); closing = true; Close(); });
        if (!closing) { shutdownRequested = false; SyncObservationState(); }
    }
}

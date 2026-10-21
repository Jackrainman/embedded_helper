using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.IO.Ports;
using System.Text;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Threading;
using EmbeddedHelper.Core;

namespace EmbeddedHelper.App;

public partial class MainWindow
{
    private readonly SerialController serialController = new();
    private SerialSession? serial => serialController.Current;
    private bool serialOpening => serialController.IsOpening;
    private bool serialClosing => serialController.IsClosing || serialDraining;
    private bool serialDraining;
    private Task? serialCloseTask;
    private readonly SerialViewModel serialView = new();
    private readonly SerialTextBuffer serialText = new();
    private readonly Stopwatch serialClock = new();
    private long lastReceived, lastSent;
    private double lastSerialTime;
    private long clearThrough, skipDecodeThrough, displayRevision = -1;

    private void RefreshPorts(object? sender, RoutedEventArgs e)
    { try { var current = SerialPortDiscovery.PortName(SerialPage.SerialPortBox.Text); var ports = SerialPortDiscovery.List(); SerialPage.SerialPortBox.ItemsSource = ports; SerialPage.SerialPortBox.Text = ports.FirstOrDefault(p => SerialPortDiscovery.PortName(p) == current) ?? current; } catch (Exception ex) { Report(ex.Message); } }
    private SerialSettings ReadSerialSettings() => new(SerialPortDiscovery.PortName(SerialPage.SerialPortBox.Text), int.Parse(SerialPage.BaudBox.Text, CultureInfo.InvariantCulture), SerialPage.DataBitsBox.SelectedIndex + 5, (Parity)SerialPage.ParityBox.SelectedItem, (StopBits)SerialPage.StopBitsBox.SelectedItem, (Handshake)SerialPage.FlowBox.SelectedItem);
    private async void ToggleSerial(object? sender, RoutedEventArgs e) => await Guard(async () =>
    {
        if (serialOpening || serialClosing) return;
        if (serial != null) await CloseSerialAsync(); else await ConnectSerialAsync();
    });
    private async Task ConnectSerialAsync()
    {
        if (shutdownRequested || serial != null || serialOpening || serialClosing) throw new InvalidOperationException("请先关闭当前串口并等待完成。");
        var parameters = ReadSerialSettings();
        serialText.ResetDecoder(); clearThrough = skipDecodeThrough = 0;
        lastReceived = lastSent = 0; lastSerialTime = 0; serialClock.Restart();
        await serialController.OpenAsync(parameters, SerialPage.RecordSerial.IsChecked == true ? System.IO.Path.Combine(App.Data.Root, "serial", "records") : null);
        SaveSerialPreferences(parameters);
        if (!serialClosing) Report(parameters.Baud > 1000000 ? "实验波特率请求已被驱动接受；实际速率和链路完整性未验收。" : "串口已打开。");
    }
    private void UpdateSerialConnection()
    {
        serialView.Update(serialOpening, serialClosing, serial != null, serial?.Connected == true, serialController.IsPeriodic, shutdownRequested);
        serialView.UpdatePeriodic(serialController.PeriodicStatus);
    }
    private Task CloseSerialAsync() => serialCloseTask is { IsCompleted: false } ? serialCloseTask : serialCloseTask = CloseSerialCoreAsync();
    private async Task CloseSerialCoreAsync()
    {
        serialDraining = true; UpdateSerialConnection();
        try
        {
            var closingSession = serial;
            var result = await serialController.CloseAsync();
            // Keep ownership until the accepted tail has been drained, including while the page is hidden.
            if (closingSession != null)
            {
                while (closingSession.Display.TryPeek(out _))
                {
                    DrainSerialDisplay(closingSession);
                    await Dispatcher.Yield(DispatcherPriority.Background);
                }
            }
            RenderSerialLog();
            serialClock.Stop();
            if (result.PeriodicError != null) Report("周期发送已停止：" + result.PeriodicError);
            SerialPage.SerialStats.Text = result.RecorderError != null ? "串口已关闭；记录失败：" + result.RecorderError : result.Discarded > 0 ? $"串口已关闭；原始记录缺失 {result.Discarded} 块，详见诊断。" : "串口已关闭。";
        }
        finally { serialDraining = false; UpdateSerialConnection(); }
    }
    private async void SendSerial(object? sender, RoutedEventArgs e) => await Guard(() => SendSerialAsync(CancellationToken.None));
    private async Task SendSerialAsync(CancellationToken token)
    {
        if (serialClosing || serialOpening) throw new InvalidOperationException("串口正在切换状态。");
        if (serial == null) throw new InvalidOperationException("请先打开串口。");
        var text = SerialPage.SendText.Text;
        var bytes = SerialSession.ParseSend(text, SerialPage.HexSend.IsChecked == true, SerialPage.NewlineBox.SelectedIndex switch { 1 => "\n", 2 => "\r\n", _ => "" }, SerialTextBuffer.GetEncoding((string)SerialPage.EncodingBox.SelectedItem));
        if (bytes.Length == 0) throw new ArgumentException("发送内容为空。");
        await serialController.SendAsync(bytes, token);
        settings = settings with { SendHistory = new[] { text }.Concat(settings.SendHistory ?? []).Distinct().Take(20).ToArray() };
        SerialPage.SendHistoryBox.ItemsSource = settings.SendHistory;
    }
    private async void StartPeriodic(object? sender, RoutedEventArgs e) => await Guard(async () =>
    {
        if (shutdownRequested || serial == null || serialOpening || serialClosing) throw new InvalidOperationException("请先打开串口并等待连接完成。");
        var delay = int.Parse(SerialPage.SendPeriod.Text, CultureInfo.InvariantCulture);
        await serialController.StartPeriodicAsync(SendSerialAsync, delay);
    });
    private void StopPeriodic(object? sender, RoutedEventArgs e) => serialController.StopPeriodic();
    private void ClearSerial(object? sender, RoutedEventArgs e)
    {
        clearThrough = serial?.PublishedSequence ?? 0;
        serialText.Clear(); RenderSerialLog();
    }
    private void SerialEncodingChanged(object? sender, RoutedEventArgs e)
    {
        if (SerialPage.EncodingBox?.SelectedItem is not string encoding || SerialPage.HexDisplay == null || SerialPage.LiveLog == null) return;
        clearThrough = serial?.PublishedSequence ?? 0;
        skipDecodeThrough = clearThrough;
        serialText.Configure(encoding, SerialPage.HexDisplay.IsChecked == true); RenderSerialLog();
    }
    private void SelectSendHistory(object? sender, SelectionChangedEventArgs e)
    { if (SerialPage.SendHistoryBox.SelectedItem is string text) SerialPage.SendText.Text = text; }
    private void OpenRecords(object? sender, RoutedEventArgs e) => OpenDirectory(System.IO.Path.Combine(App.Data.Root, "serial", "records"));
    private void DrainSerialDisplay(SerialSession session)
    {
        var started = Stopwatch.GetTimestamp();
        for (var count = 0; count < 256 && Stopwatch.GetElapsedTime(started).TotalMilliseconds < 8 && session.Display.TryRead(out var block); count++)
            if (block.Sequence > skipDecodeThrough) serialText.Append(block, block.Sequence > clearThrough, formatSentLines: true);
    }
    private void RenderSerialLog()
    {
        if (displayRevision == serialText.Revision) return;
        SerialPage.RenderLog(serialText.Capture()); displayRevision = serialText.Revision;
    }
    private void RefreshSerial()
    {
        if (serial != null)
        {
            DrainSerialDisplay(serial);
            var elapsed = serialClock.Elapsed.TotalSeconds;
            var delta = Math.Max(0.001, elapsed - lastSerialTime);
            SerialPage.SerialStats.Text = $"{(serialOpening ? "打开中" : serialClosing ? "关闭中" : serial.Connected ? "已连接" : "连接故障")} · RX {serial.Received:N0} B / {(serial.Received - lastReceived) / delta:N0} B/s · TX 驱动已接受 {serial.Sent:N0} B / {(serial.Sent - lastSent) / delta:N0} B/s · {serial.Error ?? serial.Recorder?.Error ?? serial.LastDriverError}";
            SerialPage.SerialDetails.Text = $"显示队列丢弃 {serial.Clipped:N0} B · 历史裁剪 {serialText.TrimmedCharacters:N0} 字符 · 记录积压 {serial.Recorder?.QueuedBytes ?? 0:N0} B · 记录缺口 {serial.Recorder?.Discarded ?? 0} 块 / {serial.Recorder?.DiscardedBytes ?? 0:N0} B · 驱动错误通知 {serial.DriverErrors} · {serial.LastDriverError} · {serial.Error ?? serial.Recorder?.Error}";
            lastReceived = serial.Received; lastSent = serial.Sent; lastSerialTime = elapsed;
        }
        if (SerialPage.IsVisible) RenderSerialLog();
    }
}

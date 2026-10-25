using System.Diagnostics;
using System.IO;
using System.Text;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Media;
using System.Windows.Threading;
using EmbeddedHelper.Core;

namespace EmbeddedHelper.App;

public partial class MainWindow
{
    internal void PreviewSerial()
    {
        Title = "Embedded Helper " + Product.Version + " · 离线串口显示演示";
        Pages.SelectedIndex = 3;
        serialText.Configure("utf-8", false);
        var sequence = 0;
        void Append(string direction, string text) => serialText.Append(new(++sequence, direction, Encoding.UTF8.GetBytes(text)), formatSentLines: true);
        Append("TX", "AT"); Append("RX", "OK");
        Append("TX", "AT+LF\n"); Append("RX", "回复一半"); Append("RX", "继续\r\n");
        Append("TX", "AT+CRLF\r\n"); Append("RX", "OK\r\n");
        RenderSerialLog();
        Report("离线合成收发示例；未打开串口。");
    }
    private object? serialSmoke;
    private async Task SmokeSerialAsync(string directory)
    {
        Pages.SelectedIndex = 3;
        await Dispatcher.InvokeAsync(() => { }, DispatcherPriority.ApplicationIdle);
        serialText.Configure("utf-8", false);
        var sequence = 0;
        void Append(string direction, string text)
        { serialText.Append(new(++sequence, direction, Encoding.UTF8.GetBytes(text))); RenderSerialLog(); }
        Append("RX", "设备启动\r\n半行"); Append("RX", "继续\r\n"); Append("TX", "AT\r\n");
        var expected = "设备启动\r\n半行继续\r\nAT\r\n";
        var live = SerialPage.LiveLog;
        if (live.PlainText != expected) throw new InvalidDataException("串口显示插入标记或破坏换行：" + live.PlainText);
        live.Editor.SelectAll();
        if (live.SelectedText() != expected || live.Following) throw new InvalidDataException("纯正文复制或选择时停止跟随失败。");
        var colors = live.Editor.Document.Blocks.OfType<Paragraph>().Single().Inlines.OfType<Run>().Select(r => ((SolidColorBrush)r.Foreground).Color).Distinct().Count();
        if (colors != 2) throw new InvalidDataException("收发文字未使用两种颜色。");
        Append("RX", "选择期间新数据\r\n");
        if (live.SelectedText() != expected) throw new InvalidDataException("新增日志破坏选区。");
        SerialPage.FreezeButton.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        if (!SerialPage.IsFrozen) throw new InvalidDataException("冻结按钮没有分栏。");
        if (SerialPage.FrozenLog.SelectedText() != expected) throw new InvalidDataException("分栏后历史侧没有保留原选区。");
        var frozen = SerialPage.FrozenLog.PlainText;
        Append("RX", "冻结后实时内容\r\n");
        if (SerialPage.FrozenLog.PlainText != frozen || !live.PlainText.Contains("冻结后实时内容")) throw new InvalidDataException("历史快照跟随实时侧改变。");
        SaveSmokeScreenshot(directory, "page-2-frozen.png");
        var width = Width; var height = Height; Width = MinWidth; Height = MinHeight;
        await Dispatcher.InvokeAsync(() => { }, DispatcherPriority.ApplicationIdle);
        if (SerialPage.FrozenLog.ActualWidth < 120 || live.ActualWidth < 120) throw new InvalidDataException("最小窗口分栏不可用。");
        if (live.Editor.ActualHeight < 100 || SerialPage.FrozenLog.Editor.ActualHeight < 100)
            throw new InvalidDataException($"最小窗口日志正文不可读：实时{live.Editor.ActualHeight:F0}，历史{SerialPage.FrozenLog.Editor.ActualHeight:F0}。");
        SaveSmokeScreenshot(directory, "page-2-frozen-minimum.png"); Width = width; Height = height;
        ClearSerial(null, null!);
        if (live.PlainText != "" || SerialPage.FrozenLog.PlainText != frozen) throw new InvalidDataException("清屏误清历史侧。");
        Append("TX", "清屏后的新内容\r\n");
        SerialPage.FreezeButton.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        if (SerialPage.IsFrozen || live.PlainText != "清屏后的新内容\r\n") throw new InvalidDataException("继续显示清空实时历史或恢复了已清屏内容。");
        var beforeFailure = live.PlainText;
        SerialPage.SerialPortBox.Text = ""; await Guard(ConnectSerialAsync);
        if (serial != null || live.PlainText != beforeFailure) throw new InvalidDataException("连接失败清除了历史。");
        serialText.ResetDecoder(); Append("RX", "新会话\r\n");
        if (!live.PlainText.StartsWith(beforeFailure, StringComparison.Ordinal)) throw new InvalidDataException("重置会话丢失显示历史。");
        Pages.SelectedIndex = 0; Pages.SelectedIndex = 3;
        if (!live.PlainText.Contains("新会话")) throw new InvalidDataException("切页丢失日志。");
        SerialPage.ToggleFreeze(); frozen = SerialPage.FrozenLog.PlainText;
        serialText.Configure("us-ascii", true); RenderSerialLog();
        serialText.Append(new(++sequence, "RX", [0x41, 0x42])); RenderSerialLog();
        if (live.PlainText != "4142" || SerialPage.FrozenLog.PlainText != frozen || SerialPage.FrozenLog.Snapshot?.Mode != "utf-8") throw new InvalidDataException("切换HEX修改了历史快照。");
        SerialPage.ToggleFreeze();
        foreach (var state in Enum.GetValues<PeriodicSendState>())
        {
            serialView.UpdatePeriodic(new(state, 1000, 3, state == PeriodicSendState.Faulted ? "离线模拟错误" : null));
            if (string.IsNullOrEmpty(serialView.PeriodicText)) throw new InvalidDataException("周期反馈为空。");
        }
        try { SerialSession.ParseSend("GG", true, ""); throw new InvalidDataException("无效HEX被接受。"); }
        catch (FormatException ex) when (ex.InnerException != null && ex.Message.Contains("非十六进制")) { }

        object Measure(bool split)
        {
            serialText.Configure("utf-8", false); RenderSerialLog(); live.ResumeFollowing();
            if (split) SerialPage.ToggleFreeze();
            var allocated = GC.GetAllocatedBytesForCurrentThread();
            var memory = GC.GetTotalMemory(true);
            var watch = Stopwatch.StartNew();
            var worst = 0.0; var peakRuns = 0;
            for (var frame = 0; frame < 40; frame++)
            {
                var started = Stopwatch.GetTimestamp();
                for (var block = 0; block < 32; block++)
                    serialText.Append(new(++sequence, block % 2 == 0 ? "RX" : "TX", Encoding.UTF8.GetBytes(new string('x', 126) + "\r\n")));
                RenderSerialLog(); UpdateLayout();
                if (live.ActualHeight > SerialPage.ActualHeight)
                    throw new InvalidDataException("日志内容撑高页面，发送区不可达。");
                worst = Math.Max(worst, Stopwatch.GetElapsedTime(started).TotalMilliseconds);
                peakRuns = Math.Max(peakRuns, live.RunCount);
                if (live.PlainText != serialText.Text || live.RunCount > 4096 || live.PlainText.Length > 65536) throw new InvalidDataException("日志渲染与有界模型不一致。");
            }
            var result = new { frames = 40, elapsedMs = watch.Elapsed.TotalMilliseconds, worstFrameMs = worst, allocatedBytes = GC.GetAllocatedBytesForCurrentThread() - allocated, retainedBytesDelta = GC.GetTotalMemory(true) - memory, peakRuns, characters = live.PlainText.Length };
            if (split) SerialPage.ToggleFreeze();
            return result;
        }
        var single = Measure(false); var split = Measure(true);
        live.PauseFollowing(); live.Editor.ScrollToHome();
        var reading = live.ReadPosition();
        serialText.Append(new(++sequence, "RX", Encoding.UTF8.GetBytes("裁剪后的新数据\r\n"))); RenderSerialLog();
        if (live.Following) throw new InvalidDataException("上滚查看时被强制跟随。");
        live.ResumeFollowing();
        if (!live.Following) throw new InvalidDataException("回到最新未恢复跟随。");
        var dense = new SerialTextBuffer();
        for (var i = 0; i < 5000; i++) dense.Append(new(i + 1, i % 2 == 0 ? "RX" : "TX", [65]));
        live.Release(); live.Render(dense.Capture());
        if (live.RunCount > 4096 || live.PlainText != dense.Text) throw new InvalidDataException("高碎片日志未保持对象上限。");
        serialSmoke = new { status = "passed", source = "synthetic", hardware = "not-tested", single, split, denseRuns = live.RunCount, lastReadingAnchor = reading.Anchor, scope = "bounded-rendering-and-interaction; physical-reconnect-and-1Mbps-not-tested" };
        live.Release(); displayRevision = -1;
        serialText.Configure("utf-8", false); RenderSerialLog();
        Append("RX", "离线显示演示：接收日志\r\n"); Append("TX", "离线显示演示：发送命令\r\n");
        UpdateSerialConnection();
        SaveSmokeScreenshot(directory, "page-2-log.png");
        var title = Title;
        PreviewSerial();
        if (live.PlainText != "AT\nOK\nAT+LF (LF)\n回复一半继续\r\nAT+CRLF (CRLF)\nOK\r\n")
            throw new InvalidDataException("发送显示换行或末尾标记不符。");
        await Dispatcher.InvokeAsync(() => { }, DispatcherPriority.ApplicationIdle);
        SaveSmokeScreenshot(directory, "serial-sent-lines.png");
        Title = title;
    }
}

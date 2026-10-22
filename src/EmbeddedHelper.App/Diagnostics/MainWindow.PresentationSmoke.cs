using System.Diagnostics;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;

namespace EmbeddedHelper.App;

public partial class MainWindow
{
    private object? plotSmoke;
    private async Task SmokePresentationAsync(string directory)
    {
        var baud = SerialPage.BaudBox.Text;
        SerialPage.BaudBox.Text = "invalid draft";
        var encoding = SerialPage.EncodingBox.SelectedItem;
        SerialPage.EncodingBox.SelectedItem = "gb18030";
        PersistSettings();
        if (settingsStore.Load().SerialEncoding != "gb18030") throw new InvalidDataException("显示编码偏好未保存。");
        SerialPage.EncodingBox.SelectedItem = encoding;
        SerialPage.BaudBox.Text = baud;

        remoteView.Host = "preview.example.invalid";
        PreviewConfig(null, null!);
        if (!remoteView.CanApply) throw new InvalidDataException("预览后未启用应用按钮。");
        RemotePage.RemoteHost.SetCurrentValue(TextBox.TextProperty, "edited.example.invalid");
        await Dispatcher.InvokeAsync(() => { }, DispatcherPriority.ApplicationIdle);
        if (remoteView.Host != "edited.example.invalid" || plan != null || RemotePage.ApplyConfigButton.IsEnabled) throw new InvalidDataException("远程输入绑定或预览失效失败。");

        Pages.SelectedIndex = 1; await viewerPreparation;
        observationView.Busy = true;
        if (ObservationPage.StartButton.IsEnabled) throw new InvalidDataException("准备期间开始按钮未禁用。");
        SyncObservationState();
        RttPage.RttText.Text = "离线合成 RTT 显示，不代表硬件输出。";
        PopulatePlotPreview(16);
        var series = Enumerable.Range(0, 16).Select(row => Enumerable.Range(0, 2000).Select(i => Math.Sin(i * 0.03 + row) + row * 0.15).ToArray()).ToArray();
        series[0][1001] = 8;
        observationView.Series = series;
        observationView.Status = "离线模拟绘图 · 16 条曲线 × 2000 点 · 未连接设备";
        await Dispatcher.InvokeAsync(() => { }, DispatcherPriority.ApplicationIdle);
        UpdateLayout();
        void CheckPresentation()
        {
            if (ObservationPage.Plot.Series.Length != 16 || RttPage.RttText.Text != "离线合成 RTT 显示，不代表硬件输出。" || ObservationPage.PlotEmpty.Visibility != Visibility.Collapsed) throw new InvalidDataException("绘图或文本绑定未生效。");
        }
        CheckPresentation();
        RttPage.RttText.Text = "错误文本";
        var rejected = false;
        try { CheckPresentation(); }
        catch (InvalidDataException) { rejected = true; }
        if (!rejected) throw new InvalidDataException("RTT 冒烟未拒绝错误文本。");
        RttPage.RttText.Text = "离线合成 RTT 显示，不代表硬件输出。";
        CheckPresentation();
        var plot = ObservationPage.Plot;
        var bitmap = new RenderTargetBitmap((int)plot.ActualWidth, (int)plot.ActualHeight, 96, 96, PixelFormats.Pbgra32);
        bitmap.Render(plot);
        var pixels = new byte[bitmap.PixelWidth * bitmap.PixelHeight * 4];
        bitmap.CopyPixels(pixels, bitmap.PixelWidth * 4, 0);
        if (!pixels.Any(b => b != 0)) throw new InvalidDataException("独立绘图控件没有渲染曲线。");
        SaveSmokeScreenshot(directory, "page-1-plot.png");
        if (ObservationPage.IndependentScale.IsEnabled || ObservationPage.Plot.Independent || observationView.ScaleHint != "仅显示少于2个参数") throw new InvalidDataException("超过两个变量时没有禁用分道缩放。");
        object Measure(bool updateSnapshot)
        {
            var allocated = GC.GetAllocatedBytesForCurrentThread();
            var watch = Stopwatch.StartNew();
            for (var frame = 0; frame < 30; frame++)
            {
                if (updateSnapshot) observationView.Series = series.Select(values => values.ToArray()).ToArray();
                plot.InvalidateVisual(); plot.UpdateLayout(); bitmap.Clear(); bitmap.Render(plot);
            }
            return new { frames = 30, elapsedMs = watch.Elapsed.TotalMilliseconds, allocatedBytes = GC.GetAllocatedBytesForCurrentThread() - allocated };
        }
        plotSmoke = new { source = "synthetic", series = 16, pointsPerSeries = 2000, sameSnapshotRedraw = Measure(false), snapshotUpdates = Measure(true), longRunning = "not-tested" };
        PopulatePlotPreview(2);
        observationView.IndependentScale = true;
        await Dispatcher.InvokeAsync(() => { }, DispatcherPriority.ApplicationIdle);
        UpdateLayout();
        if (!ObservationPage.IndependentScale.IsEnabled || !ObservationPage.Plot.Independent) throw new InvalidDataException("两个变量时分道缩放不可用。");
        SaveSmokeScreenshot(directory, "page-1-plot-two-lanes.png");
        observationView.IndependentScale = false;
        await Dispatcher.InvokeAsync(() => { }, DispatcherPriority.ApplicationIdle);
        UpdateLayout();
        SaveSmokeScreenshot(directory, "page-1-plot-two-common.png");
        await SmokeLongNamesAsync(directory);
        observationView.Series = []; RttPage.RttText.Text = "";
        await PrepareViewerAsync();
    }

    private async Task SmokeLongNamesAsync(string directory)
    {
        var names = new[] { "left_drive.controller.feedback.motor.encoder.calibration.position", "right_drive.controller.feedback.motor.encoder.calibration.position" };
        var rows = names.Select((name, i) => new VariableRow(new EmbeddedHelper.Core.Variable("long-" + i, name, (uint)(0x20000000 + 4 * i), "float32", 4, "")) { Selected = true, Value = "合成数据" }).ToArray();
        observationView.ReplaceRows(rows); observationView.PlotLabels = names;
        var originalWidth = Width; var originalHeight = Height;
        Width = MinWidth; Height = MinHeight;
        await Dispatcher.InvokeAsync(() => { }, DispatcherPriority.ApplicationIdle); UpdateLayout();
        static IEnumerable<VariableNameText> Find(DependencyObject parent)
        {
            for (var i = 0; i < VisualTreeHelper.GetChildrenCount(parent); i++)
            {
                var child = VisualTreeHelper.GetChild(parent, i);
                if (child is VariableNameText label) yield return label;
                foreach (var found in Find(child)) yield return found;
            }
        }
        var labels = Find(ObservationPage.Picker.SelectedRows).ToArray();
        SaveSmokeScreenshot(directory, "long-variable-names-minimum.png");
        if (labels.Length != 2 || labels.Any(label => !label.Text.EndsWith(".position") || !label.Text.Contains('…') || !label.ToolTip.ToString()!.Contains(label.FullName)))
            throw new InvalidDataException("窄窗口长变量名未保留末级字段或完整提示：" + string.Join(";", labels.Select(label => $"{label.Text} / {label.ActualWidth} / {label.ToolTip}")));
        if (labels[0].FullName == labels[1].FullName || observationView.PlotNames != string.Join("\n", names)) throw new InvalidDataException("同名末级字段丢失完整路径。");
        SaveSmokeScreenshot(directory, "long-variable-names-minimum.png");
        foreach (var label in labels) label.FontSize = 18;
        await Dispatcher.InvokeAsync(() => { }, DispatcherPriority.ApplicationIdle); UpdateLayout();
        if (labels.Any(label => !label.Text.EndsWith(".position"))) throw new InvalidDataException("字体放大后未保留末级字段。");
        SaveSmokeScreenshot(directory, "long-variable-names-large-font.png");
        Width = originalWidth; Height = originalHeight;
    }
}

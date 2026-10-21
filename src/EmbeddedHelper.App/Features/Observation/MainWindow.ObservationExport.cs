using System.IO;
using System.Windows;
using EmbeddedHelper.Core;
using Microsoft.Win32;

namespace EmbeddedHelper.App;

public partial class MainWindow
{
    private string? recordingPath;
    private async void ExportRecording(object? sender, RoutedEventArgs e)
    {
        if (!observationView.CanExport) return;
        var dialog = new SaveFileDialog { Filter = "Excel 工作簿|*.xlsx", DefaultExt = ".xlsx", AddExtension = true, FileName = "采集记录-" + Path.GetFileNameWithoutExtension(recordingPath) + ".xlsx" };
        if (dialog.ShowDialog(this) == true) await Guard(() => ExportRecordingAsync(dialog.FileName));
    }
    private async Task ExportRecordingAsync(string destination)
    {
        if (!observationView.CanExport || recordingPath is not { } source) throw new InvalidOperationException("请先停止采集，再导出最近一次记录。");
        observationView.Exporting = true;
        observationView.ExportStatus = "正在导出完整记录…"; Report(observationView.ExportStatus);
        try
        {
            var count = await Task.Run(() => ObservationExcel.Export(source, destination));
            observationView.ExportStatus = $"已导出 {count} 个采集批次：{destination}";
            Report(observationView.ExportStatus);
        }
        catch (Exception ex) { observationView.ExportStatus = "导出失败：" + ex.Message; Report(observationView.ExportStatus); throw; }
        finally { observationView.Exporting = false; }
    }
}

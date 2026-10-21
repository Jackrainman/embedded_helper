using System.Diagnostics;
using System.IO;
using EmbeddedHelper.Core;

namespace EmbeddedHelper.App;

public partial class MainWindow
{
    private void PrepareRecordingPreview()
    {
        recordingPath = Path.Combine(App.Data.Root, "observations", "offline-preview-" + Guid.NewGuid().ToString("N") + ".jsonl");
        var vars = Variables.Where(v => v.Selected).Select(v => v.Variable).ToArray();
        using (var record = new ObservationRecording(recordingPath, vars, 200, "offline-synthetic"))
        {
            for (var i = 1; i <= 6001; i++) record.Append(new("sample", "", vars.ToDictionary(v => v.Identity, _ => i.ToString(System.Globalization.CultureInfo.InvariantCulture)), Sequence: i,
                SessionId: "offline-synthetic", ReadStartTicks: (long)i * Stopwatch.Frequency / 200, ReadEndTicks: (long)i * Stopwatch.Frequency / 200 + 100, ObservedAt: DateTimeOffset.UnixEpoch.AddMilliseconds(i * 5)));
            record.Complete();
        }
        observationView.HasRecording = true;
        observationView.ExportStatus = "离线合成记录：6001批，可导出检查；不是实机数据。";
    }
    public async Task PreviewRecordingAsync()
    {
        await PreviewPlotAsync(2);
        Title = "Embedded Helper " + Product.Version + " · 离线合成记录导出";
        PrepareRecordingPreview(); observationView.Busy = false; observationView.Ready = false;
        observationView.Status = observationView.ExportStatus;
    }
    private async Task SmokeRecordingAsync(string directory)
    {
        var previousPath = recordingPath;
        try
        {
            PopulatePlotPreview(2); observationView.HasSession = false; observationView.Busy = false;
            PrepareRecordingPreview();
            await ExportRecordingAsync(Path.Combine(directory, "observation-full.xlsx"));
            if (!observationView.ExportStatus.Contains("6001")) throw new InvalidDataException("完整记录导出数量不正确。");
            SaveSmokeScreenshot(directory, "recording-export-success.png");
            try { await ExportRecordingAsync(Path.Combine(directory, "missing-export-directory", "failed.xlsx")); throw new InvalidOperationException("导出写失败未阻断。"); }
            catch (DirectoryNotFoundException) { }
            if (!observationView.CanExport || !observationView.ExportStatus.StartsWith("导出失败")) throw new InvalidDataException("导出失败后无法重试。");
            SaveSmokeScreenshot(directory, "recording-export-failure.png");
        }
        finally { recordingPath = previousPath; observationView.HasRecording = previousPath != null; }
    }
}

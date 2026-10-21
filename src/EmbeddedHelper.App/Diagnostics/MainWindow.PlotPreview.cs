using System.Windows.Threading;
using EmbeddedHelper.Core;

namespace EmbeddedHelper.App;

public partial class MainWindow
{
    // Explicit diagnostic entry for offline inspection of the packaged renderer.
    // It never starts an acquisition or loads a probe SDK.
    public async Task PreviewPlotAsync(int count)
    {
        Pages.SelectedIndex = 1;
        await viewerPreparation;
        refresh.Stop();
        observationView.Busy = true;
        Title += " · 离线合成曲线预览";
        PopulatePlotPreview(count);
        await Dispatcher.InvokeAsync(() => { }, DispatcherPriority.ApplicationIdle);
    }

    private void PopulatePlotPreview(int count)
    {
        var rows = Enumerable.Range(0, count).Select(i => new VariableRow(new Variable(
            $"preview-{i}", $"离线样本 {i + 1}", (uint)(0x20000000 + i * 4), "float", 4, ""))
        { Selected = true, Value = "合成数据" }).ToArray();
        observationView.ReplaceRows(rows);
        observationView.PlotLabels = rows.Select(row => row.Variable.Name).ToArray();
        observationView.Times = rows.Select(_ => Enumerable.Range(0, 2000).Select(i => 10 + i * 0.025).ToArray()).ToArray();
        observationView.Series = rows.Select((_, row) => Enumerable.Range(0, 2000).Select(i => row == 1 ? 128.0 : Math.Sin(i * 0.03 + row) * (row + 1)).ToArray()).ToArray();
        observationView.Summary = $"离线合成曲线 · {count} 个变量";
        observationView.ElfStatus = "离线绘图验证，无需 ELF 或设备";
        observationView.Status = "离线合成数据 · 25ms 间隔 · 10–59.975s · 不代表硬件采集结果";
    }
}

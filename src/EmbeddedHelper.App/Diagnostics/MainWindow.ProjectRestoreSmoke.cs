using System.IO;
using System.Windows;
using EmbeddedHelper.Core;

namespace EmbeddedHelper.App;

public partial class MainWindow
{
    private async Task SmokeProjectRestoreAsync(string directory)
    {
        var saved = settings;
        var originalRoot = project!.Root;
        var originalElf = ProjectPage.ElfPath.Text;
        var root = Path.Combine(directory, "restore-two-targets");
        Directory.CreateDirectory(Path.Combine(root, ".eide"));
        File.WriteAllText(Path.Combine(root, ".eide", "eide.yml"), """
            name: restore_sample
            deviceName: STM32G474VETx
            targets:
              Debug: &target
                uploadConfigMap:
                  JLink:
                    cpuInfo: {cpuName: STM32G474VE}
                    proType: 1
                    speed: 4000
              Release: *target
            """);
        try
        {
            settings = settingsStore.SaveAndRead(settings with { LastProject = root, LastTarget = "Release" });
            settings = settingsStore.Load();
            await RestoreLastProjectAsync();
            if (project?.Root != root || Target?.Name != "Release" || activeTransport != "local" || acquisition.HasSession || serial != null)
                throw new InvalidDataException("工程恢复没有沿用保存目标，或错误连接了设备。");
            settings = settings with { LastTarget = "removed-target" };
            await RestoreLastProjectAsync();
            if (Target != null) throw new InvalidDataException("原目标缺失时擅自选择了其他目标。");
            settings = settings with { LastProject = Path.Combine(root, "missing") };
            await RestoreLastProjectAsync();
            if (!StatusLabel.Text.Contains("无法恢复")) throw new InvalidDataException("失效工程记忆没有就地提示。");
            SaveSmokeScreenshot(directory, "restore-missing-project.png");
        }
        finally
        {
            settings = saved;
            await LoadProjectAsync(originalRoot);
            ProjectPage.ElfPath.Text = originalElf;
            await ParseElfAsync();
            settings = settingsStore.SaveAndRead(saved);
        }
    }

    private void SmokeProbeSelection(string directory)
    {
        // Simulated USB inventory exercises the real selector without loading any SDK.
        var view = new ConnectionView();
        var window = new Window { Title = "离线探针选择验证", Owner = this, Width = 660, Height = 460, Content = view };
        try
        {
            window.Show();
            view.SetProbes([], "");
            if (view.Probes.SelectedItem != null || !view.SetupStatus.Text.Contains("未找到")) throw new InvalidDataException("零探针选择错误。");
            view.SetProbes([new(1111, "合成 J-Link V9")], "");
            if (view.ProbeSerial.Text != "1111") throw new InvalidDataException("单探针未自动选中。");
            view.SetProbes([new(1111, "合成 J-Link V9"), new(2222, "合成 J-Link V11")], "");
            if (view.Probes.SelectedItem != null) throw new InvalidDataException("多探针擅自选择。");
            view.Probes.SelectedIndex = 1;
            if (view.ProbeSerial.Text != "2222") throw new InvalidDataException("明确选择没有更新序列号。");
            view.SetProbes([new(1111, "合成 J-Link V9")], "2222");
            if (view.Probes.SelectedItem != null || !view.SetupStatus.Text.Contains("未在线")) throw new InvalidDataException("拔出记忆探针后自动改连了另一探针。");
            view.SetProbes([new(2222, "合成 J-Link V11")], "2222");
            if (view.ProbeSerial.Text != "2222") throw new InvalidDataException("记忆探针重新插入未恢复。");
        }
        finally { window.Close(); }
    }
}

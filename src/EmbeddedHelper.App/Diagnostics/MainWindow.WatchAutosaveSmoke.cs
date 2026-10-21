using System.IO;
using EmbeddedHelper.Core;

namespace EmbeddedHelper.App;

public partial class MainWindow
{
    private async Task SmokeWatchAutosaveAsync(string directory)
    {
        var root = project!.Root; var original = await WatchSettingsStore.LoadAsync(root);
        var path = Path.Combine(root, ".embedded_helper", "watch.json");
        ResetWatchEditing(); await PrepareViewerAsync();
        suppressWatchAutosave = false;
        try
        {
            foreach (var id in Variables.Where(v => v.Selected).Select(v => v.Variable.Identity).ToArray())
            {
                Variables.Single(v => v.Variable.Identity == id).Selected = false;
                await watchSaveTask;
            }
            if ((await WatchSettingsStore.LoadAsync(root))!.Identities.Length != 0 || observationView.SettingsError)
                throw new InvalidDataException("移除最后一个变量没有自动保存空列表。");
            var ids = Variables.Where(v => v.Variable.Supported).Take(2).Select(v => v.Variable.Identity).ToArray();
            foreach (var id in ids) { Variables.Single(v => v.Variable.Identity == id).Selected = true; await watchSaveTask; }
            ObservationPage.SampleRate.Text = "200"; CommitFrequency(); await watchSaveTask;
            var saved = (await WatchSettingsStore.LoadAsync(root))!;
            if (saved.Rate != 200 || saved.Identities.Length != 2 || observationView.SettingsError) throw new InvalidDataException("变量及频率未自动保存。");
            var before = File.ReadAllText(path);
            ObservationPage.SampleRate.Text = "0"; CommitFrequency(); await watchSaveTask;
            if (!observationView.SettingsError || !watchDirty || File.ReadAllText(path) != before || ObservationPage.StartButton.IsEnabled)
                throw new InvalidDataException("非法频率覆盖了有效设置或没有就地报错。");
            try { await LoadProjectAsync(root); throw new InvalidDataException("未保存修改时错误允许切换工程。"); }
            catch (InvalidOperationException) { }
            SaveSmokeScreenshot(directory, "watch-autosave-invalid.png");
            ObservationPage.SampleRate.Text = "200"; CommitFrequency(); await watchSaveTask;
            using (var locked = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.None))
            {
                ObservationPage.SampleRate.Text = "250"; CommitFrequency(); await watchSaveTask;
                if (!observationView.SettingsError) throw new InvalidDataException("文件被占用时未报告保存失败。");
            }
            QueueWatchSave(); await watchSaveTask;
            if ((await WatchSettingsStore.LoadAsync(root))!.Rate != 250 || observationView.SettingsError) throw new InvalidDataException("写入失败后重试未成功。");
            WatchSettingsStore.Save(root, saved with { Rate = 111 });
            ObservationPage.SampleRate.Text = "333"; CommitFrequency(); await watchSaveTask;
            if (!observationView.SettingsError || (await WatchSettingsStore.LoadAsync(root))!.Rate != 111) throw new InvalidDataException("并发外部修改被覆盖。");
            await ReloadWatchSettingsAsync();
            if (ObservationPage.SampleRate.Text != "111" || observationView.SettingsError || watchDirty) throw new InvalidDataException("重新读取未恢复已保存设置。");
            await LoadProjectAsync(root);
            ProjectPage.ElfPath.Text = Path.Combine(directory, "scalars.elf");
            await PrepareViewerAsync();
            if (ObservationPage.SampleRate.Text != "111" || Variables.Count(v => v.Selected) != 2 || acquisition.HasSession) throw new InvalidDataException("重新打开工程未恢复自动保存值。");
            SaveSmokeScreenshot(directory, "watch-autosave-restored.png");
        }
        finally
        {
            suppressWatchAutosave = true;
            if (original != null) WatchSettingsStore.Save(root, original);
            ResetWatchEditing(); await PrepareViewerAsync();
        }
    }
}

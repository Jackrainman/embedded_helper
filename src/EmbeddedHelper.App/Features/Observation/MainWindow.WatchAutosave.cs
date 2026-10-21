using System.Globalization;
using System.Windows;
using System.Windows.Input;
using EmbeddedHelper.Core;

namespace EmbeddedHelper.App;

public partial class MainWindow
{
    private WatchSettingsEdit? watchEdit;
    private bool suppressWatchAutosave, watchDirty;
    private Task watchSaveTask = Task.CompletedTask;
    private void ResetWatchEditing()
    {
        watchEdit = project == null ? null : new WatchSettingsEdit(project.Root);
        watchDirty = false; observationView.SettingsError = false;
        observationView.SettingsStatus = watchEdit?.Value?.Target == Target?.Name && watchEdit?.Value != null
            ? "已读取工程变量设置；修改后自动保存。" : "变量选择自动保存；频率编辑完成后自动保存。";
    }
    private void QueueWatchSave()
    {
        if (suppressWatchAutosave || project == null || Target == null || !observationView.CanEdit) return;
        watchDirty = true;
        watchSaveTask = Guard(SaveVariablesAsync);
    }
    private void CommitFrequency()
    {
        if (!watchDirty && ObservationPage.SampleRate.Text == (watchEdit?.Value is { } saved && saved.Target == Target?.Name ? saved.Rate : 200).ToString(CultureInfo.InvariantCulture)) return;
        QueueWatchSave();
    }
    private async void ReloadWatchSettings(object? sender, RoutedEventArgs e) => await Guard(ReloadWatchSettingsAsync);
    private async Task ReloadWatchSettingsAsync()
    {
        if (!observationView.CanEdit) return;
        ResetWatchEditing();
        var saved = watchEdit?.Value?.Target == Target?.Name ? watchEdit?.Value : null;
        observationView.LoadIndex(Variables.Select(v => v.Variable).ToArray(), saved?.Identities ?? []);
        ApplyWatchToSetup(saved);
        await PrepareViewerAsync();
    }
}

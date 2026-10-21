using System.IO;
using System.Windows;
using EmbeddedHelper.Core;
using Microsoft.Win32;

namespace EmbeddedHelper.App;

public partial class MainWindow
{
    private bool discoveringTools, updatingToolSelection;
    private ToolSearchResult jlinkSearch = new([], []), readElfSearch = new([], []);
    private async void DiscoverLocalTools(object? sender, RoutedEventArgs e)
    {
        if (discoveringTools || acquisition.HasSession || ObservationBusy) return;
        discoveringTools = true;
        SyncObservationState();
        var dll = SettingsPage.DllPath.Text; var readElf = SettingsPage.ReadElfPath.Text;
        var root = project?.Root;
        SettingsPage.DiscoveryNotes.Text = "正在检查已知工具目录和 VS Code 配置…";
        try
        {
            var result = await Task.Run(() =>
            {
                var context = ToolDiscovery.Current(root);
                return (ToolDiscovery.Find(LocalToolKind.JLink, dll, context), ToolDiscovery.Find(LocalToolKind.ReadElf, readElf, context));
            });
            if (project?.Root != root) { SettingsPage.DiscoveryNotes.Text = "工程已切换，请重新查找。"; return; }
            (jlinkSearch, readElfSearch) = result;
            updatingToolSelection = true;
            try
            {
                SettingsPage.JLinkCandidates.ItemsSource = jlinkSearch.Candidates;
                SettingsPage.ReadElfCandidates.ItemsSource = readElfSearch.Candidates;
                SettingsPage.JLinkCandidates.SelectedItem = jlinkSearch.Candidates.FirstOrDefault(c => c.Path.Equals(settings.Dll, StringComparison.OrdinalIgnoreCase));
                SettingsPage.ReadElfCandidates.SelectedItem = readElfSearch.Candidates.FirstOrDefault(c => c.Path.Equals(settings.ReadElf, StringComparison.OrdinalIgnoreCase));
            }
            finally { updatingToolSelection = false; }
            SettingsPage.DiscoveryNotes.Text = $"找到 J-Link 软件组件 {jlinkSearch.Candidates.Length} 项、外部 ELF 工具 {readElfSearch.Candidates.Length} 项。选择后自动校验并保存；这里不选择或连接实体探针。\n" + string.Join("\n", jlinkSearch.Notes.Concat(readElfSearch.Notes).Distinct());
            // Only adopt a unique candidate when the user has no saved or edited selection.
            if (string.IsNullOrWhiteSpace(settings.Dll) && SettingsPage.DllPath.Text == dll)
            {
                var suggestion = ToolDiscovery.Suggest(dll, jlinkSearch);
                if (!string.IsNullOrWhiteSpace(suggestion)) SaveToolPath(LocalToolKind.JLink, suggestion);
            }
            if (string.IsNullOrWhiteSpace(settings.ReadElf) && SettingsPage.ReadElfPath.Text == readElf)
            {
                var suggestion = ToolDiscovery.Suggest(readElf, readElfSearch);
                if (!string.IsNullOrWhiteSpace(suggestion)) SaveToolPath(LocalToolKind.ReadElf, suggestion);
            }
            RefreshLocalToolStatus();
        }
        catch (Exception ex) { SettingsPage.DiscoveryNotes.Text = "查找失败，可手动选择文件：" + ex.Message; }
        finally { discoveringTools = false; SyncObservationState(); }
    }
    private void ToolSelectionChanged(LocalToolKind kind)
    {
        if (updatingToolSelection || discoveringTools || !pagesReady) return;
        if ((kind == LocalToolKind.JLink ? SettingsPage.JLinkCandidates : SettingsPage.ReadElfCandidates).SelectedItem is ToolCandidate candidate)
            SaveToolPath(kind, candidate.Path);
    }
    private void SaveToolPath(LocalToolKind kind, string path)
    {
        if (!pagesReady) return;
        var field = kind == LocalToolKind.JLink ? SettingsPage.DllPath : SettingsPage.ReadElfPath;
        var previous = kind == LocalToolKind.JLink ? settings.Dll : settings.ReadElf;
        path = path.Trim();
        try
        {
            if (acquisition.HasSession || ObservationBusy || configurationBusy)
                throw new InvalidOperationException("请先停止采集并等待当前操作结束，再更换组件。");
            if (path.Length > 0)
            {
                var checkedFile = ToolDiscovery.Inspect(kind, path, "所选组件");
                if (!checkedFile.Valid) throw new InvalidDataException(checkedFile.Status);
            }
            var next = kind == LocalToolKind.JLink ? settings with { Dll = path } : settings with { ReadElf = path };
            settings = settingsStore.SaveAndRead(next);
            field.Text = path;
            SettingsPage.DiscoveryNotes.Text = "组件设置已保存并回读确认；未连接设备。";
        }
        catch (Exception ex)
        {
            field.Text = previous;
            SettingsPage.DiscoveryNotes.Text = "组件设置未生效，当前仍使用上次保存值：" + ex.Message;
        }
        updatingToolSelection = true;
        try
        {
            var current = kind == LocalToolKind.JLink ? settings.Dll : settings.ReadElf;
            var search = kind == LocalToolKind.JLink ? jlinkSearch : readElfSearch;
            (kind == LocalToolKind.JLink ? SettingsPage.JLinkCandidates : SettingsPage.ReadElfCandidates).SelectedItem = search.Candidates.FirstOrDefault(c => c.Path.Equals(current, StringComparison.OrdinalIgnoreCase));
        }
        finally { updatingToolSelection = false; }
        RefreshLocalToolStatus();
    }
    private void BrowseReadElf(object? sender, RoutedEventArgs e)
    {
        var dialog = new OpenFileDialog { Filter = "ARM ELF 解析工具|arm-none-eabi-readelf.exe|可执行文件|*.exe" };
        if (dialog.ShowDialog(this) == true) SaveToolPath(LocalToolKind.ReadElf, dialog.FileName);
    }
    private void RefreshLocalToolStatus()
    {
        if (SettingsPage.JLinkStatus == null || SettingsPage.ReadElfStatus == null) return;
        string Describe(LocalToolKind kind, string path, ToolSearchResult search)
        {
            if (string.IsNullOrWhiteSpace(path)) return kind == LocalToolKind.JLink ? "未指定：请自动查找或手动选择" : "内置解析可用；外部工具未指定，部分 ELF 可能需要补齐";
            var source = search.Candidates.FirstOrDefault(c => c.Path.Equals(path, StringComparison.OrdinalIgnoreCase))?.Source ?? "当前指定";
            var item = ToolDiscovery.Inspect(kind, path, source);
            return $"{(kind == LocalToolKind.ReadElf ? "内置解析可用；外部工具：" : "")}{item.Source} · {item.Version} · {item.Status}";
        }
        SettingsPage.JLinkStatus.Text = Describe(LocalToolKind.JLink, SettingsPage.DllPath.Text, jlinkSearch);
        SettingsPage.ReadElfStatus.Text = Describe(LocalToolKind.ReadElf, SettingsPage.ReadElfPath.Text, readElfSearch);
    }
}

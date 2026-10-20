using System.IO;
using System.Windows;
using EmbeddedHelper.Core;
using Microsoft.Win32;

namespace EmbeddedHelper.App;

public partial class MainWindow
{
    private bool discoveringTools;
    private ToolSearchResult jlinkSearch = new([], []), readElfSearch = new([], []);
    private async void DiscoverLocalTools(object? sender, RoutedEventArgs e)
    {
        if (discoveringTools) return;
        discoveringTools = true;
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
            SettingsPage.JLinkCandidates.ItemsSource = jlinkSearch.Candidates;
            SettingsPage.ReadElfCandidates.ItemsSource = readElfSearch.Candidates;
            // Respect edits made while the background search was running.
            if (SettingsPage.DllPath.Text == dll) SettingsPage.DllPath.Text = ToolDiscovery.Suggest(dll, jlinkSearch);
            if (SettingsPage.ReadElfPath.Text == readElf) SettingsPage.ReadElfPath.Text = ToolDiscovery.Suggest(readElf, readElfSearch);
            SettingsPage.JLinkCandidates.SelectedItem = jlinkSearch.Candidates.FirstOrDefault(c => c.Path.Equals(SettingsPage.DllPath.Text, StringComparison.OrdinalIgnoreCase));
            SettingsPage.ReadElfCandidates.SelectedItem = readElfSearch.Candidates.FirstOrDefault(c => c.Path.Equals(SettingsPage.ReadElfPath.Text, StringComparison.OrdinalIgnoreCase));
            SettingsPage.DiscoveryNotes.Text = $"找到 J-Link {jlinkSearch.Candidates.Length} 项、外部 ELF 工具 {readElfSearch.Candidates.Length} 项。多份候选请选择后点击“使用所选组件”；修改后保存。不自动替换现有路径。\n" + string.Join("\n", jlinkSearch.Notes.Concat(readElfSearch.Notes).Distinct());
            RefreshLocalToolStatus();
        }
        catch (Exception ex) { SettingsPage.DiscoveryNotes.Text = "查找失败，可手动选择文件：" + ex.Message; }
        finally { discoveringTools = false; }
    }
    private void UseLocalTool(LocalToolKind kind)
    {
        var candidate = (kind == LocalToolKind.JLink ? SettingsPage.JLinkCandidates : SettingsPage.ReadElfCandidates).SelectedItem as ToolCandidate;
        if (candidate == null) { Report("请先选择一个候选组件。"); return; }
        var checkedFile = ToolDiscovery.Inspect(kind, candidate.Path, candidate.Source);
        if (!checkedFile.Valid) { Report(checkedFile.Status); return; }
        (kind == LocalToolKind.JLink ? SettingsPage.DllPath : SettingsPage.ReadElfPath).Text = candidate.Path;
        RefreshLocalToolStatus();
        Report("已选择组件，请保存本机设置；未连接设备。");
    }
    private void BrowseReadElf(object? sender, RoutedEventArgs e)
    {
        var dialog = new OpenFileDialog { Filter = "ARM ELF 解析工具|arm-none-eabi-readelf.exe|可执行文件|*.exe" };
        if (dialog.ShowDialog(this) == true) SettingsPage.ReadElfPath.Text = dialog.FileName;
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
    private void ValidateLocalTools()
    {
        foreach (var (kind, path) in new[] { (LocalToolKind.JLink, SettingsPage.DllPath.Text), (LocalToolKind.ReadElf, SettingsPage.ReadElfPath.Text) })
            if (!string.IsNullOrWhiteSpace(path))
            {
                var result = ToolDiscovery.Inspect(kind, path, "当前指定");
                if (!result.Valid) throw new InvalidDataException($"{ToolDiscovery.FileName(kind)}：{result.Status}");
            }
        RefreshLocalToolStatus();
    }
}

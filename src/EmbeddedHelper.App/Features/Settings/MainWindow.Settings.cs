using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.IO.Ports;
using System.Text;
using System.Windows;
using EmbeddedHelper.Core;
using Microsoft.Win32;

namespace EmbeddedHelper.App;

public partial class MainWindow
{
    private readonly SettingsStore settingsStore = new(App.Data);
    private AppSettings settings = new();
    private bool toolBusy;

    private void OpenLogs(object? sender, RoutedEventArgs e) => OpenDirectory(System.IO.Path.Combine(App.Data.Root, "logs"));
    private void OpenDirectory(string path) { try { Directory.CreateDirectory(path); Process.Start(new ProcessStartInfo(path) { UseShellExecute = true }); } catch (Exception ex) { Report(ex.Message); } }
    private void BrowseDll(object? sender, RoutedEventArgs e)
    { var dialog = new OpenFileDialog { Filter = "SEGGER 64 位库|JLink_x64.dll|DLL|*.dll" }; if (dialog.ShowDialog(this) == true) SettingsPage.DllPath.Text = dialog.FileName; }
    private void SaveSettings(object? sender, RoutedEventArgs e)
    { try { PersistSettings(); Report("设置已保存。升级时保留程序目录下的 data。"); } catch (Exception ex) { Report("设置保存失败：" + ex.Message); } }
    private async void ImportTool(object? sender, RoutedEventArgs e)
    {
        var dialog = new OpenFileDialog { Filter = "Debug_LAN 发布包|DebugLAN-*.zip|ZIP|*.zip" };
        if (dialog.ShowDialog(this) == true) await Guard(() => PrepareToolAsync(dialog.FileName));
    }
    private async void DownloadTool(object? sender, RoutedEventArgs e) => await Guard(() => PrepareToolAsync(null));
    private async void RefreshTools(object? sender, RoutedEventArgs e) => await Guard(async () =>
    {
        if (toolBusy) throw new InvalidOperationException("工具正在准备，请稍候。");
        toolBusy = true;
        try
        {
            var version = (SettingsPage.ToolVersionBox.SelectedItem as ToolRelease)?.Version;
            SettingsPage.ToolStatus.Text = "正在查询官方发布清单并核对标签提交和摘要；工程版本不会自动改变。";
            var available = await ToolCatalog.RefreshAsync(App.Data.Root);
            SettingsPage.ToolVersionBox.ItemsSource = available;
            SettingsPage.ToolVersionBox.SelectedItem = available.FirstOrDefault(v => v.Version == version) ?? available.First();
            SettingsPage.ToolStatus.Text = $"已核验 {available.Length} 个兼容工具版本。选择后下载或导入，再预览更新工程锁。";
        }
        catch (Exception ex) { SettingsPage.ToolStatus.Text = "更新检查失败，已安装工具仍可离线使用：" + ex.Message; throw; }
        finally { toolBusy = false; }
    });
    private async Task PrepareToolAsync(string? zip)
    {
        if (toolBusy) throw new InvalidOperationException("工具正在准备，请稍候。");
        var release = SettingsPage.ToolVersionBox.SelectedItem as ToolRelease ?? throw new InvalidOperationException("请选择工具版本。");
        toolBusy = true;
        SettingsPage.ToolStatus.Text = "正在校验并准备 " + release.Version + "；仅运行无硬件版本检查。";
        try
        {
            var executable = await ToolPreparationService.PrepareAsync(App.Data.Root, release, zip);
            SettingsPage.ToolStatus.Text = "已准备 " + release.Version + "\n" + executable + "\n工程版本尚未改变；到远程配置页预览接入或回退。";
        }
        catch (Exception ex) { SettingsPage.ToolStatus.Text = "工具准备失败：" + ex.Message; throw; }
        finally { toolBusy = false; }
    }
    private void BindCli(object? sender, RoutedEventArgs e)
    {
        try
        {
            var entry = RuntimePaths.Current.ExternalEntry(Environment.GetEnvironmentVariable(RuntimePaths.LauncherVariable));
            if (!File.Exists(entry)) throw new FileNotFoundException("未找到稳定入口，请从完整便携包启动。", entry);
            Environment.SetEnvironmentVariable(RuntimePaths.EntryVariable, entry, EnvironmentVariableTarget.User);
            Environment.SetEnvironmentVariable(RuntimePaths.EntryVariable, entry);
            Report("已绑定稳定入口；请预览并应用旧工程接入以迁移命令，再完全退出并重新打开 VS Code。旧入口变量保持原值；搬迁后重新绑定。");
        }
        catch (Exception ex) { Report("入口绑定失败：" + ex.Message); }
    }
    private void PersistSettings()
    {
        settings = settings with { Dll = SettingsPage.DllPath.Text, ReadElf = SettingsPage.ReadElfPath.Text, Width = ActualWidth > 0 ? ActualWidth : Width, Height = ActualHeight > 0 ? ActualHeight : Height, Serial = ReadSerialSettings(), SerialEncoding = SerialPage.EncodingBox.SelectedItem as string ?? "utf-8" };
        settingsStore.Save(settings);
    }
    private void EnvironmentCheck(object? sender, RoutedEventArgs e)
    {
        SettingsPage.EnvironmentText.Text = $"版本：{Product.Version}\n系统：{Environment.OSVersion} / {System.Runtime.InteropServices.RuntimeInformation.ProcessArchitecture}\n程序目录：{AppContext.BaseDirectory}\n数据目录：{App.Data.Root}\nSEGGER 库：{(File.Exists(SettingsPage.DllPath.Text) ? "文件存在（未加载、未连接设备）" : "缺失，请选择 JLink_x64.dll")}\n硬件状态：未检测；仅用户点击开始/打开串口时连接。\n本包自包含运行时；纯净 Windows 与硬件验收仍待用户执行。";
    }
    private void InitializePageSettings()
    {
        SerialPage.ParityBox.ItemsSource = Enum.GetValues<Parity>(); SerialPage.ParityBox.SelectedItem = Parity.None;
        SerialPage.StopBitsBox.ItemsSource = new[] { StopBits.One, StopBits.Two, StopBits.OnePointFive }; SerialPage.StopBitsBox.SelectedItem = StopBits.One;
        SerialPage.FlowBox.ItemsSource = Enum.GetValues<Handshake>(); SerialPage.FlowBox.SelectedItem = Handshake.None;
        try { settings = settingsStore.Load(); }
        catch (Exception ex) { Report("设置读取失败，保留原文件并使用默认值：" + ex.Message); }
        SettingsPage.DllPath.Text = settings.Dll;
        SerialPage.EncodingBox.ItemsSource = new[] { "utf-8", "gb18030", "us-ascii", "utf-16" };
        SerialPage.EncodingBox.SelectedItem = settings.SerialEncoding;
        if (SerialPage.EncodingBox.SelectedIndex < 0) SerialPage.EncodingBox.SelectedIndex = 0;
        SerialPage.SendHistoryBox.ItemsSource = settings.SendHistory ?? [];
        try { ToolCatalog.Load(App.Data.Root); } catch (Exception ex) { Report("工具目录记录无法读取：" + ex.Message); }
        SettingsPage.ToolVersionBox.ItemsSource = ToolRelease.Available; SettingsPage.ToolVersionBox.SelectedIndex = 0;
        SettingsPage.ReadElfPath.Text = string.IsNullOrEmpty(settings.ReadElf) ? ReadElfIndex.FindTool() ?? "" : settings.ReadElf;
        Width = Math.Clamp(settings.Width, MinWidth, Math.Max(MinWidth, SystemParameters.WorkArea.Width));
        Height = Math.Clamp(settings.Height, MinHeight, Math.Max(MinHeight, SystemParameters.WorkArea.Height));
        ProjectPage.RecentProjects.ItemsSource = settings.Recent ?? [];
        if (settings.Serial is { } saved)
        {
            SerialPage.SerialPortBox.Text = saved.Port; SerialPage.BaudBox.Text = saved.Baud.ToString(CultureInfo.InvariantCulture);
            SerialPage.DataBitsBox.SelectedIndex = Math.Clamp(saved.DataBits - 5, 0, 3); SerialPage.ParityBox.SelectedItem = saved.Parity;
            SerialPage.StopBitsBox.SelectedItem = saved.StopBits; SerialPage.FlowBox.SelectedItem = saved.FlowControl;
        }
    }
}

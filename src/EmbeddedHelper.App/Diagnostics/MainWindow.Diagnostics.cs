using System.IO;
using System.IO.Ports;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using EmbeddedHelper.Core;

namespace EmbeddedHelper.App;

public partial class MainWindow
{
    public async Task SmokeAsync(string directory)
    {
        try
        {
            Directory.CreateDirectory(directory);
            await LoadProjectAsync(System.IO.Path.Combine(directory, "project"));
            ProjectPage.ElfPath.Text = System.IO.Path.Combine(directory, "scalars.elf");
            await ParseElfAsync();
            if (Variables.Count(v => v.Variable.Supported) != 7) throw new InvalidDataException("标量类型自检失败。");
            RemotePage.RemoteHost.Text = "test.example.invalid"; PreviewConfig(null, null!);
            if (plan == null) throw new InvalidDataException("远程配置预览失败。");
            var toolPackage = System.IO.Path.Combine(directory, "debug-lan.zip");
            if (File.Exists(toolPackage))
            {
                await PrepareToolAsync(toolPackage);
                RemotePage.UseEideUpload.IsChecked = true; RemotePage.GdbPort.Text = "2450"; RemotePage.WebAddress.Text = "http://test.example.invalid:8000";
                PreviewIntegration(null, null!);
                if (plan == null || plan.Changes.Length == 0) throw new InvalidDataException("一键接入预览失败。");
                await ApplyConfigAsync();
                await LoadProjectAsync(project!.Root);
                if (RemotePage.UseEideUpload.IsChecked != true || RemotePage.GdbPort.Text != "2450" || RemotePage.WebAddress.Text != "http://test.example.invalid:8000") throw new InvalidDataException("远程配置读回失败。");
                ProjectPage.ElfPath.Text = System.IO.Path.Combine(directory, "scalars.elf"); await ParseElfAsync();
                PreviewIntegration(null, null!);
                if (plan == null || plan.Changes.Length != 0) throw new InvalidDataException("重复接入非幂等。");
                PreviewRemoveIntegration(null, null!);
                if (plan == null || plan.Changes.Length == 0) throw new InvalidDataException("共享移除预览失败。");
                await ApplyConfigAsync();
            }
            PersistSettings();
            var restored = settingsStore.Load();
            if (restored.Recent?.Contains(project!.Root) != true) throw new InvalidDataException("设置回读失败。");
            var savedElf = ProjectPage.ElfPath.Text;
            ProjectPage.ElfPath.Text = System.IO.Path.Combine(directory, "missing.elf");
            await Guard(ParseElfAsync);
            if (!StatusLabel.Text.StartsWith("操作未完成：", StringComparison.Ordinal)) throw new InvalidDataException("ELF 错误未反馈到界面。");
            ProjectPage.ElfPath.Text = savedElf;
            await Guard(() => SendSerialAsync(CancellationToken.None));
            if (!StatusLabel.Text.Contains("请先打开串口")) throw new InvalidDataException("未连接串口的错误未反馈。");
            SerialPage.SerialPortBox.Text = "";
            await Guard(ConnectSerialAsync);
            if (serial != null || SerialPage.ConnectSerialButton.Content?.ToString() != "打开串口" || !SerialPage.ConnectSerialButton.IsEnabled || SerialPage.SerialConnectionLabel.Text != "未连接" || SerialPage.SerialConnectionLight.Fill != Brushes.Firebrick) throw new InvalidDataException("打开失败后串口按钮和红灯未恢复。");
            Report("离线 GUI 自检：工程、ELF、设置与错误反馈通过；硬件未验收。");
            for (int i = 0; i < Pages.Items.Count; i++)
            {
                Pages.SelectedIndex = i;
                await Dispatcher.InvokeAsync(() => { }, DispatcherPriority.ApplicationIdle);
                SaveSmokeScreenshot(directory, $"page-{i}.png");
            }
            Pages.SelectedIndex = 0;
            for (int i = 0; i < ProjectSections.Items.Count; i++)
            {
                ProjectSections.SelectedIndex = i;
                await Dispatcher.InvokeAsync(() => { }, DispatcherPriority.ApplicationIdle);
                SaveSmokeScreenshot(directory, $"project-section-{i}.png");
            }
            PortableData.SaveFile(System.IO.Path.Combine(directory, "smoke.json"), new { status = "passed", exitCode = 0, version = Product.Version, pages = Pages.Items.Count, scalarCount = Variables.Count, toolIntegration = File.Exists(toolPackage), method = "automated", hardware = "not-tested" });
            Close();
        }
        catch (Exception ex)
        {
            PortableData.SaveFile(System.IO.Path.Combine(directory, "smoke.json"), new { status = "failed", exitCode = 1, error = ex.ToString() });
            closing = true; refresh.Stop(); Application.Current.Shutdown(1);
        }
    }

    private void SaveSmokeScreenshot(string directory, string file)
    {
        UpdateLayout();
        var bitmap = new RenderTargetBitmap((int)ActualWidth, (int)ActualHeight, 96, 96, PixelFormats.Pbgra32);
        bitmap.Render(this);
        var encoder = new PngBitmapEncoder(); encoder.Frames.Add(BitmapFrame.Create(bitmap));
        using var output = File.Create(System.IO.Path.Combine(directory, file)); encoder.Save(output);
    }
}

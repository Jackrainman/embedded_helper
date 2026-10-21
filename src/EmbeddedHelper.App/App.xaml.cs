using System.IO;
using System.Windows;
using EmbeddedHelper.Core;

namespace EmbeddedHelper.App;

public partial class App : Application
{
    public static PortableData Data { get; } = new(Product.HomeDirectory);
    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);
        DispatcherUnhandledException += (_, error) =>
        {
            try { Data.Log(error.Exception.ToString()); } catch (IOException) { }
            MessageBox.Show("操作未完成：" + error.Exception.Message, "Embedded Helper", MessageBoxButton.OK, MessageBoxImage.Error);
            error.Handled = true;
        };
        try
        {
            Data.Log("启动 " + Product.Version);
            var window = new MainWindow();
            MainWindow = window;
            if (e.Args is ["--smoke", var directory]) window.Loaded += async (_, _) => await window.SmokeAsync(directory);
            if (e.Args is ["--recording-preview"]) window.Loaded += async (_, _) => await window.PreviewRecordingAsync();
            if (e.Args is ["--serial-preview"]) window.Loaded += (_, _) => window.PreviewSerial();
            if (e.Args is ["--plot-preview", var count] && int.TryParse(count, out var rows) && rows is >= 1 and <= 16)
                window.Loaded += async (_, _) => await window.PreviewPlotAsync(rows);
            else if (e.Args.Length == 0) window.Loaded += async (_, _) => await window.RestoreLastProjectAsync();
            window.Show();
        }
        catch (Exception ex)
        {
            try { Data.Log(ex.ToString()); } catch (IOException) { }
            if (e.Args is ["--smoke", var directory])
            {
                PortableData.SaveFile(Path.Combine(directory, "smoke.json"), new { status = "failed", exitCode = 1, error = ex.ToString() });
                Shutdown(1);
                return;
            }
            MessageBox.Show("无法启动，请将完整程序目录移动到可写位置。\n" + ex.Message, "Embedded Helper 启动失败", MessageBoxButton.OK, MessageBoxImage.Error);
            Shutdown(1);
        }
    }
}

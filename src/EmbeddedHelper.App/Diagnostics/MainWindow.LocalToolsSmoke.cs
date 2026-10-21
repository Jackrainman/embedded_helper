using System.IO;
using EmbeddedHelper.Core;

namespace EmbeddedHelper.App;

public partial class MainWindow
{
    private void SmokeLocalToolSettings(string directory)
    {
        var saved = settings;
        var originalDll = SettingsPage.DllPath.Text;
        var originalReadElf = SettingsPage.ReadElfPath.Text;
        var settingsPath = Path.Combine(App.Data.Root, "settings", "window.json");
        var backup = settingsPath + ".r33-smoke-backup";
        try
        {
            // A PE fixture tests path persistence only; it is never run as an ELF tool.
            var fixture = Path.Combine(directory, "readelf-pe-fixture.exe");
            File.Copy(Environment.ProcessPath!, fixture, true);
            SaveToolPath(LocalToolKind.ReadElf, fixture);
            if (settingsStore.Load().ReadElf != fixture || SettingsPage.ReadElfPath.Text != fixture)
                throw new InvalidDataException("工具有效选择没有自动保存并回读。");
            SaveToolPath(LocalToolKind.ReadElf, Path.Combine(directory, "missing-tool.exe"));
            if (settingsStore.Load().ReadElf != fixture || SettingsPage.ReadElfPath.Text != fixture)
                throw new InvalidDataException("无效工具覆盖了上次保存值。");
            SettingsPage.ReadElfPath.Text = "unsaved invalid draft";
            PersistSettings();
            if (settingsStore.Load().ReadElf != fixture)
                throw new InvalidDataException("通用设置保存写入了未校验草稿。");
            SettingsPage.ReadElfPath.Text = fixture;
            preparing = true;
            SaveToolPath(LocalToolKind.ReadElf, "");
            preparing = false;
            if (settingsStore.Load().ReadElf != fixture)
                throw new InvalidDataException("连接处理中允许替换组件。");
            File.Move(settingsPath, backup);
            Directory.CreateDirectory(settingsPath);
            try
            {
                SaveToolPath(LocalToolKind.ReadElf, "");
                if (settings.ReadElf != fixture || SettingsPage.ReadElfPath.Text != fixture || !SettingsPage.DiscoveryNotes.Text.Contains("未生效"))
                    throw new InvalidDataException("工具设置写入失败没有保留当前值或明确反馈。");
            }
            finally { Directory.Delete(settingsPath); File.Move(backup, settingsPath); }
            SaveSmokeScreenshot(directory, "local-tools-save-failure.png");
        }
        finally
        {
            preparing = false;
            settings = settingsStore.SaveAndRead(saved);
            SettingsPage.DllPath.Text = originalDll;
            SettingsPage.ReadElfPath.Text = originalReadElf;
            RefreshLocalToolStatus();
        }
    }
}

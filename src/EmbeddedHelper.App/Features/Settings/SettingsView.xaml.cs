using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;

namespace EmbeddedHelper.App;

public partial class SettingsView : UserControl
{
    public SettingsView() => InitializeComponent();
    public event RoutedEventHandler? DiscoverToolsRequested;
    private void DiscoverTools(object? sender, RoutedEventArgs e) => DiscoverToolsRequested?.Invoke(sender, e);
    public event RoutedEventHandler? UseJLinkRequested;
    private void UseJLink(object? sender, RoutedEventArgs e) => UseJLinkRequested?.Invoke(sender, e);
    public event RoutedEventHandler? UseReadElfRequested;
    private void UseReadElf(object? sender, RoutedEventArgs e) => UseReadElfRequested?.Invoke(sender, e);
    public event RoutedEventHandler? BrowseReadElfRequested;
    private void BrowseReadElf(object? sender, RoutedEventArgs e) => BrowseReadElfRequested?.Invoke(sender, e);
    public event RoutedEventHandler? BrowseDllRequested;
    private void BrowseDll(object? sender, RoutedEventArgs e) => BrowseDllRequested?.Invoke(sender, e);
    public event RoutedEventHandler? SaveSettingsRequested;
    private void SaveSettings(object? sender, RoutedEventArgs e) => SaveSettingsRequested?.Invoke(sender, e);
    public event RoutedEventHandler? EnvironmentCheckRequested;
    private void EnvironmentCheck(object? sender, RoutedEventArgs e) => EnvironmentCheckRequested?.Invoke(sender, e);
    public event RoutedEventHandler? OpenLogsRequested;
    private void OpenLogs(object? sender, RoutedEventArgs e) => OpenLogsRequested?.Invoke(sender, e);
    public event RoutedEventHandler? RefreshToolsRequested;
    private void RefreshTools(object? sender, RoutedEventArgs e) => RefreshToolsRequested?.Invoke(sender, e);
    public event RoutedEventHandler? DownloadToolRequested;
    private void DownloadTool(object? sender, RoutedEventArgs e) => DownloadToolRequested?.Invoke(sender, e);
    public event RoutedEventHandler? ImportToolRequested;
    private void ImportTool(object? sender, RoutedEventArgs e) => ImportToolRequested?.Invoke(sender, e);
    public event RoutedEventHandler? BindCliRequested;
    private void BindCli(object? sender, RoutedEventArgs e) => BindCliRequested?.Invoke(sender, e);
}

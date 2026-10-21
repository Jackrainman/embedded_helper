using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;

namespace EmbeddedHelper.App;

public partial class RemoteView : UserControl
{
    public RemoteView() => InitializeComponent();
    public event RoutedEventHandler? ImportLanRequested;
    private void ImportLan(object? sender, RoutedEventArgs e) => ImportLanRequested?.Invoke(sender, e);
    public event RoutedEventHandler? ImportLanZipRequested;
    private void ImportLanZip(object? sender, RoutedEventArgs e) => ImportLanZipRequested?.Invoke(sender, e);
    public event RoutedEventHandler? OpenWebRequested;
    private void OpenWeb(object? sender, RoutedEventArgs e) => OpenWebRequested?.Invoke(sender, e);
    public event RoutedEventHandler? PreviewIntegrationRequested;
    private void PreviewIntegration(object? sender, RoutedEventArgs e) => PreviewIntegrationRequested?.Invoke(sender, e);
    public event RoutedEventHandler? PreviewConfigRequested;
    private void PreviewConfig(object? sender, RoutedEventArgs e) => PreviewConfigRequested?.Invoke(sender, e);
    public event RoutedEventHandler? PreviewRemoveIntegrationRequested;
    private void PreviewRemoveIntegration(object? sender, RoutedEventArgs e) => PreviewRemoveIntegrationRequested?.Invoke(sender, e);
    public event RoutedEventHandler? RecoverConfigRequested;
    private void RecoverConfig(object? sender, RoutedEventArgs e) => RecoverConfigRequested?.Invoke(sender, e);
    public event RoutedEventHandler? ApplyConfigRequested;
    private void ApplyConfig(object? sender, RoutedEventArgs e) => ApplyConfigRequested?.Invoke(sender, e);
}

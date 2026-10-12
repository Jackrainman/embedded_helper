using System.Windows;
using System.Windows.Controls;

namespace EmbeddedHelper.App;

public partial class ObservationSetupView : UserControl
{
    public ObservationSetupView() => InitializeComponent();
    public event RoutedEventHandler? SaveRequested;
    private void Save(object? sender, RoutedEventArgs e) => SaveRequested?.Invoke(sender, e);
    public event RoutedEventHandler? ParseElfRequested;
    private void ParseElf(object? sender, RoutedEventArgs e) => ParseElfRequested?.Invoke(sender, e);
    private void TransportChanged(object? sender, SelectionChangedEventArgs e)
    {
        if (EnableRtt == null || ProbeSerial == null) return;
        var local = TransportBox.SelectedIndex == 0;
        EnableRtt.IsEnabled = local;
        ProbeSerial.IsEnabled = local;
        if (!local) EnableRtt.IsChecked = false;
    }
}

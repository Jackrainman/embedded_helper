using System.Windows;
using System.Windows.Controls;
using EmbeddedHelper.Core;
namespace EmbeddedHelper.App;

public partial class ConnectionView : UserControl
{
    public ConnectionView() => InitializeComponent();
    public event RoutedEventHandler? SaveRequested;
    private void Save(object sender, RoutedEventArgs e) => SaveRequested?.Invoke(sender, e);
    public event RoutedEventHandler? RefreshRequested;
    private void Refresh(object sender, RoutedEventArgs e) => RefreshRequested?.Invoke(sender, e);
    private void ProbeChanged(object sender, SelectionChangedEventArgs e)
    {
        if (ProbeSerial != null) ProbeSerial.Text = Probes.SelectedItem is UsbProbe probe ? probe.SerialNumber.ToString(System.Globalization.CultureInfo.InvariantCulture) : "";
        if (SaveButton != null) SaveButton.IsEnabled = Probes.SelectedItem is UsbProbe;
    }
    internal void SetProbes(UsbProbe[] probes, string remembered)
    {
        var choice = ProbeSelection.Resolve(probes, remembered);
        Probes.ItemsSource = probes;
        Probes.SelectedItem = choice.Selected;
        SetupStatus.Text = choice.Message;
    }
}

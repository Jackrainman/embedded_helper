using System.Windows;
using System.Windows.Controls;
namespace EmbeddedHelper.App;

public partial class RttView : UserControl
{
    public RttView() => InitializeComponent();
    public event RoutedEventHandler? StartRequested, StopRequested, ConfigureRequested, ClearRequested;
    private void Start(object sender, RoutedEventArgs e) => StartRequested?.Invoke(sender, e);
    private void Stop(object sender, RoutedEventArgs e) => StopRequested?.Invoke(sender, e);
    private void Configure(object sender, RoutedEventArgs e) => ConfigureRequested?.Invoke(sender, e);
    private void Clear(object sender, RoutedEventArgs e) => ClearRequested?.Invoke(sender, e);
    private void TextChanged(object sender, TextChangedEventArgs e) => RttText.ScrollToEnd();
}

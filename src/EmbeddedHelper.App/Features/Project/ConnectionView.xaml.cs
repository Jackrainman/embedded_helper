using System.Windows;
using System.Windows.Controls;
namespace EmbeddedHelper.App;

public partial class ConnectionView : UserControl
{
    public ConnectionView() => InitializeComponent();
    public event RoutedEventHandler? SaveRequested;
    private void Save(object sender, RoutedEventArgs e) => SaveRequested?.Invoke(sender, e);
}

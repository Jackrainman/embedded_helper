using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;

namespace EmbeddedHelper.App;

public partial class ObservationView : UserControl
{
    public ObservationView() => InitializeComponent();
    public event RoutedEventHandler? ConfigureRequested;
    private void Configure(object? sender, RoutedEventArgs e) => ConfigureRequested?.Invoke(sender, e);
    public event RoutedEventHandler? StartObservationRequested;
    private void StartObservation(object? sender, RoutedEventArgs e) => StartObservationRequested?.Invoke(sender, e);
    public event RoutedEventHandler? StopObservationRequested;
    private void StopObservation(object? sender, RoutedEventArgs e) => StopObservationRequested?.Invoke(sender, e);
    public event RoutedEventHandler? ParseElfRequested;
    private void ParseElf(object? sender, RoutedEventArgs e) => ParseElfRequested?.Invoke(sender, e);
    public event SizeChangedEventHandler? PlotSizeChangedRequested;
    private void PlotSizeChanged(object? sender, SizeChangedEventArgs e) => PlotSizeChangedRequested?.Invoke(sender, e);
    public event RoutedEventHandler? ClearRttRequested;
    private void ClearRtt(object? sender, RoutedEventArgs e) => ClearRttRequested?.Invoke(sender, e);
}

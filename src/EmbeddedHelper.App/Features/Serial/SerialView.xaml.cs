using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;

namespace EmbeddedHelper.App;

public partial class SerialView : UserControl
{
    public SerialView() => InitializeComponent();
    public event RoutedEventHandler? RefreshPortsRequested;
    private void RefreshPorts(object? sender, RoutedEventArgs e) => RefreshPortsRequested?.Invoke(sender, e);
    public event RoutedEventHandler? ToggleSerialRequested;
    private void ToggleSerial(object? sender, RoutedEventArgs e) => ToggleSerialRequested?.Invoke(sender, e);
    public event RoutedEventHandler? SerialEncodingChangedRequested;
    private void SerialEncodingChanged(object? sender, RoutedEventArgs e) => SerialEncodingChangedRequested?.Invoke(sender, e);
    public event RoutedEventHandler? ClearSerialRequested;
    private void ClearSerial(object? sender, RoutedEventArgs e) => ClearSerialRequested?.Invoke(sender, e);
    public event SelectionChangedEventHandler? SelectSendHistoryRequested;
    private void SelectSendHistory(object? sender, SelectionChangedEventArgs e) => SelectSendHistoryRequested?.Invoke(sender, e);
    public event RoutedEventHandler? SendSerialRequested;
    private void SendSerial(object? sender, RoutedEventArgs e) => SendSerialRequested?.Invoke(sender, e);
    public event RoutedEventHandler? StartPeriodicRequested;
    private void StartPeriodic(object? sender, RoutedEventArgs e) => StartPeriodicRequested?.Invoke(sender, e);
    public event RoutedEventHandler? StopPeriodicRequested;
    private void StopPeriodic(object? sender, RoutedEventArgs e) => StopPeriodicRequested?.Invoke(sender, e);
    public event RoutedEventHandler? OpenRecordsRequested;
    private void OpenRecords(object? sender, RoutedEventArgs e) => OpenRecordsRequested?.Invoke(sender, e);
}

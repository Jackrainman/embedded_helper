using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using EmbeddedHelper.Core;

namespace EmbeddedHelper.App;

public partial class SerialView : UserControl
{
    public SerialView() => InitializeComponent();
    public bool IsFrozen { get; private set; }
    public void RenderLog(SerialTextSnapshot snapshot) => LiveLog.Render(snapshot);
    private void ToggleFreeze(object? sender, RoutedEventArgs e) => ToggleFreeze();
    public void ToggleFreeze()
    {
        if (!IsFrozen && LiveLog.Snapshot is { } snapshot)
        {
            var reading = LiveLog.ReadPosition();
            IsFrozen = true;
            FrozenLog.Heading = "历史快照";
            FrozenColumn.Width = new GridLength(1, GridUnitType.Star);
            FrozenColumn.MinWidth = 120; LogColumns.ColumnDefinitions[2].MinWidth = 120;
            SplitterColumn.Width = new GridLength(8);
            FrozenLog.Visibility = LogSplitter.Visibility = Visibility.Visible;
            FrozenLog.Render(snapshot);
            UpdateLayout();
            FrozenLog.RestorePosition(reading with { Following = false });
            LiveLog.ResumeFollowing();
            FreezeButton.Content = "继续显示";
        }
        else if (IsFrozen)
        {
            var reading = LiveLog.ReadPosition();
            IsFrozen = false;
            FrozenLog.Visibility = LogSplitter.Visibility = Visibility.Collapsed;
            FrozenColumn.MinWidth = 0; LogColumns.ColumnDefinitions[2].MinWidth = 0;
            FrozenColumn.Width = SplitterColumn.Width = new GridLength(0);
            FrozenLog.Release();
            UpdateLayout(); LiveLog.RestorePosition(reading);
            FreezeButton.Content = "冻结当前并分栏";
        }
    }
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

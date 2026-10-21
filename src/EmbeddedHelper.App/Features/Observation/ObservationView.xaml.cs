using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;

namespace EmbeddedHelper.App;

public partial class ObservationView : UserControl
{
    public ObservationView() => InitializeComponent();
    private void IgnoreFrequencyMouseWheel(object sender, MouseWheelEventArgs e) => e.Handled = true;
    private void AddVariables(object sender, RoutedEventArgs e) => ShowAddVariables();
    public void ShowAddVariables()
    {
        if (!Picker.CanEdit) return;
        var picker = new VariablePicker { Variables = Picker.Variables, CanEdit = Picker.CanEdit };
        picker.SetBinding(VariablePicker.CanEditProperty, new System.Windows.Data.Binding(nameof(ObservationViewModel.CanEdit)) { Source = DataContext });
        picker.SetSearchMode(true);
        var panel = new DockPanel { Margin = new Thickness(16) };
        var close = new Button { Content = "完成", HorizontalAlignment = HorizontalAlignment.Right, Margin = new Thickness(0, 8, 0, 0), MinWidth = 80 };
        DockPanel.SetDock(close, Dock.Bottom);
        panel.Children.Add(close);
        panel.Children.Add(picker);
        var dialog = new Window
        {
            Title = "添加变量",
            Owner = Window.GetWindow(this),
            Content = panel,
            Width = 720,
            Height = 560,
            MinWidth = 440,
            MinHeight = 320,
            WindowStartupLocation = WindowStartupLocation.CenterOwner,
            Background = (System.Windows.Media.Brush)FindResource("MainBackground")
        };
        close.Click += (_, _) => dialog.Close();
        dialog.Loaded += (_, _) => picker.SearchBox.Focus();
        try { dialog.ShowDialog(); }
        finally { picker.Variables = null; }
    }
    public event RoutedEventHandler? SaveVariablesRequested;
    public event RoutedEventHandler? ReloadVariablesRequested;
    private void ReloadVariables(object sender, RoutedEventArgs e) => ReloadVariablesRequested?.Invoke(sender, e);
    public event RoutedEventHandler? ExportRecordingRequested;
    private void ExportRecording(object sender, RoutedEventArgs e) => ExportRecordingRequested?.Invoke(sender, e);
    private void SaveVariables(object sender, RoutedEventArgs e) => SaveVariablesRequested?.Invoke(sender, e);
    public event RoutedEventHandler? ConnectionRequested;
    private void Connection(object sender, RoutedEventArgs e) => ConnectionRequested?.Invoke(sender, e);
    public event RoutedEventHandler? ConfigureRequested;
    private void Configure(object? sender, RoutedEventArgs e) => ConfigureRequested?.Invoke(sender, e);
    public event RoutedEventHandler? StartObservationRequested;
    private void StartObservation(object? sender, RoutedEventArgs e) => StartObservationRequested?.Invoke(sender, e);
    public event RoutedEventHandler? StopObservationRequested;
    private void StopObservation(object? sender, RoutedEventArgs e) => StopObservationRequested?.Invoke(sender, e);
    public event RoutedEventHandler? ParseElfRequested;
    private void ParseElf(object? sender, RoutedEventArgs e) => ParseElfRequested?.Invoke(sender, e);
}

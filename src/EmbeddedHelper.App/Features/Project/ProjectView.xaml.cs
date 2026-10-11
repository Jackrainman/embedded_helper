using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;

namespace EmbeddedHelper.App;

public partial class ProjectView : UserControl
{
    public ProjectView() => InitializeComponent();
    public event RoutedEventHandler? BrowseProjectRequested;
    private void BrowseProject(object? sender, RoutedEventArgs e) => BrowseProjectRequested?.Invoke(sender, e);
    public event RoutedEventHandler? OpenProjectRequested;
    private void OpenProject(object? sender, RoutedEventArgs e) => OpenProjectRequested?.Invoke(sender, e);
    public event SelectionChangedEventHandler? TargetChangedRequested;
    private void TargetChanged(object? sender, SelectionChangedEventArgs e) => TargetChangedRequested?.Invoke(sender, e);
    public event RoutedEventHandler? BrowseElfRequested;
    private void BrowseElf(object? sender, RoutedEventArgs e) => BrowseElfRequested?.Invoke(sender, e);
    public event RoutedEventHandler? ParseElfRequested;
    private void ParseElf(object? sender, RoutedEventArgs e) => ParseElfRequested?.Invoke(sender, e);
    public event MouseButtonEventHandler? OpenRecentRequested;
    private void OpenRecent(object? sender, MouseButtonEventArgs e) => OpenRecentRequested?.Invoke(sender, e);
}

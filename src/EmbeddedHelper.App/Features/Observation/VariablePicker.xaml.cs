using System.Collections.ObjectModel;
using System.Collections.Specialized;
using System.ComponentModel;
using System.Windows;
using System.Windows.Controls;

namespace EmbeddedHelper.App;

public partial class VariablePicker : UserControl
{
    public static readonly DependencyProperty VariablesProperty = DependencyProperty.Register(nameof(Variables), typeof(ObservableCollection<VariableRow>), typeof(VariablePicker), new PropertyMetadata(null, SourceChanged));
    public ObservableCollection<VariableRow>? Variables { get => (ObservableCollection<VariableRow>?)GetValue(VariablesProperty); set => SetValue(VariablesProperty, value); }
    public static readonly DependencyProperty CanEditProperty = DependencyProperty.Register(nameof(CanEdit), typeof(bool), typeof(VariablePicker), new PropertyMetadata(true));
    public bool CanEdit { get => (bool)GetValue(CanEditProperty); set => SetValue(CanEditProperty, value); }
    private readonly HashSet<VariableRow> subscribed = [];
    public VariablePicker() { InitializeComponent(); SetSearchMode(false); }
    public void SetSearchMode(bool search)
    {
        SearchBox.Visibility = Hint.Visibility = SearchResults.Visibility = search ? Visibility.Visible : Visibility.Collapsed;
        AddedVariables.Visibility = search ? Visibility.Collapsed : Visibility.Visible;
        Divider.Visibility = Visibility.Collapsed;
        Layout.RowDefinitions[2].Height = search ? new GridLength(1, GridUnitType.Star) : new GridLength(0);
        Layout.RowDefinitions[3].Height = new GridLength(0);
        Layout.RowDefinitions[4].Height = search ? new GridLength(0) : new GridLength(1, GridUnitType.Star);
    }
    private static void SourceChanged(DependencyObject sender, DependencyPropertyChangedEventArgs e)
    {
        var picker = (VariablePicker)sender;
        if (e.OldValue is ObservableCollection<VariableRow> old) old.CollectionChanged -= picker.CollectionChanged;
        if (e.NewValue is ObservableCollection<VariableRow> current) current.CollectionChanged += picker.CollectionChanged;
        picker.Subscribe();
    }
    private void CollectionChanged(object? sender, NotifyCollectionChangedEventArgs e) => Subscribe();
    private void Subscribe()
    {
        foreach (var row in subscribed) row.PropertyChanged -= RowChanged;
        subscribed.Clear();
        foreach (var row in Variables ?? []) { subscribed.Add(row); row.PropertyChanged += RowChanged; }
        Refresh();
    }
    private void RowChanged(object? sender, PropertyChangedEventArgs e) { if (e.PropertyName == nameof(VariableRow.Selected)) Refresh(); }
    private void SearchChanged(object sender, TextChangedEventArgs e) => Refresh();
    private void Refresh()
    {
        if (Results == null) return;
        var query = SearchBox.Text.Trim();
        var rows = Variables?.ToArray() ?? [];
        var matches = query.Length == 0 ? [] : rows.Where(r => !r.Selected && (r.Label.Contains(query, StringComparison.OrdinalIgnoreCase) || r.Variable.Identity.Contains(query, StringComparison.OrdinalIgnoreCase))).ToArray();
        Results.ItemsSource = matches.Take(200).ToArray();
        SelectedRows.ItemsSource = rows.Where(r => r.Selected).ToArray();
        Hint.Text = rows.Length == 0 ? "请先解析 ELF。" : query.Length == 0 ? "输入名称或类型搜索；已添加列表不受搜索影响。修改后请保存。" : $"匹配 {matches.Length} 项{(matches.Length > 200 ? "，仅显示前 200 项，请细化搜索" : "")}；已添加 {rows.Count(r => r.Selected)} 项。修改后请保存。";
    }
    private void Add(object sender, RoutedEventArgs e)
    {
        if (!CanEdit) return;
        if (((FrameworkElement)sender).DataContext is not VariableRow row || !row.Variable.Supported || row.Selected) return;
        if ((Variables?.Count(r => r.Selected) ?? 0) >= 64) { Hint.Text = "最多添加 64 项，请先移除不需要的变量。"; return; }
        row.Selected = true;
    }
    private void Remove(object sender, RoutedEventArgs e) { if (CanEdit && ((FrameworkElement)sender).DataContext is VariableRow row) row.Selected = false; }
}

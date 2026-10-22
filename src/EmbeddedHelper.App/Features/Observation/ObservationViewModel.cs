using System.Collections.ObjectModel;
using System.Collections.Specialized;
using EmbeddedHelper.Core;
using System.ComponentModel;
using System.Runtime.CompilerServices;

namespace EmbeddedHelper.App;

public sealed class ObservationViewModel : INotifyPropertyChanged
{
    private readonly VariableCollection variables = new();
    private readonly HashSet<VariableRow> subscribed = [];
    public ObservationViewModel()
    {
        variables.CollectionChanged += (_, _) =>
        {
            foreach (var row in subscribed) row.PropertyChanged -= SelectionChanged;
            subscribed.Clear();
            foreach (var row in variables) { subscribed.Add(row); row.PropertyChanged += SelectionChanged; }
            VisibleRows = variables.Where(row => row.Selected).ToArray();
        };
    }
    private void SelectionChanged(object? sender, PropertyChangedEventArgs e)
    { if (e.PropertyName == nameof(VariableRow.Selected)) { VisibleRows = variables.Where(row => row.Selected).ToArray(); SelectionEdited?.Invoke(this, EventArgs.Empty); } }
    public event EventHandler? SelectionEdited;
    public ObservableCollection<VariableRow> Variables => variables;
    public void ReplaceRows(VariableRow[] rows) => variables.Replace(rows);
    public void LoadIndex(Variable[] index, string[] selected)
    {
        var identities = selected.ToHashSet(StringComparer.Ordinal);
        var known = index.Select(v => v.Identity).ToHashSet(StringComparer.Ordinal);
        var rows = index.Select(variable => new VariableRow(variable) { Selected = identities.Contains(variable.Identity) })
            .Concat(selected.Where(id => !known.Contains(id)).Distinct().Select(id =>
                new VariableRow(new(id, id, 0, "未解析", 0, "当前索引缺少该变量")) { Selected = true })).ToArray();
        ReplaceRows(rows);
        VisibleRows = Variables.Where(v => v.Selected).ToArray();
    }
    private VariableRow[] visibleRows = [];
    public VariableRow[] VisibleRows
    {
        get => visibleRows;
        set
        {
            Set(ref visibleRows, value); Notify(nameof(HasVariables)); Notify(nameof(VariablesMissing));
            Notify(nameof(CanScaleIndependently)); Notify(nameof(ScaleHint));
            if (!CanScaleIndependently) IndependentScale = false;
        }
    }
    public bool CanScaleIndependently => VisibleRows.Length <= 2;
    public string? ScaleHint => CanScaleIndependently ? null : "仅显示不超过两个参数时可用";
    private bool independentScale = true;
    public bool IndependentScale { get => independentScale; set => Set(ref independentScale, value && CanScaleIndependently); }
    public bool HasVariables => VisibleRows.Length > 0;
    private bool ready, busy, hasSession, canRetry, paused;
    public bool Ready { get => ready; set { if (Set(ref ready, value)) StateChanged(); } }
    public bool Busy { get => busy; set { if (Set(ref busy, value)) StateChanged(); } }
    public bool HasSession { get => hasSession; set { if (Set(ref hasSession, value)) StateChanged(); } }
    public bool CanRetry { get => canRetry; set { if (Set(ref canRetry, value)) StateChanged(); } }
    public bool Paused { get => paused; set => Set(ref paused, value); }
    public bool CanStart => !SettingsError && !Busy && ((!HasSession && Ready) || CanRetry);
    private bool settingsError;
    public bool SettingsError { get => settingsError; set { Set(ref settingsError, value); Notify(nameof(CanStart)); Notify(nameof(NeedsConfiguration)); } }
    private string settingsStatus = "变量选择自动保存；频率编辑完成后自动保存。";
    public string SettingsStatus { get => settingsStatus; set => Set(ref settingsStatus, value); }
    public bool CanStop => HasSession;
    public bool CanEdit => !Busy && !HasSession;
    private bool hasRecording, exporting;
    public bool HasRecording { get => hasRecording; set { Set(ref hasRecording, value); Notify(nameof(CanExport)); } }
    public bool Exporting { get => exporting; set { Set(ref exporting, value); Notify(nameof(CanExport)); } }
    public bool CanExport => HasRecording && !HasSession && !Busy && !Exporting;
    private string exportStatus = "停止采集后可导出本次完整记录。";
    public string ExportStatus { get => exportStatus; set => Set(ref exportStatus, value); }
    public string StartText => CanRetry ? "清理并重试" : "开始采集";
    public bool NeedsConfiguration => !Ready && !HasSession && !Busy && !SettingsError;
    private string summary = "读取本工程已保存的变量与共享连接配置。", issue = "请打开工程，保存共享连接并搜索添加变量。", configureText = "设置观察变量";
    private string status = "未连接 · ELF 与固件匹配未核验", variablesEmpty = "尚无已配置变量\n请搜索添加变量。", plotEmpty = "开始采集后显示曲线";
    private double[][] series = [];
    private double[][] times = [];
    public double[][] Times { get => times; set => Set(ref times, value); }
    private string[] plotLabels = [];
    public string[] PlotLabels { get => plotLabels; set { Set(ref plotLabels, value); Notify(nameof(PlotNames)); } }
    public string PlotNames => string.Join("\n", PlotLabels);
    public string Summary { get => summary; set => Set(ref summary, value); }
    public string Issue { get => issue; set => Set(ref issue, value); }
    public string ConfigureText { get => configureText; set => Set(ref configureText, value); }
    public string Status { get => status; set => Set(ref status, value); }
    public string VariablesEmpty { get => variablesEmpty; set => Set(ref variablesEmpty, value); }
    public string PlotEmpty { get => plotEmpty; set => Set(ref plotEmpty, value); }
    public double[][] Series { get => series; set { Set(ref series, value); Notify(nameof(HasPlot)); Notify(nameof(PlotMissing)); } }
    public bool VariablesMissing => !HasVariables;
    public bool PlotMissing => !HasPlot;
    public bool HasPlot => Series.Any(s => s.Length > 1);
    private string elfStatus = "尚未解析 ELF", elfDetails = "";
    public string ElfStatus { get => elfStatus; set => Set(ref elfStatus, value); }
    public string ElfDetails { get => elfDetails; set { Set(ref elfDetails, value); Notify(nameof(HasElfDetails)); } }
    public bool HasElfDetails => ElfDetails.Length > 0;
    public event PropertyChangedEventHandler? PropertyChanged;
    private bool Set<T>(ref T field, T value, [CallerMemberName] string? name = null)
    { if (EqualityComparer<T>.Default.Equals(field, value)) return false; field = value; Notify(name); return true; }
    private void Notify([CallerMemberName] string? name = null) => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
    private void StateChanged()
    {
        Notify(nameof(CanStart)); Notify(nameof(CanStop)); Notify(nameof(CanEdit)); Notify(nameof(StartText)); Notify(nameof(NeedsConfiguration)); Notify(nameof(CanExport));
    }
}


// A large structure index is published once, rather than rebuilding the picker for every member.
internal sealed class VariableCollection : ObservableCollection<VariableRow>
{
    public void Replace(VariableRow[] rows)
    {
        CheckReentrancy();
        Items.Clear();
        foreach (var row in rows) Items.Add(row);
        OnPropertyChanged(new PropertyChangedEventArgs(nameof(Count)));
        OnPropertyChanged(new PropertyChangedEventArgs("Item[]"));
        OnCollectionChanged(new NotifyCollectionChangedEventArgs(NotifyCollectionChangedAction.Reset));
    }
}

using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Runtime.CompilerServices;

namespace EmbeddedHelper.App;

public sealed class ObservationViewModel : INotifyPropertyChanged
{
    public ObservableCollection<VariableRow> Variables { get; } = new();
    private VariableRow[] visibleRows = [];
    public VariableRow[] VisibleRows { get => visibleRows; set { Set(ref visibleRows, value); Notify(nameof(HasVariables)); Notify(nameof(VariablesMissing)); } }
    public bool HasVariables => VisibleRows.Length > 0;
    private bool ready, busy, hasSession, canRetry, paused;
    public bool Ready { get => ready; set { if (Set(ref ready, value)) StateChanged(); } }
    public bool Busy { get => busy; set { if (Set(ref busy, value)) StateChanged(); } }
    public bool HasSession { get => hasSession; set { if (Set(ref hasSession, value)) StateChanged(); } }
    public bool CanRetry { get => canRetry; set { if (Set(ref canRetry, value)) StateChanged(); } }
    public bool Paused { get => paused; set => Set(ref paused, value); }
    public bool CanStart => !Busy && ((!HasSession && Ready) || CanRetry);
    public bool CanStop => HasSession;
    public bool CanEdit => !Busy && !HasSession;
    public string StartText => CanRetry ? "清理并重试" : "开始采集";
    public bool NeedsConfiguration => !Ready && !HasSession && !Busy;
    private string summary = "读取本工程已保存的观察设置。", issue = "请打开工程，并在观察设置中保存连接方式、变量或 RTT。", configureText = "去观察设置";
    private string status = "未连接 · ELF 与固件匹配未核验", variablesEmpty = "尚无已配置变量\n请先在工程的观察设置中勾选并保存。", plotEmpty = "开始采集后显示曲线", rttEmpty = "等待本地 RTT 输出", rttText = "";
    private double[][] series = [];
    private string[] plotLabels = [];
    public string[] PlotLabels { get => plotLabels; set => Set(ref plotLabels, value); }
    public string Summary { get => summary; set => Set(ref summary, value); }
    public string Issue { get => issue; set => Set(ref issue, value); }
    public string ConfigureText { get => configureText; set => Set(ref configureText, value); }
    public string Status { get => status; set => Set(ref status, value); }
    public string VariablesEmpty { get => variablesEmpty; set => Set(ref variablesEmpty, value); }
    public string PlotEmpty { get => plotEmpty; set => Set(ref plotEmpty, value); }
    public string RttEmpty { get => rttEmpty; set => Set(ref rttEmpty, value); }
    public string RttText { get => rttText; set { Set(ref rttText, value); Notify(nameof(HasRtt)); Notify(nameof(RttMissing)); } }
    public bool HasRtt => RttText.Length > 0;
    public double[][] Series { get => series; set { Set(ref series, value); Notify(nameof(HasPlot)); Notify(nameof(PlotMissing)); } }
    public bool VariablesMissing => !HasVariables;
    public bool RttMissing => !HasRtt;
    public bool PlotMissing => !HasPlot;
    public bool HasPlot => Series.Any(s => s.Length > 1);
    public event PropertyChangedEventHandler? PropertyChanged;
    private bool Set<T>(ref T field, T value, [CallerMemberName] string? name = null)
    { if (EqualityComparer<T>.Default.Equals(field, value)) return false; field = value; Notify(name); return true; }
    private void Notify([CallerMemberName] string? name = null) => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
    private void StateChanged()
    {
        Notify(nameof(CanStart)); Notify(nameof(CanStop)); Notify(nameof(CanEdit)); Notify(nameof(StartText)); Notify(nameof(NeedsConfiguration));
    }
}

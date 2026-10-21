using System.ComponentModel;
using System.Runtime.CompilerServices;
using EmbeddedHelper.Core;

namespace EmbeddedHelper.App;

public sealed class VariableRow(Variable variable) : INotifyPropertyChanged
{
    public Variable Variable { get; } = variable;
    public string Label => Variable.Name + " / " + Variable.Type;
    public string Detail => $"{Variable.Name}\n{Variable.Type} · {Address}\n{Variable.Status}";
    public string Address => Variable.Size == 0 ? "未解析" : Variable.AddressText;
    private bool selected;
    public bool Selected { get => selected; set { if (selected == value) return; selected = value; Changed(); } }
    public Queue<double> History { get; } = new();
    public Queue<double> HistorySeconds { get; } = new();
    public string? LatestValue { get; set; }
    public DateTimeOffset? LastObservedAt { get; set; }
    private string value = variable.Supported ? "未采集" : variable.Status;
    public string Value { get => value; set { if (this.value == value) return; this.value = value; Changed(); } }
    public event PropertyChangedEventHandler? PropertyChanged;
    private void Changed([CallerMemberName] string? name = null) => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
}

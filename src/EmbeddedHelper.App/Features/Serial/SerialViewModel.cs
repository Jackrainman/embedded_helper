using System.ComponentModel;
using System.Windows.Media;

namespace EmbeddedHelper.App;

public sealed class SerialViewModel : INotifyPropertyChanged
{
    public bool CanToggle { get; private set; } = true;
    public bool CanEdit { get; private set; } = true;
    public string ToggleText { get; private set; } = "打开串口";
    public string ConnectionText { get; private set; } = "未连接";
    public Brush ConnectionBrush { get; private set; } = Brushes.Firebrick;
    public bool CanStartPeriodic { get; private set; }
    public bool CanStopPeriodic { get; private set; }
    public void Update(bool opening, bool closing, bool hasSession, bool connected, bool periodic, bool shuttingDown)
    {
        var busy = opening || closing || shuttingDown;
        var toggle = opening ? "打开中…" : closing ? "关闭中…" : hasSession ? "关闭串口" : "打开串口";
        var text = opening ? "正在打开" : closing ? "正在关闭" : connected ? "已连接" : hasSession ? "连接故障" : "未连接";
        var brush = !busy && connected ? Brushes.ForestGreen : Brushes.Firebrick;
        var edit = !hasSession && !busy;
        var start = connected && !busy && !periodic;
        if (CanToggle == !busy && CanEdit == edit && ToggleText == toggle && ConnectionText == text && ConnectionBrush == brush && CanStartPeriodic == start && CanStopPeriodic == periodic) return;
        CanToggle = !busy; CanEdit = edit; ToggleText = toggle; ConnectionText = text; ConnectionBrush = brush; CanStartPeriodic = start; CanStopPeriodic = periodic;
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(""));
    }
    public event PropertyChangedEventHandler? PropertyChanged;
}

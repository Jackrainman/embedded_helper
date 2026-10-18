using System.ComponentModel;
using System.Windows.Media;
using EmbeddedHelper.Core;

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
    public string PeriodicText { get; private set; } = "周期发送未启动";
    public void UpdatePeriodic(PeriodicSendStatus status)
    {
        var text = status.State switch
        {
            PeriodicSendState.Running => $"周期发送中 · {status.Milliseconds} ms · 驱动已接受 {status.Count} 次",
            PeriodicSendState.Stopping => $"正在停止周期发送 · 等待当前发送结束 · {status.Count} 次",
            PeriodicSendState.Stopped => $"周期发送已停止 · 驱动已接受 {status.Count} 次",
            PeriodicSendState.Faulted => "周期发送故障：" + status.Error,
            _ => "周期发送未启动"
        };
        if (PeriodicText == text) return;
        PeriodicText = text; PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(PeriodicText)));
    }
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

using System.ComponentModel;
using System.Runtime.CompilerServices;
using EmbeddedHelper.Core;

namespace EmbeddedHelper.App;

public sealed class RemoteViewModel : INotifyPropertyChanged
{
    private string host = "", port = "19011", web = "", gdbPort = "2331", preview = "";
    private bool eideUpload, busy;
    private ToolRelease? selectedRelease;
    public ToolRelease? SelectedRelease { get => selectedRelease; set { if (Set(ref selectedRelease, value)) Invalidate(); } }
    private string toolStatus = "请打开工程，选择要接入的工具版本。";
    public string ToolStatus { get => toolStatus; set => Set(ref toolStatus, value); }
    public string Host { get => host; set { if (Set(ref host, value)) Invalidate(); } }
    public string Port { get => port; set { if (Set(ref port, value)) Invalidate(); } }
    public string Web { get => web; set { if (Set(ref web, value)) Invalidate(); } }
    public string GdbPort { get => gdbPort; set { if (Set(ref gdbPort, value)) Invalidate(); } }
    public bool EideUpload { get => eideUpload; set { if (Set(ref eideUpload, value)) Invalidate(); } }
    public bool Busy { get => busy; set { Set(ref busy, value); Notify(nameof(CanApply)); Notify(nameof(CanEdit)); } }
    public bool CanEdit => !Busy;
    public ConfigPlan? Plan { get; private set; }
    public bool CanApply => !Busy && Plan?.Changes.Length > 0;
    public string Preview { get => preview; set => Set(ref preview, value); }
    public void ShowPlan(ConfigPlan plan)
    {
        Plan = plan;
        Preview = plan.Changes.Length == 0 ? "无需修改；当前配置已一致。" : string.Join("\n\n", plan.Changes.Select(c => c.Path + "\n原内容：\n" + (c.Before ?? "（不存在）") + "\n修改后：\n" + c.After));
        Notify(nameof(CanApply));
    }
    public void Invalidate()
    {
        if (Plan != null) Preview = "输入或工程已变化，请重新预览。";
        Plan = null; Notify(nameof(CanApply));
    }
    public event PropertyChangedEventHandler? PropertyChanged;
    private void Notify([CallerMemberName] string? name = null) => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
    private bool Set<T>(ref T field, T value, [CallerMemberName] string? name = null)
    { if (EqualityComparer<T>.Default.Equals(field, value)) return false; field = value; Notify(name); return true; }
}

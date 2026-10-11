using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.IO.Ports;
using System.Net.Http;
using System.Runtime.CompilerServices;
using System.Text;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Shapes;
using System.Windows.Threading;
using EmbeddedHelper.Core;
using Microsoft.Win32;

namespace EmbeddedHelper.App;

public sealed class VariableRow(Variable variable) : INotifyPropertyChanged
{
    public Variable Variable { get; } = variable;
    public string Label => Variable.Name + " / " + Variable.Type;
    public string Address => Variable.AddressText;
    public bool Selected { get; set; }
    public Queue<double> History { get; } = new();
    public string? LatestValue { get; set; }
    public DateTimeOffset? LastObservedAt { get; set; }
    private string value = variable.Supported ? "未采集" : variable.Status;
    public string Value { get => value; set { this.value = value; Changed(); } }
    public event PropertyChangedEventHandler? PropertyChanged;
    private void Changed([CallerMemberName] string? name = null) => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
}
public sealed record AppSettings(string Dll = "", string Probe = "", string[]? Recent = null, double Width = 1280, double Height = 860, SerialSettings? Serial = null, string ReadElf = "", string SerialEncoding = "utf-8", string[]? SendHistory = null);
public sealed record WatchSettings(string Target, string[] Identities, int Rate);

public partial class MainWindow : Window
{
    public ObservableCollection<VariableRow> Variables { get; } = new();
    private ProjectInfo? project;
    private ConfigPlan? plan;
    private AppSettings settings = new();
    private ObservationSession? observation;
    private SerialSession? serial;
    private CancellationTokenSource? periodic;
    private Task? periodicTask;
    private readonly DispatcherTimer refresh = new() { Interval = TimeSpan.FromMilliseconds(100) };
    private readonly Decoder rttDecoder = Encoding.UTF8.GetDecoder();
    private readonly StringBuilder rttHistory = new();
    private readonly SerialTextBuffer serialText = new();
    private readonly Stopwatch serialClock = new();
    private long lastReceived, lastSent;
    private double lastSerialTime;
    private bool serialOpening, serialClosing;
    private bool toolBusy;
    private DateTime elfStamp;
    private bool closing, transition;
    private long samples;
    private readonly Stopwatch sampling = new();
    private BuildTarget? Target => TargetBox.SelectedItem as BuildTarget;

    public MainWindow()
    {
        InitializeComponent();
        DataContext = this;
        VersionLabel.Text = "v" + Product.Version;
        Title = "Embedded Helper " + Product.Version;
        ParityBox.ItemsSource = Enum.GetValues<Parity>(); ParityBox.SelectedItem = Parity.None;
        StopBitsBox.ItemsSource = new[] { StopBits.One, StopBits.Two, StopBits.OnePointFive }; StopBitsBox.SelectedItem = StopBits.One;
        FlowBox.ItemsSource = Enum.GetValues<Handshake>(); FlowBox.SelectedItem = Handshake.None;
        try { settings = App.Data.Load("settings/window.json", () => new AppSettings()); }
        catch (Exception ex) { Report("设置读取失败，保留原文件并使用默认值：" + ex.Message); }
        DllPath.Text = settings.Dll; ProbeSerial.Text = settings.Probe;
        EncodingBox.ItemsSource = new[] { "utf-8", "gb18030", "us-ascii", "utf-16" };
        EncodingBox.SelectedItem = settings.SerialEncoding;
        if (EncodingBox.SelectedIndex < 0) EncodingBox.SelectedIndex = 0;
        SendHistoryBox.ItemsSource = settings.SendHistory ?? [];
        try { ToolCatalog.Load(App.Data.Root); } catch (Exception ex) { Report("工具目录记录无法读取：" + ex.Message); }
        ToolVersionBox.ItemsSource = ToolRelease.Available; ToolVersionBox.SelectedIndex = 0;
        ReadElfPath.Text = string.IsNullOrEmpty(settings.ReadElf) ? ReadElfIndex.FindTool() ?? "" : settings.ReadElf;
        Width = Math.Clamp(settings.Width, MinWidth, Math.Max(MinWidth, SystemParameters.WorkArea.Width));
        Height = Math.Clamp(settings.Height, MinHeight, Math.Max(MinHeight, SystemParameters.WorkArea.Height));
        RecentProjects.ItemsSource = settings.Recent ?? [];
        if (settings.Serial is { } saved)
        {
            SerialPortBox.Text = saved.Port; BaudBox.Text = saved.Baud.ToString(CultureInfo.InvariantCulture);
            DataBitsBox.SelectedIndex = Math.Clamp(saved.DataBits - 5, 0, 3); ParityBox.SelectedItem = saved.Parity;
            StopBitsBox.SelectedItem = saved.StopBits; FlowBox.SelectedItem = saved.FlowControl;
        }
        RefreshPorts(null, null!);
        EnvironmentCheck(null, null!);
        refresh.Tick += Refresh;
        refresh.Start();
    }

    private void Report(string text) { StatusLabel.Text = text; try { App.Data.Log(text); } catch (IOException) { } }
    private async Task Guard(Func<Task> action)
    {
        try { await action(); } catch (Exception ex) { Report("操作未完成：" + ex.Message); }
    }
    private void BrowseProject(object? sender, RoutedEventArgs e)
    {
        var dialog = new OpenFolderDialog { Title = "选择 EIDE 工程目录" };
        if (dialog.ShowDialog(this) == true) ProjectPath.Text = dialog.FolderName;
    }
    private async void OpenProject(object? sender, RoutedEventArgs e) => await Guard(() => LoadProjectAsync(ProjectPath.Text));
    private async void OpenRecent(object? sender, System.Windows.Input.MouseButtonEventArgs e)
    { if (RecentProjects.SelectedItem is string path) { ProjectPath.Text = path; await Guard(() => LoadProjectAsync(path)); } }
    private async Task LoadProjectAsync(string path)
    {
        if (observation != null || transition) throw new InvalidOperationException("请先停止采集再切换工程。");
        var loaded = await Task.Run(() => ProjectReader.Read(path));
        project = loaded;
        ProjectPath.Text = loaded.Root;
        ProjectSummary.Text = loaded.Name + " · " + loaded.Family;
        McuLabel.Text = "MCU：" + loaded.Mcu + "\n来源：.eide/eide.yml";
        ProjectDiagnostics.Text = string.Join("\n", loaded.Diagnostics.Prepend("读取完成；未运行工程钩子、构建或设备命令。"));
        TargetBox.ItemsSource = loaded.Targets;
        TargetBox.SelectedIndex = loaded.Targets.Length == 1 ? 0 : -1;
        var statePath = System.IO.Path.Combine(loaded.Root, ".embedded_helper", "watch.json");
        if (File.Exists(statePath))
        {
            var watch = JsonSerializer.Deserialize<WatchSettings>(await File.ReadAllTextAsync(statePath), Product.Json);
            TargetBox.SelectedItem = loaded.Targets.FirstOrDefault(t => t.Name == watch?.Target);
        }
        RemoteHost.Text = ""; RemotePort.Text = "19011"; WebAddress.Text = "";
        var integration = VsCodeIntegration.ReadState(loaded.Root);
        GdbPort.Text = (integration?.GdbPort ?? 2331).ToString(CultureInfo.InvariantCulture);
        UseEideUpload.IsChecked = integration?.EideFields?.Length > 0;
        if (File.Exists(System.IO.Path.Combine(loaded.Root, ".embedded_helper", "tools.lock.json")))
            ToolVersionBox.SelectedItem = ToolCache.ReadLock(loaded.Root);
        if (File.Exists(loaded.RemotePath))
        {
            using var remote = JsonDocument.Parse(await File.ReadAllTextAsync(loaded.RemotePath), ProjectReader.JsonOptions);
            RemoteHost.Text = remote.RootElement.GetProperty("ServerName").GetString();
            RemotePort.Text = remote.RootElement.GetProperty("Port").GetInt32().ToString(CultureInfo.InvariantCulture);
            WebAddress.Text = remote.RootElement.TryGetProperty("WebUrl", out var web) ? web.GetString() ?? "" : "";
        }
        settings = settings with { Recent = new[] { loaded.Root }.Concat(settings.Recent ?? []).Distinct(StringComparer.OrdinalIgnoreCase).Take(8).ToArray() };
        RecentProjects.ItemsSource = settings.Recent;
        PersistSettings();
        plan = null; ApplyConfigButton.IsEnabled = false;
        Report("已打开 " + loaded.Name + "。请选择 ELF 解析变量，或维护远程配置。");
        if (ConfigTransaction.Pending(JournalFor(loaded.Root)).Length > 0) Report("该工程有未完成写入，请到远程配置页恢复；外部修改不会被覆盖。");
    }
    private void TargetChanged(object? sender, SelectionChangedEventArgs e)
    {
        if (Target is not { } target) { ElfPath.Text = ""; return; }
        ElfPath.Text = target.ElfPath;
        Variables.Clear();
        plan = null; ApplyConfigButton.IsEnabled = false;
    }
    private async void BrowseElf(object? sender, RoutedEventArgs e)
    {
        if (observation != null || transition) { Report("请先停止采集或等待当前解析结束再更换 ELF。"); return; }
        var dialog = new OpenFileDialog { Filter = "固件 ELF|*.elf;*.axf|所有文件|*.*" };
        if (dialog.ShowDialog(this) == true) { ElfPath.Text = dialog.FileName; await Guard(ParseElfAsync); }
    }
    private async void ParseElf(object? sender, RoutedEventArgs e) => await Guard(ParseElfAsync);
    private async Task ParseElfAsync()
    {
        if (transition) throw new InvalidOperationException("正在切换连接或读取计划，请稍候。");
        transition = true; TargetBox.IsEnabled = false;
        try
        {
            var path = ElfPath.Text;
            var tool = ReadElfPath.Text;
            var indexed = await Task.Run(() =>
            {
                static string Digest(string file) { using var stream = File.OpenRead(file); return Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(stream)); }
                var before = Digest(path);
                var result = ElfIndex.Read(path, tool);
                if (before != Digest(path)) throw new IOException("解析期间 ELF 发生变化，请等待编译完成后重试。");
                return result;
            });
            var selected = Variables.Where(v => v.Selected).Select(v => v.Variable.Identity).ToHashSet();
            if (selected.Count == 0 && project != null)
            {
                var state = System.IO.Path.Combine(project.Root, ".embedded_helper", "watch.json");
                if (File.Exists(state))
                {
                    var watch = JsonSerializer.Deserialize<WatchSettings>(await File.ReadAllTextAsync(state), Product.Json);
                    if (watch != null && watch.Target == Target?.Name) selected.UnionWith(watch.Identities);
                }
            }
            var missing = Variables.Where(v => v.Selected && !indexed.Any(n => n.Identity == v.Variable.Identity)).Select(v => v.Variable with { Status = "新 ELF 中缺失，地址未使用" }).ToArray();
            var rows = indexed.Concat(missing).Select(v => new VariableRow(v) { Selected = selected.Contains(v.Identity) }).ToArray();
            if (observation != null)
            {
                await observation.UpdateAsync(rows.Where(v => v.Selected && v.Variable.Supported).Select(v => v.Variable).ToArray(), int.Parse(SampleRate.Text, CultureInfo.InvariantCulture));
                samples = 0; sampling.Restart(); rttDecoder.Reset(); rttHistory.Clear();
            }
            Variables.Clear();
            foreach (var row in rows) Variables.Add(row);
            elfStamp = File.GetLastWriteTimeUtc(path);
            ObservationStatus.Text = $"ELF 已解析：{indexed.Length} 项，{indexed.Count(v => v.Supported)} 项可读取 · 与目标固件匹配未核验";
            Report(observation == null ? "变量索引完成；地址来自 ELF，尚未连接硬件。" : $"读计划第 {observation.Generation} 代已在批次边界应用；新曲线重新开始，缺失项标为无效。固件与 ELF 仍未核验。");
            if (project != null && Target is { } target) PortableData.SaveFile(System.IO.Path.Combine(project.Root, ".embedded_helper", "watch.json"), new WatchSettings(target.Name, selected.ToArray(), int.Parse(SampleRate.Text, CultureInfo.InvariantCulture)));
        }
        finally { transition = false; TargetBox.IsEnabled = observation == null; }
    }
    private async void StartObservation(object? sender, RoutedEventArgs e) => await Guard(async () =>
    {
        if (transition) throw new InvalidOperationException("正在切换连接，请稍候。");
        if (observation?.CanRetry == true) await StopObservationAsync();
        if (observation != null) throw new InvalidOperationException("请先停止已有采集。");
        if (Target is not { } target) throw new InvalidOperationException("请先选择工程目标。");
        var variables = Variables.Where(v => v.Selected).Select(v => v.Variable).ToArray();
        if (variables.Length == 0 && EnableRtt.IsChecked != true) throw new InvalidOperationException("请选择变量或启用 RTT。");
        if (variables.Any(v => !v.Supported)) throw new InvalidOperationException("观察列表包含不支持的变量类型。");
        transition = true; StartButton.IsEnabled = false; TargetBox.IsEnabled = false; EnableConnectionFields(false);
        var candidate = new ObservationSession();
        observation = candidate;
        try
        {
            var remote = TransportBox.SelectedIndex == 1;
            var request = new ObservationRequest(DllPath.Text, int.TryParse(ProbeSerial.Text, out var number) ? number : 0,
                remote ? RemoteHost.Text : "", int.Parse(RemotePort.Text, CultureInfo.InvariantCulture), target.Device, target.Interface, target.SpeedKHz,
                int.Parse(SampleRate.Text, CultureInfo.InvariantCulture), EnableRtt.IsChecked == true && !remote, variables);
            await candidate.StartAsync(System.IO.Path.Combine(AppContext.BaseDirectory, "EmbeddedHelper.Worker.exe"), request);
            samples = 0; sampling.Restart(); rttDecoder.Reset(); rttHistory.Clear();
            foreach (var row in Variables) { row.History.Clear(); row.LatestValue = null; row.LastObservedAt = null; row.Value = row.Variable.Supported ? "等待新会话数据" : row.Variable.Status; }
            if (project != null) PortableData.SaveFile(System.IO.Path.Combine(project.Root, ".embedded_helper", "watch.json"), new WatchSettings(target.Name, variables.Select(v => v.Identity).ToArray(), request.SampleRate));
            PersistSettings();
        }
        catch { await candidate.DisposeAsync(); observation = null; throw; }
        finally { transition = false; StartButton.IsEnabled = observation == null; TargetBox.IsEnabled = observation == null; EnableConnectionFields(observation == null); }
    });
    private async void StopObservation(object? sender, RoutedEventArgs e) => await Guard(StopObservationAsync);
    private async Task StopObservationAsync()
    {
        if (transition) return;
        transition = true;
        try
        {
            if (observation != null) await observation.DisposeAsync();
            observation = null; sampling.Stop();
            foreach (var row in Variables.Where(v => v.Selected)) row.Value = "已停止 / 过期";
            ObservationStatus.Text = "已释放 · 可使用外部烧录/调试工具";
            StartButton.IsEnabled = true; TargetBox.IsEnabled = true; EnableConnectionFields(true);
        }
        finally { transition = false; }
    }
    private void EnableConnectionFields(bool enabled)
    {
        foreach (var field in new UIElement[] { TransportBox, ProbeSerial, DllPath, RemoteHost, RemotePort, EnableRtt }) field.IsEnabled = enabled;
    }
    private void Refresh(object? sender, EventArgs e)
    {
        if (observation != null)
        {
            while (observation.Events.TryRead(out var item))
            {
                if (item.SessionId != observation.SessionId || item.Generation != observation.Generation) continue;
                if (item.Kind != "sample") { if (item.Message.Length > 0) Report(item.Message); continue; }
                samples++;
                if (item.Values != null) foreach (var row in Variables)
                    if (item.Values.TryGetValue(row.Variable.Identity, out var value))
                    {
                        row.LatestValue = value; row.LastObservedAt = item.ObservedAt;
                        if (double.TryParse(value, CultureInfo.InvariantCulture, out var number) && double.IsFinite(number))
                        { row.History.Enqueue(number); if (row.History.Count > 2000) row.History.Dequeue(); }
                    }
                if (item.RttBase64 != null)
                {
                    rttHistory.Append(Decode(rttDecoder, Convert.FromBase64String(item.RttBase64)));
                    if (rttHistory.Length > 65536) rttHistory.Remove(0, rttHistory.Length - 65536);
                }
            }
            ObservationStatus.Text = $"{observation.State} · 第 {observation.Generation} 代 · 接收批次 {samples / Math.Max(1, sampling.Elapsed.TotalSeconds):F1} Hz · 显示缺口 {observation.Dropped}";
            StartButton.IsEnabled = !transition && observation.CanRetry;
            StartButton.Content = observation.CanRetry ? "清理并重试" : "开始采集";
            if (PauseObservation.IsChecked != true)
            {
                foreach (var row in Variables.Where(v => v.LatestValue != null)) row.Value = row.LatestValue + (observation.CanRetry || DateTimeOffset.UtcNow - row.LastObservedAt > TimeSpan.FromSeconds(3) ? " / 过期" : "");
                RttText.Text = rttHistory.ToString(); RttText.ScrollToEnd(); DrawPlot();
            }
        }
        if (!string.IsNullOrEmpty(ElfPath.Text) && elfStamp != default && File.GetLastWriteTimeUtc(ElfPath.Text) != elfStamp)
            ObservationStatus.Text += " · ELF 已变化，当前地址未替换";
        if (serial != null)
        {
            var changed = false;
            while (serial.Display.TryRead(out var block))
            {
                serialText.Append(block); changed = true;
            }
            if (PauseSerial.IsChecked != true && (changed || SerialText.Text != serialText.Text)) { SerialText.Text = serialText.Text; SerialText.ScrollToEnd(); }
            var elapsed = serialClock.Elapsed.TotalSeconds;
            var delta = Math.Max(0.001, elapsed - lastSerialTime);
            SerialStats.Text = $"{(serialOpening ? "打开中" : serialClosing ? "关闭中" : serial.Connected ? "已连接" : "连接故障")} · RX {serial.Received:N0} B / {(serial.Received - lastReceived) / delta:N0} B/s · TX 驱动已接受 {serial.Sent:N0} B / {(serial.Sent - lastSent) / delta:N0} B/s\n显示队列丢弃 {serial.Clipped:N0} B · 历史裁剪 {serialText.TrimmedCharacters:N0} 字符 · 记录积压 {serial.Recorder?.QueuedBytes ?? 0:N0} B · 记录缺口 {serial.Recorder?.Discarded ?? 0} 块 / {serial.Recorder?.DiscardedBytes ?? 0:N0} B · 驱动错误通知 {serial.DriverErrors} · {serial.LastDriverError} · {serial.Error ?? serial.Recorder?.Error}";
            lastReceived = serial.Received; lastSent = serial.Sent; lastSerialTime = elapsed;
        }
    }
    private static string Decode(Decoder decoder, byte[] data)
    { var chars = new char[Encoding.UTF8.GetMaxCharCount(data.Length)]; var count = decoder.GetChars(data, chars, false); return new string(chars, 0, count); }
    private static void AppendBounded(TextBox box, string text)
    { if (box.Text.Length + text.Length > 65536) box.Text = box.Text[Math.Min(box.Text.Length, 32768)..]; box.AppendText(text); box.ScrollToEnd(); }
    private void DrawPlot()
    {
        Plot.Children.Clear();
        var width = Plot.ActualWidth; var height = Plot.ActualHeight;
        if (width < 5 || height < 5) return;
        var rows = Variables.Where(v => v.Selected && v.History.Count > 1).Take(16).ToArray();
        var all = rows.SelectMany(r => r.History).ToArray();
        if (all.Length == 0) return;
        var min = all.Min(); var range = Math.Max(0.001, all.Max() - min);
        Brush[] colors = [Brushes.DeepSkyBlue, Brushes.MediumAquamarine, Brushes.Gold, Brushes.Orchid];
        for (int i = 0; i < rows.Length; i++)
        {
            var values = rows[i].History.ToArray();
            var line = new Polyline { Stroke = colors[i % colors.Length], StrokeThickness = 1.5 };
            for (var n = 0; n < values.Length; n++) line.Points.Add(new Point(n * width / Math.Max(1, values.Length - 1), height - 8 - (values[n] - min) / range * (height - 16)));
            Plot.Children.Add(line);
        }
    }
    private void PlotSizeChanged(object? sender, SizeChangedEventArgs e) => DrawPlot();
    private void ClearRtt(object? sender, RoutedEventArgs e) { rttHistory.Clear(); RttText.Clear(); }
    private void PreviewConfig(object? sender, RoutedEventArgs e)
    {
        try
        {
            plan = RemoteConfiguration.Plan(project ?? throw new InvalidOperationException("请先打开工程。"), Target ?? throw new InvalidOperationException("请选择目标。"), RemoteHost.Text, int.Parse(RemotePort.Text, CultureInfo.InvariantCulture), WebAddress.Text);
            ConfigPreview.Text = string.Join("\n\n", plan.Changes.Select(c => c.Path + "\n" + c.After));
            ApplyConfigButton.IsEnabled = true;
        }
        catch (Exception ex) { plan = null; ApplyConfigButton.IsEnabled = false; Report(ex.Message); }
    }
    private async void ApplyConfig(object? sender, RoutedEventArgs e) => await Guard(ApplyConfigAsync);
    private async Task ApplyConfigAsync()
    {
        if (observation != null || transition) throw new InvalidOperationException("请先停止采集再修改连接配置。");
        if (plan == null) throw new InvalidOperationException("请先预览修改。");
        var ready = plan;
        var journal = JournalFor(ready.Root);
        await Task.Run(() => ConfigTransaction.Apply(ready, journal));
        plan = null; ApplyConfigButton.IsEnabled = false; Report("已应用预览中的配置。保留本地条目；未执行构建、烧录或调试。VS Code 入口需先在设置页绑定。");
    }
    private static string JournalFor(string root) => System.IO.Path.Combine(App.Data.Root, "projects", ConfigTransaction.Hash(root), "transactions");
    private void PreviewIntegration(object? sender, RoutedEventArgs e)
    {
        try
        {
            var loaded = project ?? throw new InvalidOperationException("请先打开工程。");
            var release = ToolVersionBox.SelectedItem as ToolRelease ?? throw new InvalidOperationException("请在设置页选择工具版本。");
            new ToolCache(App.Data.Root).Resolve(release);
            ShowPlan(VsCodeIntegration.Plan(loaded, Target ?? throw new InvalidOperationException("请选择目标。"), RemoteHost.Text, int.Parse(RemotePort.Text, CultureInfo.InvariantCulture), int.Parse(GdbPort.Text, CultureInfo.InvariantCulture), release, UseEideUpload.IsChecked == true, WebAddress.Text));
        }
        catch (Exception ex) { plan = null; ApplyConfigButton.IsEnabled = false; Report(ex.Message); }
    }
    private void PreviewRemoveIntegration(object? sender, RoutedEventArgs e)
    {
        try { ShowPlan(VsCodeIntegration.Remove(project?.Root ?? throw new InvalidOperationException("请先打开工程。"))); }
        catch (Exception ex) { plan = null; ApplyConfigButton.IsEnabled = false; Report(ex.Message); }
    }
    private void ShowPlan(ConfigPlan candidate)
    {
        plan = candidate;
        ConfigPreview.Text = candidate.Changes.Length == 0 ? "无需修改；当前配置已一致。" : string.Join("\n\n", candidate.Changes.Select(c => c.Path + "\n原内容：\n" + (c.Before ?? "（不存在）") + "\n修改后：\n" + c.After));
        ApplyConfigButton.IsEnabled = candidate.Changes.Length > 0;
    }
    private async void RecoverConfig(object? sender, RoutedEventArgs e) => await Guard(async () =>
    {
        var root = project?.Root ?? throw new InvalidOperationException("请先打开工程。");
        var conflicts = await Task.Run(() => ConfigTransaction.RecoverIncomplete(root, JournalFor(root)));
        plan = null; ApplyConfigButton.IsEnabled = false;
        ConfigPreview.Text = conflicts.Length == 0 ? "已检查未完成事务，能确认归属的写入已恢复。" : "以下文件有外部修改，未覆盖；请按事务备份人工处理：\n" + string.Join("\n", conflicts) + "\n备份：" + JournalFor(root);
        Report(ConfigPreview.Text);
    });
    private async void OpenWeb(object? sender, RoutedEventArgs e) => await Guard(async () =>
    {
        if (!Uri.TryCreate(WebAddress.Text, UriKind.Absolute, out var uri) || uri.Scheme is not ("http" or "https")) throw new ArgumentException("请输入完整 http:// 或 https:// 网页地址。");
        if (transition) throw new InvalidOperationException("连接正在切换，请稍后重试。");
        await StopObservationAsync();
        if (observation != null) throw new IOException("采集连接尚未释放，未打开网页。");
        Process.Start(new ProcessStartInfo(uri.AbsoluteUri) { UseShellExecute = true });
        Report("已释放客户端采集并打开网页；返回远程变量前，请先在网页手动停止 RTT。");
    });
    private void RefreshPorts(object? sender, RoutedEventArgs e)
    { try { var current = SerialPortBox.Text; SerialPortBox.ItemsSource = SerialPort.GetPortNames().Order().ToArray(); SerialPortBox.Text = current; } catch (Exception ex) { Report(ex.Message); } }
    private SerialSettings ReadSerialSettings() => new(SerialPortBox.Text, int.Parse(BaudBox.Text, CultureInfo.InvariantCulture), DataBitsBox.SelectedIndex + 5, (Parity)ParityBox.SelectedItem, (StopBits)StopBitsBox.SelectedItem, (Handshake)FlowBox.SelectedItem);
    private async void ConnectSerial(object? sender, RoutedEventArgs e) => await Guard(async () =>
    {
        if (serial != null || serialOpening || serialClosing) throw new InvalidOperationException("请先关闭当前串口并等待完成。");
        var parameters = ReadSerialSettings();
        ConnectSerialButton.IsEnabled = false;
        var candidate = new SerialSession();
        serial = candidate; serialOpening = true;
        serialText.Clear(); lastReceived = lastSent = 0; lastSerialTime = 0; serialClock.Restart();
        try
        {
            await candidate.OpenAsync(parameters, RecordSerial.IsChecked == true ? System.IO.Path.Combine(App.Data.Root, "serial", "records") : null);
            PersistSettings();
            if (!serialClosing) Report(parameters.Baud > 1000000 ? "实验波特率请求已被驱动接受；实际速率和链路完整性未验收。" : "串口已打开；发送结果表示驱动已接受，尚无对端确认。");
        }
        catch { await candidate.DisposeAsync(); if (ReferenceEquals(serial, candidate)) serial = null; throw; }
        finally { serialOpening = false; ConnectSerialButton.IsEnabled = serial == null && !serialClosing; }
    });
    private async void DisconnectSerial(object? sender, RoutedEventArgs e) => await Guard(CloseSerialAsync);
    private async Task CloseSerialAsync()
    {
        if (serialClosing) throw new InvalidOperationException("串口正在关闭，请稍候。");
        serialClosing = true; ConnectSerialButton.IsEnabled = false;
        try
        {
            periodic?.Cancel();
            if (periodicTask != null) { try { await periodicTask; } catch (Exception ex) { Report("周期发送已停止：" + ex.Message); } }
            var current = serial;
            if (current != null) await current.DisposeAsync();
            serial = null; serialClock.Stop();
            SerialStats.Text = current?.Recorder?.Error is { } error ? "串口已关闭；记录失败：" + error : $"串口已关闭；记录缺口 {current?.Recorder?.Discarded ?? 0} 块。";
        }
        finally { serialClosing = false; ConnectSerialButton.IsEnabled = serial == null && !serialOpening; }
    }
    private async void SendSerial(object? sender, RoutedEventArgs e) => await Guard(() => SendSerialAsync(CancellationToken.None));
    private async Task SendSerialAsync(CancellationToken token)
    {
        if (serialClosing || serialOpening) throw new InvalidOperationException("串口正在切换状态。");
        var current = serial ?? throw new InvalidOperationException("请先打开串口。");
        var text = SendText.Text;
        var bytes = SerialSession.ParseSend(text, HexSend.IsChecked == true, NewlineBox.SelectedIndex switch { 1 => "\n", 2 => "\r\n", _ => "" }, SerialTextBuffer.GetEncoding((string)EncodingBox.SelectedItem));
        if (bytes.Length == 0) throw new ArgumentException("发送内容为空。");
        await current.SendAsync(bytes, token);
        settings = settings with { SendHistory = new[] { text }.Concat(settings.SendHistory ?? []).Distinct().Take(20).ToArray() };
        SendHistoryBox.ItemsSource = settings.SendHistory;
    }
    private async void StartPeriodic(object? sender, RoutedEventArgs e) => await Guard(async () =>
    {
        if (periodic != null) throw new InvalidOperationException("周期发送已启动。");
        var delay = int.Parse(SendPeriod.Text, CultureInfo.InvariantCulture);
        if (delay < 10) throw new ArgumentException("周期不得小于 10 ms。");
        var cancellation = new CancellationTokenSource(); periodic = cancellation;
        periodicTask = RunPeriodicAsync(cancellation, delay);
        try { await periodicTask; } finally { periodicTask = null; }
    });
    private async Task RunPeriodicAsync(CancellationTokenSource cancellation, int delay)
    {
        try { while (!cancellation.IsCancellationRequested) { await SendSerialAsync(cancellation.Token); await Task.Delay(delay, cancellation.Token); } }
        catch (OperationCanceledException) { }
        finally { cancellation.Dispose(); periodic = null; }
    }
    private void StopPeriodic(object? sender, RoutedEventArgs e) => periodic?.Cancel();
    private void ClearSerial(object? sender, RoutedEventArgs e) { serialText.Clear(); SerialText.Clear(); }
    private void SerialEncodingChanged(object? sender, RoutedEventArgs e)
    {
        if (EncodingBox?.SelectedItem is not string encoding || HexDisplay == null || SerialText == null) return;
        serialText.Configure(encoding, HexDisplay.IsChecked == true); SerialText.Clear();
    }
    private void SelectSendHistory(object? sender, SelectionChangedEventArgs e)
    { if (SendHistoryBox.SelectedItem is string text) SendText.Text = text; }
    private void OpenRecords(object? sender, RoutedEventArgs e) => OpenDirectory(System.IO.Path.Combine(App.Data.Root, "serial", "records"));
    private void OpenLogs(object? sender, RoutedEventArgs e) => OpenDirectory(System.IO.Path.Combine(App.Data.Root, "logs"));
    private void OpenDirectory(string path) { try { Directory.CreateDirectory(path); Process.Start(new ProcessStartInfo(path) { UseShellExecute = true }); } catch (Exception ex) { Report(ex.Message); } }
    private void BrowseDll(object? sender, RoutedEventArgs e)
    { var dialog = new OpenFileDialog { Filter = "SEGGER 64 位库|JLink_x64.dll|DLL|*.dll" }; if (dialog.ShowDialog(this) == true) DllPath.Text = dialog.FileName; }
    private void SaveSettings(object? sender, RoutedEventArgs e)
    { try { PersistSettings(); Report("设置已保存。升级时保留程序目录下的 data。"); } catch (Exception ex) { Report("设置保存失败：" + ex.Message); } }
    private async void ImportTool(object? sender, RoutedEventArgs e)
    {
        var dialog = new OpenFileDialog { Filter = "Debug_LAN 发布包|DebugLAN-*.zip|ZIP|*.zip" };
        if (dialog.ShowDialog(this) == true) await Guard(() => PrepareToolAsync(dialog.FileName));
    }
    private async void DownloadTool(object? sender, RoutedEventArgs e) => await Guard(() => PrepareToolAsync(null));
    private async void RefreshTools(object? sender, RoutedEventArgs e) => await Guard(async () =>
    {
        if (toolBusy) throw new InvalidOperationException("工具正在准备，请稍候。");
        toolBusy = true;
        try
        {
            var version = (ToolVersionBox.SelectedItem as ToolRelease)?.Version;
            ToolStatus.Text = "正在查询官方发布清单并核对标签提交和摘要；工程版本不会自动改变。";
            var available = await ToolCatalog.RefreshAsync(App.Data.Root);
            ToolVersionBox.ItemsSource = available;
            ToolVersionBox.SelectedItem = available.FirstOrDefault(v => v.Version == version) ?? available.First();
            ToolStatus.Text = $"已核验 {available.Length} 个兼容工具版本。选择后下载或导入，再预览更新工程锁。";
        }
        catch (Exception ex) { ToolStatus.Text = "更新检查失败，已安装工具仍可离线使用：" + ex.Message; throw; }
        finally { toolBusy = false; }
    });
    private async Task PrepareToolAsync(string? zip)
    {
        if (toolBusy) throw new InvalidOperationException("工具正在准备，请稍候。");
        var release = ToolVersionBox.SelectedItem as ToolRelease ?? throw new InvalidOperationException("请选择工具版本。");
        toolBusy = true; string? download = null;
        ToolStatus.Text = "正在校验并准备 " + release.Version + "；仅运行无硬件版本检查。";
        try
        {
            if (zip == null)
            {
                var directory = System.IO.Path.Combine(App.Data.Root, "downloads"); Directory.CreateDirectory(directory);
                download = System.IO.Path.Combine(directory, Guid.NewGuid().ToString("N") + ".zip");
                using var client = new HttpClient { Timeout = TimeSpan.FromMinutes(2) };
                client.DefaultRequestHeaders.UserAgent.ParseAdd("EmbeddedHelper/" + Product.Version);
                using var response = await client.GetAsync(release.DownloadUrl, HttpCompletionOption.ResponseHeadersRead); response.EnsureSuccessStatusCode();
                await using var source = await response.Content.ReadAsStreamAsync();
                await using (var output = File.Create(download))
                {
                    var buffer = new byte[65536]; long total = 0; int count;
                    using var timeout = new CancellationTokenSource(TimeSpan.FromMinutes(2));
                    while ((count = await source.ReadAsync(buffer, timeout.Token)) > 0)
                    {
                        total += count; if (total > 128L * 1024 * 1024) throw new IOException("工具下载超过128MiB上限。");
                        await output.WriteAsync(buffer.AsMemory(0, count), timeout.Token);
                    }
                }
                zip = download;
            }
            var file = zip;
            var executable = await Task.Run(() => new ToolCache(App.Data.Root).InstallAsync(file, release));
            ToolStatus.Text = "已准备 " + release.Version + "\n" + executable + "\n工程版本尚未改变；到远程配置页预览接入或回退。";
        }
        catch (Exception ex) { ToolStatus.Text = "工具准备失败：" + ex.Message; throw; }
        finally { toolBusy = false; if (download != null && File.Exists(download)) File.Delete(download); }
    }
    private void BindCli(object? sender, RoutedEventArgs e)
    {
        try
        {
            Environment.SetEnvironmentVariable("EMBEDDED_HELPER_HOME", AppContext.BaseDirectory.TrimEnd(System.IO.Path.DirectorySeparatorChar), EnvironmentVariableTarget.User);
            Environment.SetEnvironmentVariable("EMBEDDED_HELPER_HOME", AppContext.BaseDirectory.TrimEnd(System.IO.Path.DirectorySeparatorChar));
            Report("已绑定本机入口；请完全退出并重新打开 VS Code 使环境变量生效。搬迁本软件后再次点击此按钮即可。");
        }
        catch (Exception ex) { Report("入口绑定失败：" + ex.Message); }
    }
    private void PersistSettings()
    {
        settings = settings with { Dll = DllPath.Text, ReadElf = ReadElfPath.Text, Probe = ProbeSerial.Text, Width = ActualWidth > 0 ? ActualWidth : Width, Height = ActualHeight > 0 ? ActualHeight : Height, Serial = ReadSerialSettings(), SerialEncoding = EncodingBox.SelectedItem as string ?? "utf-8" };
        App.Data.Save("settings/window.json", settings);
    }
    private void EnvironmentCheck(object? sender, RoutedEventArgs e)
    {
        EnvironmentText.Text = $"版本：{Product.Version}\n系统：{Environment.OSVersion} / {System.Runtime.InteropServices.RuntimeInformation.ProcessArchitecture}\n程序目录：{AppContext.BaseDirectory}\n数据目录：{App.Data.Root}\nSEGGER 库：{(File.Exists(DllPath.Text) ? "文件存在（未加载、未连接设备）" : "缺失，请选择 JLink_x64.dll")}\n硬件状态：未检测；仅用户点击开始/打开串口时连接。\n本包自包含运行时；纯净 Windows 与硬件验收仍待用户执行。";
    }
    private async void WindowClosing(object? sender, CancelEventArgs e)
    {
        if (closing) return;
        e.Cancel = true;
        if (toolBusy) { Report("正在准备工具，请等待下载或校验完成后关闭。"); return; }
        if (transition) { Report("正在切换连接，请稍后关闭。"); return; }
        await Guard(async () => { await CloseSerialAsync(); await StopObservationAsync(); try { PersistSettings(); } catch (Exception ex) { Report("输入无效，保留上次设置：" + ex.Message); } refresh.Stop(); closing = true; Close(); });
    }
    public async Task SmokeAsync(string directory)
    {
        try
        {
            Directory.CreateDirectory(directory);
            await LoadProjectAsync(System.IO.Path.Combine(directory, "project"));
            ElfPath.Text = System.IO.Path.Combine(directory, "scalars.elf");
            await ParseElfAsync();
            if (Variables.Count(v => v.Variable.Supported) != 7) throw new InvalidDataException("标量类型自检失败。");
            RemoteHost.Text = "test.example.invalid"; PreviewConfig(null, null!);
            if (plan == null) throw new InvalidDataException("远程配置预览失败。");
            var toolPackage = System.IO.Path.Combine(directory, "debug-lan.zip");
            if (File.Exists(toolPackage))
            {
                await PrepareToolAsync(toolPackage);
                UseEideUpload.IsChecked = true; GdbPort.Text = "2450"; WebAddress.Text = "http://test.example.invalid:8000";
                PreviewIntegration(null, null!);
                if (plan == null || plan.Changes.Length == 0) throw new InvalidDataException("一键接入预览失败。");
                await ApplyConfigAsync();
                await LoadProjectAsync(project!.Root);
                if (UseEideUpload.IsChecked != true || GdbPort.Text != "2450" || WebAddress.Text != "http://test.example.invalid:8000") throw new InvalidDataException("远程配置读回失败。");
                ElfPath.Text = System.IO.Path.Combine(directory, "scalars.elf"); await ParseElfAsync();
                PreviewIntegration(null, null!);
                if (plan == null || plan.Changes.Length != 0) throw new InvalidDataException("重复接入非幂等。");
                PreviewRemoveIntegration(null, null!);
                if (plan == null || plan.Changes.Length == 0) throw new InvalidDataException("共享移除预览失败。");
                await ApplyConfigAsync();
            }
            PersistSettings();
            var restored = App.Data.Load("settings/window.json", () => new AppSettings());
            if (restored.Recent?.Contains(project!.Root) != true) throw new InvalidDataException("设置回读失败。");
            var savedElf = ElfPath.Text;
            ElfPath.Text = System.IO.Path.Combine(directory, "missing.elf");
            await Guard(ParseElfAsync);
            if (!StatusLabel.Text.StartsWith("操作未完成：", StringComparison.Ordinal)) throw new InvalidDataException("ELF 错误未反馈到界面。");
            ElfPath.Text = savedElf;
            await Guard(() => SendSerialAsync(CancellationToken.None));
            if (!StatusLabel.Text.Contains("请先打开串口")) throw new InvalidDataException("未连接串口的错误未反馈。");
            Report("离线 GUI 自检：工程、ELF、设置与错误反馈通过；硬件未验收。");
            for (int i = 0; i < Pages.Items.Count; i++)
            {
                Pages.SelectedIndex = i;
                await Dispatcher.InvokeAsync(() => { }, DispatcherPriority.ApplicationIdle);
                UpdateLayout();
                var bitmap = new RenderTargetBitmap((int)ActualWidth, (int)ActualHeight, 96, 96, PixelFormats.Pbgra32);
                bitmap.Render(this);
                var encoder = new PngBitmapEncoder(); encoder.Frames.Add(BitmapFrame.Create(bitmap));
                using var output = File.Create(System.IO.Path.Combine(directory, $"page-{i}.png")); encoder.Save(output);
            }
            PortableData.SaveFile(System.IO.Path.Combine(directory, "smoke.json"), new { status = "passed", exitCode = 0, version = Product.Version, pages = Pages.Items.Count, scalarCount = Variables.Count, toolIntegration = File.Exists(toolPackage), method = "automated", hardware = "not-tested" });
            closing = true; refresh.Stop(); Application.Current.Shutdown(0);
        }
        catch (Exception ex)
        {
            PortableData.SaveFile(System.IO.Path.Combine(directory, "smoke.json"), new { status = "failed", exitCode = 1, error = ex.ToString() });
            closing = true; refresh.Stop(); Application.Current.Shutdown(1);
        }
    }
}

using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.IO.Ports;
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
    private string value = variable.Supported ? "未采集" : variable.Status;
    public string Value { get => value; set { this.value = value; Changed(); } }
    public event PropertyChangedEventHandler? PropertyChanged;
    private void Changed([CallerMemberName] string? name = null) => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
}
public sealed record AppSettings(string Dll = "", string Probe = "", string[]? Recent = null, double Width = 1280, double Height = 860, SerialSettings? Serial = null, string ReadElf = "");
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
    private readonly DispatcherTimer refresh = new() { Interval = TimeSpan.FromMilliseconds(100) };
    private readonly Decoder rttDecoder = Encoding.UTF8.GetDecoder();
    private readonly Dictionary<string, Decoder> serialDecoders = new() { ["RX"] = Encoding.UTF8.GetDecoder(), ["TX"] = Encoding.UTF8.GetDecoder() };
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
        if (File.Exists(loaded.RemotePath))
        {
            using var remote = JsonDocument.Parse(await File.ReadAllTextAsync(loaded.RemotePath), ProjectReader.JsonOptions);
            RemoteHost.Text = remote.RootElement.GetProperty("ServerName").GetString();
            RemotePort.Text = remote.RootElement.GetProperty("Port").GetInt32().ToString(CultureInfo.InvariantCulture);
        }
        settings = settings with { Recent = new[] { loaded.Root }.Concat(settings.Recent ?? []).Distinct(StringComparer.OrdinalIgnoreCase).Take(8).ToArray() };
        RecentProjects.ItemsSource = settings.Recent;
        PersistSettings();
        plan = null; ApplyConfigButton.IsEnabled = false;
        Report("已打开 " + loaded.Name + "。请选择 ELF 解析变量，或维护远程配置。");
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
        if (observation != null) { Report("请先停止采集再更换 ELF。"); return; }
        var dialog = new OpenFileDialog { Filter = "固件 ELF|*.elf;*.axf|所有文件|*.*" };
        if (dialog.ShowDialog(this) == true) { ElfPath.Text = dialog.FileName; await Guard(ParseElfAsync); }
    }
    private async void ParseElf(object? sender, RoutedEventArgs e) => await Guard(ParseElfAsync);
    private async Task ParseElfAsync()
    {
        if (observation != null) throw new InvalidOperationException("请先停止并释放，再更新地址并重新开始。");
        var path = ElfPath.Text;
        var tool = ReadElfPath.Text;
        var indexed = await Task.Run(() => ElfIndex.Read(path, tool));
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
        Variables.Clear();
        foreach (var variable in indexed) Variables.Add(new VariableRow(variable) { Selected = variable.Supported && selected.Contains(variable.Identity) });
        elfStamp = File.GetLastWriteTimeUtc(path);
        ObservationStatus.Text = $"ELF 已解析：{indexed.Length} 项，{indexed.Count(v => v.Supported)} 项可读取 · 与目标固件匹配未核验";
        Report("变量索引完成；地址来自 ELF，尚未连接硬件。");
    }
    private async void StartObservation(object? sender, RoutedEventArgs e) => await Guard(async () =>
    {
        if (transition || observation != null) throw new InvalidOperationException("请先停止已有采集。");
        if (Target is not { } target) throw new InvalidOperationException("请先选择工程目标。");
        var variables = Variables.Where(v => v.Selected).Select(v => v.Variable).ToArray();
        if (variables.Length == 0 && EnableRtt.IsChecked != true) throw new InvalidOperationException("请选择变量或启用 RTT。");
        if (variables.Any(v => !v.Supported)) throw new InvalidOperationException("观察列表包含不支持的变量类型。");
        transition = true; StartButton.IsEnabled = false; TargetBox.IsEnabled = false;
        var candidate = new ObservationSession();
        try
        {
            var remote = TransportBox.SelectedIndex == 1;
            var request = new ObservationRequest(DllPath.Text, int.TryParse(ProbeSerial.Text, out var number) ? number : 0,
                remote ? RemoteHost.Text : "", int.Parse(RemotePort.Text, CultureInfo.InvariantCulture), target.Device, target.Interface, target.SpeedKHz,
                int.Parse(SampleRate.Text, CultureInfo.InvariantCulture), EnableRtt.IsChecked == true && !remote, variables);
            await candidate.StartAsync(System.IO.Path.Combine(AppContext.BaseDirectory, "EmbeddedHelper.Worker.exe"), request);
            observation = candidate; samples = 0; sampling.Restart(); rttDecoder.Reset();
            if (project != null) PortableData.SaveFile(System.IO.Path.Combine(project.Root, ".embedded_helper", "watch.json"), new WatchSettings(target.Name, variables.Select(v => v.Identity).ToArray(), request.SampleRate));
            PersistSettings();
        }
        catch { await candidate.DisposeAsync(); throw; }
        finally { transition = false; StartButton.IsEnabled = observation == null; TargetBox.IsEnabled = observation == null; }
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
            StartButton.IsEnabled = true; TargetBox.IsEnabled = true;
        }
        finally { transition = false; }
    }
    private void Refresh(object? sender, EventArgs e)
    {
        if (observation != null)
        {
            while (observation.Events.TryRead(out var item))
            {
                if (item.Kind != "sample") { if (item.Message.Length > 0) Report(item.Message); continue; }
                samples++;
                if (PauseObservation.IsChecked == true) continue;
                if (item.Values != null) foreach (var row in Variables)
                    if (item.Values.TryGetValue(row.Variable.Identity, out var value))
                    {
                        row.Value = value;
                        if (double.TryParse(value, CultureInfo.InvariantCulture, out var number) && double.IsFinite(number))
                        { row.History.Enqueue(number); if (row.History.Count > 2000) row.History.Dequeue(); }
                    }
                if (item.RttBase64 != null) AppendBounded(RttText, Decode(rttDecoder, Convert.FromBase64String(item.RttBase64)));
            }
            ObservationStatus.Text = $"{observation.State} · 实际显示批次 {samples / Math.Max(1, sampling.Elapsed.TotalSeconds):F1} Hz · 显示缺口 {observation.Dropped}";
            if (PauseObservation.IsChecked != true) DrawPlot();
        }
        if (!string.IsNullOrEmpty(ElfPath.Text) && elfStamp != default && File.GetLastWriteTimeUtc(ElfPath.Text) != elfStamp)
            ObservationStatus.Text += " · ELF 已变化，当前地址未替换";
        if (serial != null)
        {
            while (serial.Display.TryRead(out var block))
            {
                if (PauseSerial.IsChecked == true) continue;
                AppendBounded(SerialText, $"[{block.Direction}] " + (HexDisplay.IsChecked == true ? Convert.ToHexString(block.Bytes) + "\n" : Decode(serialDecoders[block.Direction], block.Bytes)));
            }
            SerialStats.Text = $"{(serial.Connected ? "已连接" : "连接故障")} · RX {serial.Received:N0} B · TX 驱动已接受 {serial.Sent:N0} B · 显示裁剪 {serial.Clipped:N0} B · 原始记录缺口 {serial.Recorder?.Discarded ?? 0} · {serial.Error ?? serial.Recorder?.Error}";
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
    private void ClearRtt(object? sender, RoutedEventArgs e) => RttText.Clear();
    private void PreviewConfig(object? sender, RoutedEventArgs e)
    {
        try
        {
            plan = RemoteConfiguration.Plan(project ?? throw new InvalidOperationException("请先打开工程。"), Target ?? throw new InvalidOperationException("请选择目标。"), RemoteHost.Text, int.Parse(RemotePort.Text, CultureInfo.InvariantCulture));
            ConfigPreview.Text = string.Join("\n\n", plan.Changes.Select(c => c.Path + "\n" + c.After));
            ApplyConfigButton.IsEnabled = true;
        }
        catch (Exception ex) { plan = null; ApplyConfigButton.IsEnabled = false; Report(ex.Message); }
    }
    private async void ApplyConfig(object? sender, RoutedEventArgs e) => await Guard(async () =>
    {
        if (plan == null) throw new InvalidOperationException("请先预览修改。");
        var ready = plan;
        var journal = System.IO.Path.Combine(App.Data.Root, "projects", ConfigTransaction.Hash(ready.Root), "transactions");
        await Task.Run(() => ConfigTransaction.Apply(ready, journal));
        plan = null; ApplyConfigButton.IsEnabled = false; Report("已保存远程配置，原有未知字段保留。未执行烧录。");
    });
    private void OpenWeb(object? sender, RoutedEventArgs e)
    {
        try
        {
            if (!Uri.TryCreate(WebAddress.Text, UriKind.Absolute, out var uri) || uri.Scheme is not ("http" or "https")) throw new ArgumentException("请输入完整 http:// 或 https:// 网页地址。");
            Process.Start(new ProcessStartInfo(uri.AbsoluteUri) { UseShellExecute = true });
        }
        catch (Exception ex) { Report(ex.Message); }
    }
    private void RefreshPorts(object? sender, RoutedEventArgs e)
    { try { var current = SerialPortBox.Text; SerialPortBox.ItemsSource = SerialPort.GetPortNames().Order().ToArray(); SerialPortBox.Text = current; } catch (Exception ex) { Report(ex.Message); } }
    private SerialSettings ReadSerialSettings() => new(SerialPortBox.Text, int.Parse(BaudBox.Text, CultureInfo.InvariantCulture), DataBitsBox.SelectedIndex + 5, (Parity)ParityBox.SelectedItem, (StopBits)StopBitsBox.SelectedItem, (Handshake)FlowBox.SelectedItem);
    private async void ConnectSerial(object? sender, RoutedEventArgs e) => await Guard(async () =>
    {
        if (serial != null) throw new InvalidOperationException("请先关闭当前串口。");
        ConnectSerialButton.IsEnabled = false;
        var candidate = new SerialSession();
        try
        {
            await candidate.OpenAsync(ReadSerialSettings(), RecordSerial.IsChecked == true ? System.IO.Path.Combine(App.Data.Root, "serial", "records") : null);
            serial = candidate; foreach (var decoder in serialDecoders.Values) decoder.Reset(); PersistSettings();
        }
        catch { await candidate.DisposeAsync(); throw; }
        finally { ConnectSerialButton.IsEnabled = true; }
    });
    private async void DisconnectSerial(object? sender, RoutedEventArgs e) => await Guard(CloseSerialAsync);
    private async Task CloseSerialAsync()
    { periodic?.Cancel(); if (serial != null) await serial.DisposeAsync(); serial = null; SerialStats.Text = "串口已关闭，原始记录已完成写入。"; }
    private async void SendSerial(object? sender, RoutedEventArgs e) => await Guard(() => SendSerialAsync(CancellationToken.None));
    private Task SendSerialAsync(CancellationToken token) => (serial ?? throw new InvalidOperationException("请先打开串口。")).SendAsync(SerialSession.ParseSend(SendText.Text, HexSend.IsChecked == true, NewlineBox.SelectedIndex switch { 1 => "\n", 2 => "\r\n", _ => "" }), token);
    private async void StartPeriodic(object? sender, RoutedEventArgs e) => await Guard(async () =>
    {
        if (periodic != null) throw new InvalidOperationException("周期发送已启动。");
        var delay = int.Parse(SendPeriod.Text, CultureInfo.InvariantCulture);
        if (delay < 10) throw new ArgumentException("周期不得小于 10 ms。");
        periodic = new CancellationTokenSource();
        try { while (!periodic.IsCancellationRequested) { await SendSerialAsync(periodic.Token); await Task.Delay(delay, periodic.Token); } }
        catch (OperationCanceledException) { }
        finally { periodic.Dispose(); periodic = null; }
    });
    private void StopPeriodic(object? sender, RoutedEventArgs e) => periodic?.Cancel();
    private void ClearSerial(object? sender, RoutedEventArgs e) => SerialText.Clear();
    private void OpenRecords(object? sender, RoutedEventArgs e) => OpenDirectory(System.IO.Path.Combine(App.Data.Root, "serial", "records"));
    private void OpenLogs(object? sender, RoutedEventArgs e) => OpenDirectory(System.IO.Path.Combine(App.Data.Root, "logs"));
    private void OpenDirectory(string path) { try { Directory.CreateDirectory(path); Process.Start(new ProcessStartInfo(path) { UseShellExecute = true }); } catch (Exception ex) { Report(ex.Message); } }
    private void BrowseDll(object? sender, RoutedEventArgs e)
    { var dialog = new OpenFileDialog { Filter = "SEGGER 64 位库|JLink_x64.dll|DLL|*.dll" }; if (dialog.ShowDialog(this) == true) DllPath.Text = dialog.FileName; }
    private void SaveSettings(object? sender, RoutedEventArgs e)
    { try { PersistSettings(); Report("设置已保存。升级时保留程序目录下的 data。"); } catch (Exception ex) { Report("设置保存失败：" + ex.Message); } }
    private void PersistSettings()
    {
        settings = settings with { Dll = DllPath.Text, ReadElf = ReadElfPath.Text, Probe = ProbeSerial.Text, Width = ActualWidth > 0 ? ActualWidth : Width, Height = ActualHeight > 0 ? ActualHeight : Height, Serial = ReadSerialSettings() };
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
            PortableData.SaveFile(System.IO.Path.Combine(directory, "smoke.json"), new { status = "passed", exitCode = 0, version = Product.Version, pages = Pages.Items.Count, scalarCount = Variables.Count, method = "automated", hardware = "not-tested" });
            closing = true; refresh.Stop(); Application.Current.Shutdown(0);
        }
        catch (Exception ex)
        {
            PortableData.SaveFile(System.IO.Path.Combine(directory, "smoke.json"), new { status = "failed", exitCode = 1, error = ex.ToString() });
            closing = true; refresh.Stop(); Application.Current.Shutdown(1);
        }
    }
}

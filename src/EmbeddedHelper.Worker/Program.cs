using System.Diagnostics;
using System.Globalization;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;
using EmbeddedHelper.Core;

Console.OutputEncoding = new UTF8Encoding(false);
if (args is ["--version"] or ["--self-test"]) { Console.WriteLine(Product.Version); return 0; }
if (args is ["--list-probes"])
{
    try
    {
        var dll = JsonSerializer.Deserialize<string>(Console.ReadLine() ?? "") ?? throw new InvalidDataException("缺少 J-Link 组件路径。");
        Console.WriteLine(JsonSerializer.Serialize(UsbProbeDiscovery.Read(dll)));
        return 0;
    }
    catch (Exception ex) { Console.Error.WriteLine(ex.Message); return 1; }
}
if (args is not ["--observe"]) { Console.Error.WriteLine("请由 Embedded Helper 启动采集。"); return 2; }
try
{
    var request = JsonSerializer.Deserialize<ObservationRequest>(Console.ReadLine() ?? "") ?? throw new InvalidDataException("缺少采集请求。");
    var plan = new ReadPlan(request.Generation, request.SampleRate, request.Variables); plan.Validate();
    if (request.SessionId.Length != 32) throw new InvalidDataException("无效会话身份。");
    var mailbox = new ReadPlanMailbox(plan);
    using var cancel = new CancellationTokenSource();
    _ = Task.Run(() =>
    {
        try
        {
            while (Console.ReadLine() is { } line)
            {
                if (line.Length > 1024 * 1024) throw new InvalidDataException("控制请求过长。");
                mailbox.Submit(JsonSerializer.Deserialize<ReadPlan>(line) ?? throw new InvalidDataException("缺少计划。"));
            }
        }
        catch (Exception ex) { Console.Error.WriteLine("控制通道失败：" + ex.Message); }
        finally { cancel.Cancel(); }
    });
    using var link = new Link(request);
    Emit(new ObservationEvent("state", "采集中 · 固件与 ELF 匹配未核验", SessionId: request.SessionId, Generation: plan.Generation));
    Emit(new ObservationEvent("plan", "读取计划已应用", SessionId: request.SessionId, Generation: plan.Generation));
    var timer = Stopwatch.StartNew();
    var health = new SamplingHealth();
    long sequence = 0, round = 0;
    while (!cancel.IsCancellationRequested)
    {
        if (mailbox.Take() is { } updated)
        {
            plan = updated; round = 0; timer.Restart(); health.Reset();
            Emit(new ObservationEvent("plan", "读取计划已应用", SessionId: request.SessionId, Generation: plan.Generation));
        }
        var readStart = Stopwatch.GetTimestamp();
        var values = new Dictionary<string, string>();
        var successful = 0;
        foreach (var variable in plan.Variables)
        {
            try { values[variable.Identity] = ElfIndex.Decode(variable, link.Read(variable)).ToString("G9", CultureInfo.InvariantCulture); successful++; }
            catch (IOException) { values[variable.Identity] = "读取失败"; }
        }
        health.Observe(plan.Variables.Length, successful, timer.Elapsed);
        var rtt = link.Rtt();
        Emit(new ObservationEvent("sample", "", values, rtt.Length > 0 ? Convert.ToBase64String(rtt) : null, ++sequence, request.SessionId, plan.Generation, readStart, Stopwatch.GetTimestamp(), DateTimeOffset.UtcNow));
        var delay = (int)Math.Max(0, ++round * 1000.0 / plan.SampleRate - timer.Elapsed.TotalMilliseconds);
        if (delay > 0) { try { await Task.Delay(Math.Min(delay, 1000), cancel.Token); } catch (OperationCanceledException) { } }
    }
    return 0;
}
catch (Exception ex) { Console.Error.WriteLine(ex.Message); return 1; }
static void Emit(ObservationEvent item) => Console.WriteLine(JsonSerializer.Serialize(item));

internal sealed class Link : IDisposable
{
    private readonly IntPtr library;
    private bool open, rtt;
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)] private delegate IntPtr OpenDelegate();
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)] private delegate void VoidDelegate();
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)] private delegate int IntDelegate();
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)] private delegate int SelectDelegate(int value);
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)] private delegate void SpeedDelegate(int value);
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)] private delegate int IpDelegate([MarshalAs(UnmanagedType.LPStr)] string host, int port);
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)] private delegate int CommandDelegate([MarshalAs(UnmanagedType.LPStr)] string command, StringBuilder error, int size);
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)] private delegate int ReadDelegate(uint address, uint count, [Out] byte[] bytes, uint access);
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)] private delegate int RttControlDelegate(uint command, IntPtr config);
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)] private delegate int RttReadDelegate(uint channel, [Out] byte[] bytes, uint count);
    private T Function<T>(string name) where T : Delegate => Marshal.GetDelegateForFunctionPointer<T>(NativeLibrary.GetExport(library, name));
    public Link(ObservationRequest request)
    {
        if (!Path.IsPathFullyQualified(request.Dll)) throw new ArgumentException("SDK 必须为绝对路径。");
        library = NativeLibrary.Load(request.Dll);
        try
        {
            if (request.Host.Length > 0)
            {
                if (request.Port is < 1 or > 65535 || Function<IpDelegate>("JLINKARM_SelectIP")(request.Host, request.Port) != 0) throw new IOException("远程探针选择失败。");
            }
            else if (request.ProbeSerial <= 0 || Function<SelectDelegate>("JLINKARM_EMU_SelectByUSBSN")(request.ProbeSerial) < 0) throw new IOException("指定探针不可用。");
            var error = Function<OpenDelegate>("JLINKARM_Open")();
            if (error != IntPtr.Zero) throw new IOException(Marshal.PtrToStringAnsi(error));
            open = true;
            Command("InhibitConnectRetries = 1");
            Command("HideDeviceSelection = 1");
            if (Function<SelectDelegate>("JLINKARM_TIF_Select")(request.Interface == "SWD" ? 1 : 0) < 0) throw new IOException("接口选择失败。");
            Function<SpeedDelegate>("JLINKARM_SetSpeed")(request.SpeedKHz);
            if (!System.Text.RegularExpressions.Regex.IsMatch(request.Device, "^[A-Za-z0-9_-]+$")) throw new ArgumentException("芯片名称无效。");
            Command("Device = " + request.Device);
            if (Function<IntDelegate>("JLINKARM_Connect")() < 0) throw new IOException("目标连接失败；请先停止其他调试工具。");
            if (request.Rtt && request.Host.Length == 0)
            {
                rtt = Function<RttControlDelegate>("JLINK_RTTERMINAL_Control")(0, IntPtr.Zero) >= 0;
                if (!rtt) Console.WriteLine(JsonSerializer.Serialize(new ObservationEvent("notice", "RTT 启动失败；变量采集仍可使用。", SessionId: request.SessionId, Generation: request.Generation)));
            }
        }
        catch { Dispose(); throw; }
    }
    private void Command(string command)
    {
        var error = new StringBuilder(1024);
        if (Function<CommandDelegate>("JLINKARM_ExecCommand")(command, error, error.Capacity) < 0) throw new IOException("SDK 拒绝命令：" + error);
    }
    public byte[] Read(Variable variable)
    {
        var bytes = new byte[variable.Size];
        var count = Function<ReadDelegate>("JLINKARM_ReadMemEx")((uint)variable.Address, (uint)bytes.Length, bytes, 1);
        if (count != bytes.Length) throw new IOException("目标读取长度不完整。");
        return bytes;
    }
    public byte[] Rtt()
    {
        if (!rtt) return [];
        var bytes = new byte[8192];
        var count = Function<RttReadDelegate>("JLINK_RTTERMINAL_Read")(0, bytes, (uint)bytes.Length);
        if (count < 0) throw new IOException("RTT 读取失败。");
        return bytes.AsSpan(0, count).ToArray();
    }
    public void Dispose()
    {
        if (open)
        {
            try { if (rtt) Function<RttControlDelegate>("JLINK_RTTERMINAL_Control")(1, IntPtr.Zero); }
            finally { Function<VoidDelegate>("JLINKARM_Close")(); open = false; }
        }
        NativeLibrary.Free(library);
    }
}

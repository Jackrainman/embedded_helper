using System.Diagnostics;
using System.Text.Json;
using EmbeddedHelper.Core;

try
{
    if (args.FirstOrDefault() == "--cli") args = args[1..];
    var data = RuntimePaths.Current.DataDirectory;
    if (args.Length >= 2 && args[0] == "--data") { data = Path.GetFullPath(args[1]); args = args[2..]; }
    if (args is ["--version"] or ["--self-test"]) { Console.WriteLine(Product.Version); return 0; }
    var cache = new ToolCache(data);
    if (args is ["tool", "refresh"]) { Console.WriteLine(JsonSerializer.Serialize(await ToolCatalog.RefreshAsync(data), Product.Json)); return 0; }
    if (args is ["tool", "install", var zip, var version])
    { Console.WriteLine(await cache.InstallAsync(zip, ToolRelease.Find(version))); return 0; }
    if (args is ["tool", "version", var installedVersion])
    { Console.WriteLine(await ToolCache.VersionAsync(cache.Resolve(ToolRelease.Find(installedVersion)))); return 0; }
    if (args is ["--project", var selectRoot, "use-tool", var selectedVersion])
    {
        var project = ProjectReader.Read(selectRoot);
        var release = ToolRelease.Find(selectedVersion); cache.Resolve(release);
        ConfigTransaction.Apply(new ConfigPlan(project.Root, [ToolCache.SelectVersion(project.Root, release)]), Path.Combine(data, "projects", ConfigTransaction.Hash(project.Root), "transactions"));
        Console.WriteLine("工程已锁定 Debug_LAN " + release.Version); return 0;
    }
    if (args is ["--project", var versionRoot, "tool-version"])
    { Console.WriteLine(await ToolCache.VersionAsync(cache.Resolve(ToolCache.ReadLock(ProjectReader.Read(versionRoot).Root)))); return 0; }
    if (args is ["--project", var path, "check"])
    {
        var project = ProjectReader.Read(path);
        Console.WriteLine(JsonSerializer.Serialize(project, Product.Json));
        if (File.Exists(Path.Combine(project.Root, ".embedded_helper", "tools.lock.json")))
            return await Run(cache.Resolve(ToolCache.ReadLock(project.Root)), ["--check", "--config", project.RemotePath]);
        Console.WriteLine("工程读取通过；未锁定远程工具，未检查远程执行环境。"); return 0;
    }
    if (args.Length >= 3 && args[0] == "--project" && args[2] is "flash" or "gdb-server")
    {
        var project = ProjectReader.Read(args[1]);
        var executable = cache.Resolve(ToolCache.ReadLock(project.Root));
        var forwarded = new List<string> { "--config", project.RemotePath };
        if (args is [_, _, "flash", "--program", var program]) { forwarded.Add("--program"); forwarded.Add(Path.GetFullPath(program, project.Root)); }
        else if (args is [_, _, "gdb-server", "--gdb-port", var port] && int.TryParse(port, out var number) && number is >= 1 and <= 65533)
        { forwarded.AddRange(["--gdb-server", "--gdb-port", port]); }
        else throw new ArgumentException("flash要求--program；gdb-server要求--gdb-port（1～65533）。");
        Console.WriteLine("请确认已停止占用该探针的采集/调试会话；开始调用工程锁定的 Debug_LAN 工具。");
        return await Run(executable, forwarded);
    }
    if (args is ["elf", var elf]) { Console.WriteLine(JsonSerializer.Serialize(ElfIndex.Read(elf), Product.Json)); return 0; }
    Console.Error.WriteLine("embedded-helper-cli --version | elf <文件> | tool install <ZIP> <版本> | tool version <版本>\n--project <目录> check | tool-version | use-tool <版本> | flash --program <固件> | gdb-server --gdb-port <端口>\n可在开头指定--data <便携数据目录>；实际设备操作只在显式flash/gdb-server时执行。");
    return 2;
}
catch (Exception ex) { Console.Error.WriteLine(ex.Message); return 1; }

static async Task<int> Run(string executable, IEnumerable<string> arguments)
{
    var start = new ProcessStartInfo(executable) { UseShellExecute = false };
    foreach (var argument in arguments) start.ArgumentList.Add(argument);
    using var child = Process.Start(start) ?? throw new IOException("无法启动工具。");
    using var cancellation = new CancellationTokenSource();
    ConsoleCancelEventHandler cancel = (_, e) => { e.Cancel = true; cancellation.Cancel(); };
    Console.CancelKeyPress += cancel;
    try { await child.WaitForExitAsync(cancellation.Token); return child.ExitCode; }
    catch (OperationCanceledException) { if (!child.HasExited) child.Kill(true); await child.WaitForExitAsync(); return 130; }
    finally { Console.CancelKeyPress -= cancel; }
}

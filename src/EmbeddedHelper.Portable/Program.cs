using System.Diagnostics;
using System.IO.Compression;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text.Json;

try
{
    var assembly = Assembly.GetExecutingAssembly();
    using var metadata = assembly.GetManifestResourceStream("payload.json") ?? throw new IOException("此开发构建没有内嵌客户端，请使用正式打包入口。");
    var payload = JsonSerializer.Deserialize<Payload>(metadata) ?? throw new IOException("内嵌清单无效。");
    var runtime = Path.Combine(AppContext.BaseDirectory, "data", "runtime");
    Directory.CreateDirectory(runtime);
    CheckPath(runtime);
    var destination = Path.Combine(runtime, payload.Version + "-" + payload.Sha256[..16]);
    // Cross-process extraction lock; normal GUI runs do not retain it.
    var timer = Stopwatch.StartNew(); FileStream? guard = null;
    while (guard == null)
    {
        try { guard = new FileStream(Path.Combine(runtime, "prepare.lock"), FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None); }
        catch (IOException) when (timer.Elapsed < TimeSpan.FromSeconds(30)) { Thread.Sleep(100); }
    }
    using (guard)
    {
        if (!Directory.Exists(destination))
        {
            var staging = destination + "." + Guid.NewGuid().ToString("N") + ".tmp";
            Directory.CreateDirectory(staging);
            try
            {
                using var source = assembly.GetManifestResourceStream("payload.zip") ?? throw new IOException("缺少内嵌客户端。");
                if (!Convert.ToHexString(SHA256.HashData(source)).Equals(payload.Sha256, StringComparison.OrdinalIgnoreCase)) throw new IOException("内嵌客户端摘要不匹配。");
                source.Position = 0;
                using var archive = new ZipArchive(source, ZipArchiveMode.Read);
                foreach (var entry in archive.Entries)
                {
                    var target = SafePath(staging, entry.FullName);
                    Directory.CreateDirectory(Path.GetDirectoryName(target)!);
                    using var input = entry.Open(); using var output = new FileStream(target, FileMode.CreateNew);
                    input.CopyTo(output);
                }
                Verify(staging, payload);
                Directory.Move(staging, destination);
            }
            finally { if (Directory.Exists(staging)) Directory.Delete(staging, true); }
        }
        Verify(destination, payload);
    }
    var mode = args.FirstOrDefault();
    var targetName = mode == "--worker" ? "EmbeddedHelper.Worker.exe" : mode == "--cli" ? "embedded-helper-cli.exe" : "EmbeddedHelper.exe";
    var start = new ProcessStartInfo(Path.Combine(destination, targetName)) { UseShellExecute = false, WorkingDirectory = AppContext.BaseDirectory };
    foreach (var argument in mode is "--worker" or "--cli" ? args[1..] : args) start.ArgumentList.Add(argument);
    using var child = Process.Start(start) ?? throw new IOException("客户端未能启动。");
    child.WaitForExit(); return child.ExitCode;
}
catch (Exception ex)
{
    if (args.Length > 0) Console.Error.WriteLine(ex.Message);
    else Native.MessageBox(IntPtr.Zero, "无法启动。请将 EXE 放在可写目录。若组件被修改，请关闭程序后删除 data/runtime 让程序重新生成（设置和记录不在该目录）。\n\n" + ex.Message, "Embedded Helper", 0x10);
    return 1;
}
static string SafePath(string root, string relative)
{
    var path = Path.GetFullPath(Path.Combine(root, relative));
    if (!path.StartsWith(Path.GetFullPath(root) + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase) || relative.Contains(':')) throw new IOException("组件路径无效。");
    CheckPath(path); return path;
}
static void CheckPath(string path)
{
    for (var current = path; current != null; current = Path.GetDirectoryName(current))
        if ((Directory.Exists(current) || File.Exists(current)) && (File.GetAttributes(current) & FileAttributes.ReparsePoint) != 0) throw new IOException("运行目录包含链接，请使用普通本地目录。");
}
static void Verify(string root, Payload payload)
{
    foreach (var file in payload.Files)
    {
        using var stream = File.OpenRead(SafePath(root, file.Key));
        if (!Convert.ToHexString(SHA256.HashData(stream)).Equals(file.Value, StringComparison.OrdinalIgnoreCase)) throw new IOException("组件被修改：" + file.Key);
    }
}
internal sealed record Payload(string Version, string Sha256, Dictionary<string, string> Files);
internal static class Native
{
    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    internal static extern int MessageBox(IntPtr window, string text, string caption, uint type);
}

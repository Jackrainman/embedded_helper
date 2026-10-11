using System.Diagnostics;
using System.IO.Compression;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;

namespace EmbeddedHelper.Core;

public sealed record ToolRelease(string Version, string Sha256, string Commit)
{
    public const string Repository = "XJU-Hurricane-Team/STM32-Template";
    public string ReleaseTag => "debug-lan-v" + Version;
    public string AssetName => $"DebugLAN-{Version}-windows-x64.zip";
    public string DownloadUrl => $"https://github.com/{Repository}/releases/download/{ReleaseTag}/{AssetName}";
    // Published, independently packaged versions. Offline imports only execute known release bytes.
    public static readonly ToolRelease[] Known = [
        new("0.1.0-alpha.2", "d17e8b3e113b12fff97652a24f88a9262281550f1249978b3fb6d900ce2951b5", "a699c144103f705f5d7e15c2e2107b4d974efee0"),
        new("0.1.0-alpha.1", "565aacf60362a98d1e78dfcfa0623e3044f760b8b6d4d5cb4cd0a51219bd7c5e", "967e8bc17ac258e5fd5926ffa57f083b0abb1431")];
    private static readonly System.Collections.Concurrent.ConcurrentDictionary<string, ToolRelease> discovered = new();
    public static ToolRelease[] Available => Known.Concat(discovered.Values).DistinctBy(r => r.Version).OrderByDescending(r => System.Version.Parse(r.Version.Split('-')[0])).ThenByDescending(r => r.Version, StringComparer.Ordinal).ToArray();
    internal static void Register(ToolRelease release)
    {
        if (!Regex.IsMatch(release.Version, @"^\d+\.\d+\.\d+(?:-[A-Za-z0-9.-]+)?$") || release.Version.Length > 64 || !System.Version.TryParse(release.Version.Split('-')[0], out _) || !Regex.IsMatch(release.Sha256, "^[0-9a-f]{64}$") || !Regex.IsMatch(release.Commit, "^[0-9a-f]{40}$")) throw new InvalidDataException("上游工具版本或摘要格式无效。");
        var known = Known.SingleOrDefault(r => r.Version == release.Version);
        if (known != null) { if (known != release) throw new InvalidDataException("上游已发布版本的内容发生变化，禁止覆盖。"); return; }
        if (discovered.GetOrAdd(release.Version, release) != release) throw new InvalidDataException("同一工具版本存在冲突摘要。");
    }
    public static ToolRelease Find(string version) => Known.SingleOrDefault(r => r.Version == version) ?? (discovered.TryGetValue(version, out var value) ? value : throw new InvalidDataException("未知工具版本；请在设置页刷新官方版本目录，或使用已核验版本。"));
}

public sealed record InstalledTool(string Version, string PackageSha256, string Commit, Dictionary<string, string> Files);

public sealed class ToolCache
{
    private readonly string dataRoot;
    public ToolCache(string dataRoot) { this.dataRoot = Path.GetFullPath(dataRoot); ToolCatalog.Load(this.dataRoot); }
    private string Root => Path.GetFullPath(Path.Combine(dataRoot, "tools", "debug-lan"));
    private static string Digest(string path) { using var stream = File.OpenRead(path); return Convert.ToHexString(SHA256.HashData(stream)).ToLowerInvariant(); }
    public static void ValidateArchive(ZipArchive archive)
    {
        if (archive.Entries.Count is < 1 or > 5000 || archive.Entries.Any(e => e.Length > 512L * 1024 * 1024) || archive.Entries.Sum(e => e.Length) > 512L * 1024 * 1024) throw new InvalidDataException("工具包解压规模超限。");
        var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var entry in archive.Entries)
        {
            var name = entry.FullName.TrimEnd('/');
            var mode = (entry.ExternalAttributes >> 16) & 0xf000;
            if (string.IsNullOrEmpty(name) || !names.Add(name) || name.Contains('\\') || name.StartsWith('/') || mode is not (0 or 0x8000 or 0x4000) || (entry.ExternalAttributes & 0x400) != 0)
                throw new InvalidDataException("工具包包含重复路径、链接或不兼容条目。");
            foreach (var part in name.Split('/'))
                if (part is "" or "." or ".." || part.EndsWith(' ') || part.EndsWith('.') || part.Any(c => char.IsControl(c) || "<>:\"|?*".Contains(c)) || Regex.IsMatch(part, @"^(CON|PRN|AUX|NUL|COM[1-9]|LPT[1-9])(?:\.|$)", RegexOptions.IgnoreCase))
                    throw new InvalidDataException("工具包路径不安全。");
        }
    }
    public async Task<string> InstallAsync(string package, ToolRelease release)
    {
        if (ToolRelease.Find(release.Version) != release) throw new InvalidDataException("工具来源未核验。");
        using var packageStream = new FileStream(package, FileMode.Open, FileAccess.Read, FileShare.Read);
        if (packageStream.Length > 128L * 1024 * 1024 || Convert.ToHexString(SHA256.HashData(packageStream)).ToLowerInvariant() != release.Sha256) throw new InvalidDataException("工具包大小或 SHA-256 与上游发布不一致。");
        packageStream.Position = 0;
        ConfigTransaction.CheckPath(dataRoot, Root);
        Directory.CreateDirectory(Root);
        using var guard = new FileStream(Path.Combine(Root, "install.lock"), FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);
        var destination = Path.Combine(Root, release.Version);
        if (Directory.Exists(destination)) return Resolve(release);
        var staging = Path.Combine(Root, ".install-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(staging);
        try
        {
            using (var archive = new ZipArchive(packageStream, ZipArchiveMode.Read, leaveOpen: true))
            {
                ValidateArchive(archive);
                foreach (var entry in archive.Entries)
                {
                    var path = Path.GetFullPath(Path.Combine(staging, entry.FullName)); ConfigTransaction.CheckPath(staging, path);
                    if (entry.FullName.EndsWith('/')) { Directory.CreateDirectory(path); continue; }
                    Directory.CreateDirectory(Path.GetDirectoryName(path)!);
                    using var output = new FileStream(path, FileMode.CreateNew, FileAccess.Write);
                    using var input = entry.Open(); await input.CopyToAsync(output);
                }
            }
            using var manifest = JsonDocument.Parse(await File.ReadAllTextAsync(Path.Combine(staging, "tool-manifest.json")));
            var m = manifest.RootElement;
            if (m.GetProperty("manifestSchema").GetInt32() != 1 || m.GetProperty("toolId").GetString() != "debug-lan" || m.GetProperty("version").GetString() != release.Version ||
                m.GetProperty("platform").GetString() != "windows" || m.GetProperty("architecture").GetString() != "x64" || m.GetProperty("cliApiMajor").GetInt32() != 1 ||
                m.GetProperty("entrypoint").GetString() != "debug-lan.exe" || m.GetProperty("upstreamCommit").GetString() != release.Commit ||
                m.GetProperty("configSchemaRange").GetProperty("min").GetInt32() > 1 || m.GetProperty("configSchemaRange").GetProperty("max").GetInt32() < 1)
                throw new InvalidDataException("工具清单与版本、平台或CLI协议不兼容。");
            var entrypoint = Path.Combine(staging, "debug-lan.exe");
            var version = await VersionAsync(entrypoint);
            if (version != release.Version) throw new InvalidDataException("工具入口实际版本与清单不一致。");
            var files = Directory.GetFiles(staging, "*", SearchOption.AllDirectories).ToDictionary(p => Path.GetRelativePath(staging, p), Digest);
            PortableData.SaveFile(Path.Combine(staging, "installed.json"), new InstalledTool(release.Version, release.Sha256, release.Commit, files));
            Directory.Move(staging, destination);
            return Resolve(release);
        }
        finally
        {
            // Only this invocation's private directory can be removed, never an installed version.
            if (Directory.Exists(staging)) { ConfigTransaction.CheckPath(Root, staging); Directory.Delete(staging, true); }
        }
    }
    public string Resolve(ToolRelease release)
    {
        var directory = Path.Combine(Root, ToolRelease.Find(release.Version).Version); ConfigTransaction.CheckPath(dataRoot, directory);
        var marker = Path.Combine(directory, "installed.json");
        if (!File.Exists(marker)) throw new FileNotFoundException($"尚未安装 Debug_LAN {release.Version}，请在设置中下载或导入对应ZIP。");
        var installed = JsonSerializer.Deserialize<InstalledTool>(File.ReadAllText(marker), Product.Json) ?? throw new InvalidDataException("安装记录无效。");
        if (installed.PackageSha256 != release.Sha256 || installed.Version != release.Version || installed.Commit != release.Commit || !installed.Files.ContainsKey("debug-lan.exe")) throw new InvalidDataException("安装来源与工程锁不一致。");
        foreach (var file in installed.Files)
        {
            var path = Path.Combine(directory, file.Key); ConfigTransaction.CheckPath(directory, path);
            if (!File.Exists(path) || Digest(path) != file.Value) throw new InvalidDataException("已安装工具文件被修改，请重新准备该版本：" + file.Key);
        }
        return Path.Combine(directory, "debug-lan.exe");
    }
    public static async Task<string> VersionAsync(string executable)
    {
        var start = new ProcessStartInfo(executable) { UseShellExecute = false, RedirectStandardOutput = true, RedirectStandardError = true, CreateNoWindow = true };
        start.ArgumentList.Add("--version");
        using var child = Process.Start(start) ?? throw new IOException("无法启动工具版本检查。");
        var stdout = child.StandardOutput.ReadToEndAsync(); var stderr = child.StandardError.ReadToEndAsync();
        try { await child.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(20)); }
        catch { if (!child.HasExited) { child.Kill(true); await child.WaitForExitAsync(); } throw; }
        if (child.ExitCode != 0) throw new IOException("工具版本检查失败：" + await stderr);
        return (await stdout).Trim();
    }
    public static FileChange SelectVersion(string projectRoot, ToolRelease release)
    {
        var path = Path.Combine(projectRoot, ".embedded_helper", "tools.lock.json"); ConfigTransaction.CheckPath(projectRoot, path);
        var before = File.Exists(path) ? File.ReadAllText(path) : null;
        var text = before ?? "{\"schemaVersion\":1}";
        var root = JsonNode.Parse(text, documentOptions: ProjectReader.JsonOptions)!.AsObject();
        if (root["schemaVersion"]?.GetValue<int>() != 1) throw new InvalidDataException("工具锁协议不兼容。");
        var tools = root["tools"]?.DeepClone().AsObject() ?? new JsonObject();
        var selected = tools["debug-lan"]?.DeepClone().AsObject() ?? new JsonObject();
        selected["version"] = release.Version; selected["sha256"] = release.Sha256; selected["cliApiMajor"] = 1;
        selected["sourceRepository"] = ToolRelease.Repository; selected["releaseTag"] = release.ReleaseTag; selected["assetName"] = release.AssetName;
        tools["debug-lan"] = selected;
        return new FileChange(path, before, JsonEdit.Set(text, "tools", tools));
    }
    public static ToolRelease ReadLock(string projectRoot)
    {
        using var doc = JsonDocument.Parse(File.ReadAllText(Path.Combine(projectRoot, ".embedded_helper", "tools.lock.json")), ProjectReader.JsonOptions);
        var root = doc.RootElement;
        var selected = root.GetProperty("tools").GetProperty("debug-lan");
        var release = ToolRelease.Find(selected.GetProperty("version").GetString()!);
        if (root.GetProperty("schemaVersion").GetInt32() != 1 || selected.GetProperty("sha256").GetString() != release.Sha256 || selected.GetProperty("sourceRepository").GetString() != ToolRelease.Repository ||
            selected.GetProperty("releaseTag").GetString() != release.ReleaseTag || selected.GetProperty("assetName").GetString() != release.AssetName || selected.GetProperty("cliApiMajor").GetInt32() != 1)
            throw new InvalidDataException("工程工具锁的来源、摘要或协议无效。");
        return release;
    }
}

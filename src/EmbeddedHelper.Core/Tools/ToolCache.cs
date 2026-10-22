using System.IO.Compression;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;

namespace EmbeddedHelper.Core;

public sealed record ToolRelease(string Version, string Sha256, string Commit)
{
    public const string Repository = "XJU-Hurricane-Team/LAN_Debug";
    public const string ToolId = "LAN_Debug_client";
    public string ReleaseTag => ToolId + "-v" + Version;
    public string AssetName => $"{ToolId}-{Version}.zip";
    public string DownloadUrl => $"https://github.com/{Repository}/releases/download/{ReleaseTag}/{AssetName}";
    public static readonly ToolRelease[] Known = [new("0.2.0-alpha.1", "025d5ddc703faaf9a2be3674ac3ad00aa7aaaf60c3ff852f83fb0934fcfff8ad", "fc2d23f9739e638f87e2c2aba7926235df3c3a8e")];
    private static readonly System.Collections.Concurrent.ConcurrentDictionary<string, ToolRelease> discovered = new();
    public static ToolRelease[] Available => Known.Concat(discovered.Values).DistinctBy(r => r.Version).OrderByDescending(r => r.Version, Comparer<string>.Create(CompareVersions)).ToArray();
    private static int CompareVersions(string left, string right)
    {
        var a = left.Split('-', 2); var b = right.Split('-', 2);
        var main = System.Version.Parse(a[0]).CompareTo(System.Version.Parse(b[0]));
        if (main != 0) return main;
        if (a.Length != b.Length) return a.Length == 1 ? 1 : -1;
        if (a.Length == 1) return 0;
        var x = a[1].Split('.'); var y = b[1].Split('.');
        for (var i = 0; i < Math.Min(x.Length, y.Length); i++)
        {
            var nx = int.TryParse(x[i], out var vx); var ny = int.TryParse(y[i], out var vy);
            var compare = nx && ny ? vx.CompareTo(vy) : nx != ny ? (nx ? -1 : 1) : StringComparer.Ordinal.Compare(x[i], y[i]);
            if (compare != 0) return compare;
        }
        return x.Length.CompareTo(y.Length);
    }
    internal static void Register(ToolRelease release)
    {
        if (!Regex.IsMatch(release.Version, @"^\d+\.\d+\.\d+(?:-[A-Za-z0-9.-]+)?$") || release.Version.Length > 64 || !System.Version.TryParse(release.Version.Split('-')[0], out _) || !Regex.IsMatch(release.Sha256, "^[0-9a-f]{64}$") || !Regex.IsMatch(release.Commit, "^[0-9a-f]{40}$")) throw new InvalidDataException("客户端版本或摘要格式无效。");
        var known = Known.SingleOrDefault(r => r.Version == release.Version);
        if (known != null) { if (known != release) throw new InvalidDataException("同一已发布客户端版本的内容发生变化。"); return; }
        if (discovered.GetOrAdd(release.Version, release) != release) throw new InvalidDataException("同一客户端版本存在冲突摘要。");
    }
    public static ToolRelease FromManifest(string text)
    {
        using var doc = JsonDocument.Parse(text);
        var m = doc.RootElement;
        if (m.GetProperty("manifestSchema").GetInt32() != 2 || m.GetProperty("toolId").GetString() != ToolId || m.GetProperty("sourceRepository").GetString() != Repository || m.GetProperty("entrypoint").GetString() != "jlink_flash.py" || m.GetProperty("pythonMinimum").GetString() != "3.10") throw new InvalidDataException("不是兼容的 LAN_Debug 客户端脚本包清单。");
        var release = new ToolRelease(m.GetProperty("version").GetString()!, m.GetProperty("packageSha256").GetString()!, m.GetProperty("upstreamCommit").GetString()!);
        if (m.GetProperty("packageName").GetString() != release.AssetName || m.GetProperty("packageBytes").GetInt64() is < 1 or > 2097152) throw new InvalidDataException("客户端包名称或大小无效。");
        Register(release);
        return release;
    }
    public static ToolRelease Find(string version) => Known.SingleOrDefault(r => r.Version == version) ?? (discovered.TryGetValue(version, out var value) ? value : throw new InvalidDataException("未知客户端版本；请刷新 LAN_Debug Release 目录或同时提供发布清单。"));
}

public sealed record InstalledTool(string Version, string PackageSha256, string Commit, Dictionary<string, string> Files);

public sealed class ToolCache
{
    private readonly string dataRoot;
    public ToolCache(string dataRoot) { this.dataRoot = Path.GetFullPath(dataRoot); ToolCatalog.Load(this.dataRoot); }
    private string Root => Path.GetFullPath(Path.Combine(dataRoot, "tools", ToolRelease.ToolId));
    private static string Digest(string path) { using var stream = File.OpenRead(path); return Convert.ToHexString(SHA256.HashData(stream)).ToLowerInvariant(); }
    public static void ValidateArchive(ZipArchive archive)
    {
        if (archive.Entries.Count is < 1 or > 5000 || archive.Entries.Any(e => e.Length > 2L * 1024 * 1024) || archive.Entries.Sum(e => e.Length) > 8L * 1024 * 1024) throw new InvalidDataException("工具包解压规模超限。");
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
        if (packageStream.Length > 2L * 1024 * 1024 || Convert.ToHexString(SHA256.HashData(packageStream)).ToLowerInvariant() != release.Sha256) throw new InvalidDataException("工具包大小或 SHA-256 与上游发布不一致。");
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
            string[] expected = ["jlink_flash.py", "VERSION", "README.md", "remote-jlink.example.json", "vscode/tasks.json", "vscode/launch.json"];
            var actual = Directory.GetFiles(staging, "*", SearchOption.AllDirectories).Select(p => Path.GetRelativePath(staging, p).Replace('\\', '/')).Order().ToArray();
            if (!actual.SequenceEqual(expected.Order()) || File.ReadAllText(Path.Combine(staging, "VERSION")).Trim() != release.Version)
                throw new InvalidDataException("客户端脚本包文件不完整或版本不匹配。");
            foreach (var file in actual) _ = new System.Text.UTF8Encoding(false, true).GetString(File.ReadAllBytes(Path.Combine(staging, file)));
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
        if (!File.Exists(marker)) throw new FileNotFoundException($"尚未准备 LAN_Debug {release.Version}，请在工程页点击导入，或选择对应的官方 ZIP。");
        var installed = JsonSerializer.Deserialize<InstalledTool>(File.ReadAllText(marker), Product.Json) ?? throw new InvalidDataException("安装记录无效。");
        if (installed.PackageSha256 != release.Sha256 || installed.Version != release.Version || installed.Commit != release.Commit || !installed.Files.ContainsKey("jlink_flash.py")) throw new InvalidDataException("安装来源与工程锁不一致。");
        foreach (var file in installed.Files)
        {
            var path = Path.Combine(directory, file.Key); ConfigTransaction.CheckPath(directory, path);
            if (!File.Exists(path) || Digest(path) != file.Value) throw new InvalidDataException("已安装工具文件被修改，请重新准备该版本：" + file.Key);
        }
        return Path.Combine(directory, "jlink_flash.py");
    }
    public static string ReadVersion(string script) => File.ReadAllText(Path.Combine(Path.GetDirectoryName(script)!, "VERSION")).Trim();
    public static FileChange SelectVersion(string projectRoot, ToolRelease release)
    {
        var path = Path.Combine(projectRoot, ".embedded_helper", "tools.lock.json"); ConfigTransaction.CheckPath(projectRoot, path);
        var before = File.Exists(path) ? File.ReadAllText(path) : null;
        var text = before ?? "{\"schemaVersion\":1}";
        var root = JsonNode.Parse(text, documentOptions: ProjectReader.JsonOptions)!.AsObject();
        if (root["schemaVersion"]?.GetValue<int>() != 1) throw new InvalidDataException("工具锁协议不兼容。");
        var tools = root["tools"]?.DeepClone().AsObject() ?? new JsonObject();
        var selected = tools[ToolRelease.ToolId]?.DeepClone().AsObject() ?? new JsonObject();
        selected["version"] = release.Version; selected["sha256"] = release.Sha256; selected["manifestSchema"] = 2;
        selected["sourceRepository"] = ToolRelease.Repository; selected["releaseTag"] = release.ReleaseTag; selected["assetName"] = release.AssetName;
        tools.Remove("debug-lan");
        tools[ToolRelease.ToolId] = selected;
        return new FileChange(path, before, JsonEdit.Set(text, "tools", tools));
    }
    public static ToolRelease ReadLock(string projectRoot)
    {
        using var doc = JsonDocument.Parse(File.ReadAllText(Path.Combine(projectRoot, ".embedded_helper", "tools.lock.json")), ProjectReader.JsonOptions);
        var root = doc.RootElement;
        if (!root.GetProperty("tools").TryGetProperty(ToolRelease.ToolId, out var selected)) throw new InvalidDataException("工程仍使用旧 EXE 工具，请重新导入 LAN_Debug 客户端脚本包；原任务和连接配置已保留。");
        var release = ToolRelease.Find(selected.GetProperty("version").GetString()!);
        if (root.GetProperty("schemaVersion").GetInt32() != 1 || selected.GetProperty("sha256").GetString() != release.Sha256 || selected.GetProperty("sourceRepository").GetString() != ToolRelease.Repository ||
            selected.GetProperty("releaseTag").GetString() != release.ReleaseTag || selected.GetProperty("assetName").GetString() != release.AssetName || selected.GetProperty("manifestSchema").GetInt32() != 2)
            throw new InvalidDataException("工程工具锁的来源、摘要或协议无效。");
        return release;
    }
}

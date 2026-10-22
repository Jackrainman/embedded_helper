using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace EmbeddedHelper.Core;

/// <summary>Apply scripts and configuration through the same existing transaction.</summary>
public static class ProjectToolCache
{
    public const string RelativeEntry = "LAN_Debug/jlink_flash.py";
    private static string Marker(string root) => Path.Combine(root, "LAN_Debug", "installed.json");
    private static string Digest(string path) => Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(path))).ToLowerInvariant();
    public static string Resolve(string root, ToolRelease release)
    {
        var marker = Marker(root); ConfigTransaction.CheckPath(root, marker);
        if (!File.Exists(marker)) throw new FileNotFoundException("工程尚未准备客户端脚本，请一键导入 LAN_Debug。");
        var installed = JsonSerializer.Deserialize<InstalledTool>(File.ReadAllText(marker), Product.Json)!;
        if (installed.Version != release.Version || installed.Commit != release.Commit || installed.PackageSha256 != release.Sha256 || !installed.Files.ContainsKey("jlink_flash.py") || !installed.Files.ContainsKey("VERSION")) throw new InvalidDataException("工程脚本与锁定版本不一致。");
        foreach (var file in installed.Files)
        {
            var path = Path.Combine(root, "LAN_Debug", file.Key); ConfigTransaction.CheckPath(root, path);
            if (!File.Exists(path) || Digest(path) != file.Value) throw new InvalidDataException("工程客户端文件已被修改或缺失：" + file.Key);
        }
        return Path.Combine(root, RelativeEntry);
    }
    public static FileChange[] Plan(string root, string dataRoot, ToolRelease release)
    {
        var source = Path.GetDirectoryName(new ToolCache(dataRoot).Resolve(release))!;
        var marker = Marker(root); ConfigTransaction.CheckPath(root, marker);
        var beforeMarker = File.Exists(marker) ? File.ReadAllText(marker) : null;
        var previous = beforeMarker == null ? null : JsonSerializer.Deserialize<InstalledTool>(beforeMarker, Product.Json);
        var installed = JsonSerializer.Deserialize<InstalledTool>(File.ReadAllText(Path.Combine(source, "installed.json")), Product.Json)!;
        var changes = new List<FileChange>();
        foreach (var file in installed.Files)
        {
            var path = Path.Combine(root, "LAN_Debug", file.Key); ConfigTransaction.CheckPath(root, path);
            var after = new UTF8Encoding(false, true).GetString(File.ReadAllBytes(Path.Combine(source, file.Key)));
            var before = File.Exists(path) ? File.ReadAllText(path) : null;
            if (before != null && before != after && (previous == null || !previous.Files.TryGetValue(file.Key, out var digest) || Digest(path) != digest))
                throw new InvalidDataException("客户端文件存在用户修改，未覆盖：" + path);
            changes.Add(new FileChange(path, before, after));
        }
        changes.Add(new FileChange(marker, beforeMarker, JsonSerializer.Serialize(installed, Product.Json)));
        return changes.Where(c => c.Before != c.After).ToArray();
    }
}

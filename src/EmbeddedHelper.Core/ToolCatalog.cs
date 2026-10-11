using System.Net.Http;
using System.Text.Json;

namespace EmbeddedHelper.Core;

public static class ToolCatalog
{
    private sealed record Catalog(string SourceRepository, ToolRelease[] Releases);
    private static string CatalogPath(string root) => Path.Combine(root, "tools", "official-catalog.json");
    public static void Load(string root)
    {
        var file = CatalogPath(root);
        if (!File.Exists(file)) return;
        if (new FileInfo(file).Length > 1024 * 1024) throw new InvalidDataException("本机工具目录记录过大。");
        var catalog = JsonSerializer.Deserialize<Catalog>(File.ReadAllText(file), Product.Json) ?? throw new InvalidDataException("工具目录记录无效。");
        if (catalog.SourceRepository != ToolRelease.Repository || catalog.Releases.Length > 100) throw new InvalidDataException("工具目录来源不匹配。");
        foreach (var release in catalog.Releases) ToolRelease.Register(release);
    }
    public static async Task<ToolRelease[]> RefreshAsync(string root)
    {
        using var client = new HttpClient { Timeout = TimeSpan.FromSeconds(30) };
        client.DefaultRequestHeaders.UserAgent.ParseAdd("EmbeddedHelper/" + Product.Version);
        using var timeout = new CancellationTokenSource(TimeSpan.FromMinutes(2));
        async Task<JsonDocument> Read(string url, int maximum)
        {
            using var response = await client.GetAsync(url, HttpCompletionOption.ResponseHeadersRead, timeout.Token); response.EnsureSuccessStatusCode();
            await using var input = await response.Content.ReadAsStreamAsync(timeout.Token);
            using var output = new MemoryStream(); var buffer = new byte[8192]; int count;
            while ((count = await input.ReadAsync(buffer, timeout.Token)) > 0)
            { if (output.Length + count > maximum) throw new InvalidDataException("上游元数据超出容量限制。"); await output.WriteAsync(buffer.AsMemory(0, count), timeout.Token); }
            return JsonDocument.Parse(output.ToArray());
        }
        var api = "https://api.github.com/repos/" + ToolRelease.Repository;
        using var releases = await Read(api + "/releases?per_page=100", 2 * 1024 * 1024);
        var candidates = new List<ToolRelease>();
        foreach (var item in releases.RootElement.EnumerateArray())
        {
            var tag = item.GetProperty("tag_name").GetString() ?? "";
            if (item.GetProperty("draft").GetBoolean() || !tag.StartsWith("debug-lan-v", StringComparison.Ordinal)) continue;
            var version = tag[11..];
            if (!System.Text.RegularExpressions.Regex.IsMatch(version, @"^\d+\.\d+\.\d+(?:-[A-Za-z0-9.-]+)?$") || version.Length > 64) continue;
            var asset = item.GetProperty("assets").EnumerateArray().FirstOrDefault(a => a.GetProperty("name").GetString() == $"DebugLAN-{version}-windows-x64.zip");
            if (asset.ValueKind == JsonValueKind.Undefined || !item.GetProperty("assets").EnumerateArray().Any(a => a.GetProperty("name").GetString() == "release-manifest.json")) continue;
            using var manifest = await Read($"https://github.com/{ToolRelease.Repository}/releases/download/{tag}/release-manifest.json", 65536);
            var m = manifest.RootElement;
            if (m.GetProperty("manifestSchema").GetInt32() != 1 || m.GetProperty("toolId").GetString() != "debug-lan" || m.GetProperty("version").GetString() != version || m.GetProperty("platform").GetString() != "windows" || m.GetProperty("architecture").GetString() != "x64" || m.GetProperty("cliApiMajor").GetInt32() != 1) continue;
            var release = new ToolRelease(version, m.GetProperty("packageSha256").GetString()!, m.GetProperty("upstreamCommit").GetString()!);
            if (m.GetProperty("packageName").GetString() != release.AssetName || m.GetProperty("packageBytes").GetInt64() != asset.GetProperty("size").GetInt64() || asset.GetProperty("size").GetInt64() > 128L * 1024 * 1024) throw new InvalidDataException("上游发布附件与清单不一致。");
            if (asset.TryGetProperty("digest", out var digest) && digest.ValueKind == JsonValueKind.String && digest.GetString() != "sha256:" + release.Sha256) throw new InvalidDataException("GitHub附件摘要与清单不一致。");
            using var reference = await Read(api + "/git/ref/tags/" + Uri.EscapeDataString(tag), 65536);
            var referenceObject = reference.RootElement.GetProperty("object");
            var type = referenceObject.GetProperty("type").GetString(); var commit = referenceObject.GetProperty("sha").GetString();
            for (var depth = 0; type == "tag" && depth < 4; depth++)
            {
                using var annotated = await Read(api + "/git/tags/" + commit, 65536);
                var value = annotated.RootElement.GetProperty("object"); type = value.GetProperty("type").GetString(); commit = value.GetProperty("sha").GetString();
            }
            if (type != "commit" || commit != release.Commit) throw new InvalidDataException("上游标签提交与包清单不一致。");
            ToolRelease.Register(release); candidates.Add(release);
        }
        var verified = ToolRelease.Known.Concat(candidates).DistinctBy(r => r.Version).ToArray();
        var path = CatalogPath(root); ConfigTransaction.CheckPath(root, path);
        PortableData.SaveFile(path, new Catalog(ToolRelease.Repository, verified));
        return ToolRelease.Available;
    }
}

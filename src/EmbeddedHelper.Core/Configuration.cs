using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace EmbeddedHelper.Core;

public static class JsonEdit
{
    // Replace one top-level value while retaining unrelated bytes, comments and formatting.
    public static string Set(string text, string key, JsonNode? value)
    {
        using var validation = JsonDocument.Parse(text, ProjectReader.JsonOptions);
        if (validation.RootElement.ValueKind != JsonValueKind.Object || validation.RootElement.EnumerateObject().GroupBy(p => p.Name).Any(g => g.Count() > 1)) throw new InvalidDataException("JSON根对象无效或含重复字段。");
        var bytes = Encoding.UTF8.GetBytes(text);
        var reader = new Utf8JsonReader(bytes, new JsonReaderOptions { AllowTrailingCommas = true, CommentHandling = JsonCommentHandling.Skip });
        var closing = -1;
        while (reader.Read())
        {
            if (reader.TokenType == JsonTokenType.PropertyName && reader.CurrentDepth == 1 && reader.GetString() == key)
            {
                reader.Read();
                var start = (int)reader.TokenStartIndex;
                reader.Skip();
                return Encoding.UTF8.GetString(bytes.AsSpan(0, start)) + (value?.ToJsonString(Product.Json) ?? "null") + Encoding.UTF8.GetString(bytes.AsSpan((int)reader.BytesConsumed));
            }
            if (reader.TokenType == JsonTokenType.EndObject && reader.CurrentDepth == 0) closing = (int)reader.TokenStartIndex;
        }
        if (closing < 0) throw new InvalidDataException("JSON 根节点不是对象。");
        using var document = JsonDocument.Parse(text, ProjectReader.JsonOptions);
        var prefix = Encoding.UTF8.GetString(bytes.AsSpan(0, closing));
        var property = JsonSerializer.Serialize(key) + ": " + (value?.ToJsonString(Product.Json) ?? "null");
        // A trailing comment cannot determine whether a comma already exists; inspect tokens.
        var scan = new Utf8JsonReader(bytes, new JsonReaderOptions { AllowTrailingCommas = true, CommentHandling = JsonCommentHandling.Skip });
        long endOfLastValue = 1;
        while (scan.Read())
            if (scan.TokenType == JsonTokenType.PropertyName && scan.CurrentDepth == 1) { scan.Read(); scan.Skip(); endOfLastValue = scan.BytesConsumed; }
        var tail = Encoding.UTF8.GetString(bytes.AsSpan((int)endOfLastValue, closing - (int)endOfLastValue));
        var hasComma = tail.TrimStart().StartsWith(',');
        var separator = document.RootElement.EnumerateObject().Any() && !hasComma ? "," : "";
        if (separator.Length > 0) prefix = Encoding.UTF8.GetString(bytes.AsSpan(0, (int)endOfLastValue)) + "," + tail;
        return prefix + "\n  " + property + "\n" + Encoding.UTF8.GetString(bytes.AsSpan(closing));
    }
}

public sealed record FileChange(string Path, string? Before, string After);
public sealed record ConfigPlan(string Root, FileChange[] Changes);
public static class ConfigTransaction
{
    public static string[] Pending(string journalRoot)
    {
        if (!Directory.Exists(journalRoot)) return [];
        return Directory.GetFiles(journalRoot, "*.json").Where(path =>
        {
            using var doc = JsonDocument.Parse(File.ReadAllText(path));
            return doc.RootElement.GetProperty("status").GetString() is "prepared" or "conflict";
        }).ToArray();
    }
    public static string[] RecoverIncomplete(string root, string journalRoot)
    {
        Directory.CreateDirectory(journalRoot);
        using var guard = new FileStream(Path.Combine(journalRoot, "write.lock"), FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);
        var allConflicts = new List<string>();
        foreach (var journal in Pending(journalRoot))
        {
            using var doc = JsonDocument.Parse(File.ReadAllText(journal));
            var plan = doc.RootElement.GetProperty("plan").Deserialize<ConfigPlan>(Product.Json) ?? throw new InvalidDataException("事务记录无效。");
            if (!Path.GetFullPath(plan.Root).Equals(Path.GetFullPath(root), StringComparison.OrdinalIgnoreCase)) throw new InvalidDataException("事务工程身份不匹配。");
            var conflicts = new List<string>();
            foreach (var change in plan.Changes.Reverse())
            {
                CheckPath(root, change.Path);
                var current = File.Exists(change.Path) ? File.ReadAllText(change.Path) : null;
                if (current == change.Before) continue;
                if (current != change.After) { conflicts.Add(change.Path); continue; }
                if (change.Before == null) File.Delete(change.Path); else AtomicText(change.Path, change.Before);
            }
            PortableData.SaveFile(journal, new { status = conflicts.Count == 0 ? "rolled-back" : "conflict", conflicts, plan });
            allConflicts.AddRange(conflicts);
        }
        return allConflicts.ToArray();
    }
    public static string Hash(string? text) => text == null ? "missing" : Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(text)));
    public static void Apply(ConfigPlan plan, string journalRoot, Action<int>? afterWrite = null)
    {
        foreach (var change in plan.Changes) CheckPath(plan.Root, change.Path);
        Directory.CreateDirectory(journalRoot);
        using var guard = new FileStream(Path.Combine(journalRoot, "write.lock"), FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);
        var journal = Path.Combine(journalRoot, Guid.NewGuid().ToString("N") + ".json");
        if (Pending(journalRoot).Length > 0) throw new IOException("存在未完成事务，请先恢复或处理外部修改。");
        foreach (var change in plan.Changes) CheckBefore(change);
        PortableData.SaveFile(journal, new { status = "prepared", plan });
        var written = new List<FileChange>();
        try
        {
            foreach (var change in plan.Changes)
            {
                CheckPath(plan.Root, change.Path);
                CheckBefore(change);
                AtomicText(change.Path, change.After);
                written.Add(change);
                afterWrite?.Invoke(written.Count);
            }
            if (written.Any(c => File.ReadAllText(c.Path) != c.After)) throw new IOException("写入后文件被外部修改。");
            PortableData.SaveFile(journal, new { status = "committed", plan });
        }
        catch (Exception ex)
        {
            var conflicts = new List<string>();
            foreach (var change in written.AsEnumerable().Reverse())
            {
                try
                {
                    if (!File.Exists(change.Path) || File.ReadAllText(change.Path) != change.After) { conflicts.Add(change.Path); continue; }
                    if (change.Before == null) File.Delete(change.Path); else AtomicText(change.Path, change.Before);
                }
                catch (IOException) { conflicts.Add(change.Path); }
            }
            PortableData.SaveFile(journal, new { status = conflicts.Count == 0 ? "rolled-back" : "conflict", conflicts, plan });
            throw new IOException(conflicts.Count == 0 ? "应用失败，已恢复原文件：" + ex.Message : "应用失败，存在外部修改，请按事务备份处理：" + journal, ex);
        }
    }
    public static void CheckPath(string root, string path)
    {
        var fullRoot = Path.GetFullPath(root);
        var fullPath = Path.GetFullPath(path);
        if (!fullPath.StartsWith(fullRoot.TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase)) throw new IOException("配置路径超出工程。");
        for (var current = fullPath; current != null; current = Path.GetDirectoryName(current))
            if ((Directory.Exists(current) || File.Exists(current)) && (File.GetAttributes(current) & FileAttributes.ReparsePoint) != 0) throw new IOException("配置路径含目录联接或符号链接，已停止写入。");
    }
    private static void CheckBefore(FileChange change)
    {
        var current = File.Exists(change.Path) ? File.ReadAllText(change.Path) : null;
        if (Hash(current) != Hash(change.Before)) throw new IOException("文件已被外部修改，请重新生成计划：" + change.Path);
    }
    private static void AtomicText(string path, string text)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        var temp = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try { File.WriteAllText(temp, text, new UTF8Encoding(false)); File.Move(temp, path, true); }
        finally { if (File.Exists(temp)) File.Delete(temp); }
    }
}

public static class RemoteConfiguration
{
    public static ConfigPlan Plan(ProjectInfo project, BuildTarget target, string host, int port, string? webAddress = null)
    {
        if (project.Family is not ("G4" or "H7" or "F4" or "F1")) throw new InvalidDataException("该系列尚未完成配置适配，保留只读。");
        if (!string.IsNullOrEmpty(webAddress) && (!Uri.TryCreate(webAddress, UriKind.Absolute, out var uri) || uri.Scheme is not ("http" or "https"))) throw new ArgumentException("网页地址须为完整HTTP(S)地址或留空。");
        if (string.IsNullOrWhiteSpace(host) || host.Any(char.IsWhiteSpace) || Uri.CheckHostName(host) == UriHostNameType.Unknown || port is < 1 or > 65535)
            throw new ArgumentException("请填写主机名/IP 和 1～65535 的探针端口。");
        if (string.IsNullOrWhiteSpace(target.Device) || target.Interface == "未知" || target.SpeedKHz <= 0) throw new InvalidDataException("连接参数不完整。");
        if (project.Diagnostics.Any(d => d.Contains("多份远程配置"))) throw new InvalidDataException("远程配置存在歧义。");
        var before = File.Exists(project.RemotePath) ? File.ReadAllText(project.RemotePath) : null;
        var text = before ?? "{}";
        foreach (var (key, value) in new Dictionary<string, JsonNode?> { ["ServerName"] = JsonValue.Create(host), ["Port"] = JsonValue.Create(port), ["Device"] = JsonValue.Create(target.Device), ["Interface"] = JsonValue.Create(target.Interface), ["SpeedKHz"] = JsonValue.Create(target.SpeedKHz) }) text = JsonEdit.Set(text, key, value);
        if (before == null) text = JsonEdit.Set(text, "JLinkExe", JsonValue.Create(""));
        if (webAddress != null) text = JsonEdit.Set(text, "WebUrl", JsonValue.Create(webAddress));
        using var parsed = JsonDocument.Parse(text, ProjectReader.JsonOptions);
        return new ConfigPlan(project.Root, [new FileChange(project.RemotePath, before, text)]);
    }
}

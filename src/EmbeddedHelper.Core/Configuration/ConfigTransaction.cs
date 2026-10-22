using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace EmbeddedHelper.Core;

public sealed record ConflictClosure(string Root, string Journal, string JournalHash, Dictionary<string, string> FileHashes);

public static class ConfigTransaction
{
    private static JsonDocument ReadJournal(string path)
    {
        JsonDocument? doc = null;
        try
        {
            doc = JsonDocument.Parse(File.ReadAllText(path));
            if (!doc.RootElement.TryGetProperty("status", out var status) || status.ValueKind != JsonValueKind.String)
                throw new InvalidDataException("缺少 status。");
            if (status.GetString() is not ("prepared" or "conflict" or "committed" or "rolled-back" or "accepted-current"))
                throw new InvalidDataException("未知事务状态。");
            if (!doc.RootElement.TryGetProperty("plan", out var node)) throw new InvalidDataException("缺少 plan。");
            var plan = node.Deserialize<ConfigPlan>(Product.Json);
            if (plan == null || string.IsNullOrWhiteSpace(plan.Root) || !Path.IsPathFullyQualified(plan.Root) || plan.Changes == null ||
                plan.Changes.Any(c => c == null || string.IsNullOrWhiteSpace(c.Path) || !Path.IsPathFullyQualified(c.Path) || c.After == null))
                throw new InvalidDataException("事务计划字段无效。");
            return doc;
        }
        catch (Exception ex) when (ex is JsonException or IOException or InvalidDataException or UnauthorizedAccessException or InvalidOperationException or ArgumentException)
        { doc?.Dispose(); throw new InvalidDataException("事务记录不可读：" + path + "；" + ex.Message, ex); }
    }
    public static ConflictClosure PreviewKeepCurrent(string root, string journal)
    {
        using var doc = ReadJournal(journal);
        if (doc.RootElement.GetProperty("status").GetString() != "conflict") throw new IOException("请先恢复未完成写入，仅冲突记录可保留现状结案。");
        var plan = doc.RootElement.GetProperty("plan").Deserialize<ConfigPlan>(Product.Json) ?? throw new InvalidDataException("事务记录无效：" + journal);
        CheckRoot(plan);
        if (!Path.GetFullPath(plan.Root).Equals(Path.GetFullPath(root), StringComparison.OrdinalIgnoreCase)) throw new InvalidDataException("事务工程身份不匹配：" + journal);
        var hashes = new Dictionary<string, string>();
        foreach (var change in plan.Changes)
        {
            CheckPath(root, change.Path);
            hashes.Add(change.Path, Hash(File.Exists(change.Path) ? File.ReadAllText(change.Path) : null));
        }
        return new(root, journal, Hash(File.ReadAllText(journal)), hashes);
    }
    public static void KeepCurrent(ConflictClosure preview, string journalRoot)
    {
        if (!Path.GetFullPath(Path.GetDirectoryName(preview.Journal)!).Equals(Path.GetFullPath(journalRoot), StringComparison.OrdinalIgnoreCase)) throw new InvalidDataException("事务日志目录不匹配。");
        using var guard = new FileStream(Path.Combine(journalRoot, "write.lock"), FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);
        var current = PreviewKeepCurrent(preview.Root, preview.Journal);
        if (current.JournalHash != preview.JournalHash || current.FileHashes.Count != preview.FileHashes.Count || current.FileHashes.Any(p => preview.FileHashes.GetValueOrDefault(p.Key) != p.Value))
            throw new IOException("预览后文件或事务记录已变化，请重新确认保留现状。");
        var journal = JsonNode.Parse(File.ReadAllText(preview.Journal))!.AsObject();
        journal["status"] = "accepted-current";
        journal["resolution"] = JsonSerializer.SerializeToNode(new { method = "explicit-keep-current", at = DateTimeOffset.UtcNow, current.FileHashes }, Product.Json);
        PortableData.SaveFile(preview.Journal, journal);
    }
    public static string[] Pending(string journalRoot)
    {
        if (!Directory.Exists(journalRoot)) return [];
        return Directory.GetFiles(journalRoot, "*.json").Where(path =>
        {
            using var doc = ReadJournal(path);
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
            using var doc = ReadJournal(journal);
            var plan = doc.RootElement.GetProperty("plan").Deserialize<ConfigPlan>(Product.Json) ?? throw new InvalidDataException("事务记录无效。");
            CheckRoot(plan);
            if (!Path.GetFullPath(plan.Root).Equals(Path.GetFullPath(root), StringComparison.OrdinalIgnoreCase)) throw new InvalidDataException("事务工程身份不匹配。");
            var conflicts = new List<string>();
            foreach (var change in plan.Changes.Reverse())
            {
                CheckRoot(plan);
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
        CheckRoot(plan);
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
                CheckRoot(plan);
                CheckPath(plan.Root, change.Path);
                CheckBefore(change);
                AtomicText(change.Path, change.After);
                written.Add(change);
                afterWrite?.Invoke(written.Count);
            }
            foreach (var change in written)
            {
                CheckRoot(plan);
                CheckPath(plan.Root, change.Path);
                if (File.ReadAllText(change.Path) != change.After) throw new IOException("写入后文件被外部修改。");
            }
            PortableData.SaveFile(journal, new { status = "committed", plan });
        }
        catch (Exception ex)
        {
            var conflicts = new List<string>();
            foreach (var change in written.AsEnumerable().Reverse())
            {
                try
                {
                    CheckRoot(plan);
                    CheckPath(plan.Root, change.Path);
                    if (!File.Exists(change.Path) || File.ReadAllText(change.Path) != change.After) { conflicts.Add(change.Path); continue; }
                    if (change.Before == null) File.Delete(change.Path); else AtomicText(change.Path, change.Before);
                }
                catch (Exception rollbackError) when (rollbackError is IOException or UnauthorizedAccessException) { conflicts.Add(change.Path); }
            }
            PortableData.SaveFile(journal, new { status = conflicts.Count == 0 ? "rolled-back" : "conflict", conflicts, plan });
            throw new IOException(conflicts.Count == 0 ? "应用失败，已恢复原文件：" + ex.Message : "应用失败，存在外部修改，请按事务备份处理：" + journal, ex);
        }
    }
    internal static string ResolveRoot(string root)
    {
        var full = Path.GetFullPath(root);
        var current = Path.GetPathRoot(full)!;
        foreach (var part in full[current.Length..].Split(Path.DirectorySeparatorChar, StringSplitOptions.RemoveEmptyEntries))
        {
            current = Path.Combine(current, part);
            if (Directory.Exists(current) && (File.GetAttributes(current) & FileAttributes.ReparsePoint) != 0)
                current = new DirectoryInfo(current).ResolveLinkTarget(true)?.FullName ?? throw new IOException("无法解析工程根目录联接。");
        }
        return Path.TrimEndingDirectorySeparator(Path.GetFullPath(current));
    }
    private static void CheckRoot(ConfigPlan plan)
    {
        if (!ResolveRoot(plan.Root).Equals(plan.ResolvedRoot, StringComparison.OrdinalIgnoreCase))
            throw new IOException("工程根目录联接已变化，请重新打开工程并预览；原事务备份已保留。");
    }
    public static void CheckPath(string root, string path)
    {
        var fullRoot = Path.TrimEndingDirectorySeparator(Path.GetFullPath(root));
        var fullPath = Path.GetFullPath(path);
        if (!fullPath.StartsWith(fullRoot.TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase)) throw new IOException("配置路径超出工程。");
        for (var current = fullPath; current != null; current = Path.GetDirectoryName(current))
        {
            if ((Directory.Exists(current) || File.Exists(current)) && (File.GetAttributes(current) & FileAttributes.ReparsePoint) != 0) throw new IOException("配置路径含目录联接或符号链接，已停止写入。");
            // The chosen root is the boundary; a junction above it is a supported access path.
            if (current.Equals(fullRoot, StringComparison.OrdinalIgnoreCase)) break;
        }
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

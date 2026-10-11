using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace EmbeddedHelper.Core;

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

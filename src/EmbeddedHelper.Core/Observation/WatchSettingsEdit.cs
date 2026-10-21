using System.Text.Json;

namespace EmbeddedHelper.Core;

/// <summary>An edit is based on the file the user loaded, not a newly read replacement.</summary>
public sealed class WatchSettingsEdit
{
    public string Root { get; }
    private string PathName => Path.Combine(Root, ".embedded_helper", "watch.json");
    private string? original;
    public WatchSettings? Value { get; private set; }
    public WatchSettingsEdit(string root)
    {
        Root = Path.GetFullPath(root);
        original = Read();
        Value = original == null ? null : JsonSerializer.Deserialize<WatchSettings>(original, Product.Json) ?? throw new InvalidDataException("变量设置文件为空。");
    }
    private string? Read() => File.Exists(PathName) ? File.ReadAllText(PathName) : null;
    public WatchSettings Save(string target, string[] identities, int rate)
    {
        if (string.IsNullOrWhiteSpace(target) || rate is < 1 or > 1000 || identities.Length > 64 || identities.Any(string.IsNullOrWhiteSpace) || identities.Distinct(StringComparer.Ordinal).Count() != identities.Length)
            throw new InvalidDataException("采样频率须为 1～1000 Hz，变量最多 64 项且不能重复。");
        if (Read() != original) throw new InvalidDataException("工程变量设置已被其他窗口修改；请重新读取后再编辑，原文件未覆盖。");
        var previous = Value?.Target == target ? Value : null;
        var next = new WatchSettings(target, (string[])identities.Clone(), rate)
        { Transport = previous?.Transport ?? "local", ProbeSerial = previous?.ProbeSerial ?? "", EnableRtt = false };
        WatchSettingsStore.Save(Root, next);
        var written = Read();
        if (written != JsonSerializer.Serialize(next, Product.Json)) throw new IOException("变量设置写入后回读不一致，请重新读取。");
        original = written; Value = next;
        return next;
    }
}

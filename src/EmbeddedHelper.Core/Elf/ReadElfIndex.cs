using System.Diagnostics;
using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;

namespace EmbeddedHelper.Core;

public static partial class ReadElfIndex
{
    private sealed record Entry(ulong Offset, string Tag, string Scope, Dictionary<string, string> Attributes);
    public static string? FindTool()
    {
        var eide = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".eide", "tools", "gcc_arm", "bin", "arm-none-eabi-readelf.exe");
        return File.Exists(eide) ? eide : null;
    }
    public static Variable[] Read(string tool, string elf)
    {
        var start = new ProcessStartInfo(Path.GetFullPath(tool)) { UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true };
        start.ArgumentList.Add("--debug-dump=info"); start.ArgumentList.Add("--wide"); start.ArgumentList.Add(Path.GetFullPath(elf));
        start.Environment["LC_ALL"] = "C";
        using var process = Process.Start(start) ?? throw new IOException("无法启动 ARM readelf。");
        var output = ReadBoundedAsync(process.StandardOutput, 64 * 1024 * 1024);
        var errors = ReadBoundedAsync(process.StandardError, 1024 * 1024);
        try
        {
            Task.WhenAll(process.WaitForExitAsync(), output, errors).WaitAsync(TimeSpan.FromSeconds(30)).GetAwaiter().GetResult();
            if (process.ExitCode != 0) throw new InvalidDataException("ARM readelf 解析失败：" + errors.Result);
            return Parse(output.Result);
        }
        catch { if (!process.HasExited) { process.Kill(true); process.WaitForExit(); } throw; }
    }
    private static async Task<string> ReadBoundedAsync(StreamReader reader, int limit)
    {
        var text = new StringBuilder(); var buffer = new char[8192];
        while (await reader.ReadAsync(buffer) is var count && count > 0)
        { if (text.Length + count > limit) throw new InvalidDataException("readelf 输出超过上限。"); text.Append(buffer, 0, count); }
        return text.ToString();
    }
    public static Variable[] Parse(string text)
    {
        var entries = new Dictionary<ulong, Entry>();
        Entry? current = null;
        var scopes = new Dictionary<int, string>();
        string cu = "";
        foreach (var line in text.Split('\n'))
        {
            var header = Header().Match(line);
            if (header.Success)
            {
                var depth = int.Parse(header.Groups[1].Value, CultureInfo.InvariantCulture);
                foreach (var level in scopes.Keys.Where(k => k >= depth).ToArray()) scopes.Remove(level);
                var offset = ulong.Parse(header.Groups[2].Value, NumberStyles.HexNumber, CultureInfo.InvariantCulture);
                var tag = header.Groups[3].Value;
                if (depth == 0) cu = "";
                current = new Entry(offset, tag, string.Join("/", scopes.OrderBy(p => p.Key).Select(p => p.Value)), new());
                entries[offset] = current;
                scopes[depth] = tag + "@" + offset.ToString("x", CultureInfo.InvariantCulture);
                continue;
            }
            if (line.TrimStart().StartsWith('<') && line.Contains("Abbrev Number: 0")) { current = null; continue; }
            if (current == null) continue;
            var attribute = Attribute().Match(line);
            if (!attribute.Success) continue;
            var key = attribute.Groups[1].Value; var value = attribute.Groups[2].Value.Trim();
            current.Attributes[key] = value;
            if (current.Tag == "compile_unit" && key == "name") cu = Name(value).Replace('\\', '/');
            current.Attributes["cu"] = cu;
            if (key == "name" && current.Tag is "compile_unit" or "subprogram" or "namespace")
            {
                var level = scopes.FirstOrDefault(p => p.Value.EndsWith("@" + current.Offset.ToString("x", CultureInfo.InvariantCulture))).Key;
                scopes[level] = Name(value);
            }
        }
        var result = new List<Variable>();
        foreach (var item in entries.Values.Where(e => e.Tag == "variable"))
        {
            var location = Location().Match(item.Attributes.GetValueOrDefault("location", ""));
            if (!location.Success || !item.Attributes.TryGetValue("name", out var rawName)) continue;
            Entry? type = item;
            var visited = new HashSet<ulong>();
            while (type != null && visited.Add(type.Offset) && type.Tag is "variable" or "typedef" or "const_type" or "volatile_type" or "restrict_type")
            {
                var reference = Reference().Match(type.Attributes.GetValueOrDefault("type", ""));
                type = reference.Success && entries.TryGetValue(ulong.Parse(reference.Groups[1].Value, NumberStyles.HexNumber, CultureInfo.InvariantCulture), out var target) ? target : null;
            }
            var size = Number(type?.Attributes.GetValueOrDefault("byte_size", "") ?? "");
            var encoding = Number(type?.Attributes.GetValueOrDefault("encoding", "") ?? "");
            var scalar = type?.Tag == "base_type" ? (encoding, size) switch
            { (5 or 6, 1) => "int8", (7 or 8, 1) => "uint8", (5, 2) => "int16", (7, 2) => "uint16", (5, 4) => "int32", (7, 4) => "uint32", (4, 4) => "float32", _ => "不支持" } : "不支持";
            var name = Name(rawName);
            var external = Number(item.Attributes.GetValueOrDefault("external", "")) == 1;
            var identity = external ? "global:" + name : item.Attributes.GetValueOrDefault("cu", "") + ":" + item.Scope + ":" + name;
            result.Add(new Variable(identity, name, ulong.Parse(location.Groups[1].Value, NumberStyles.HexNumber, CultureInfo.InvariantCulture), scalar, size, scalar == "不支持" ? "类型尚不支持" : "可读取"));
        }
        if (result.Count == 0) throw new InvalidDataException("readelf 未找到固定地址变量。");
        return result.DistinctBy(v => v.Identity).OrderBy(v => v.Name, StringComparer.Ordinal).ToArray();
    }
    private static string Name(string value)
    {
        var marker = value.LastIndexOf("): ", StringComparison.Ordinal);
        if (marker >= 0) return value[(marker + 3)..];
        return Regex.Replace(value, @"^\([^)]*\)\s*", "");
    }
    private static int Number(string value)
    { var match = Numeric().Match(value); return match.Success ? int.Parse(match.Groups[1].Value, CultureInfo.InvariantCulture) : 0; }
    [GeneratedRegex(@"^\s*<(\d+)><([0-9a-f]+)>:.*\(DW_TAG_([a-z_]+)\)")] private static partial Regex Header();
    [GeneratedRegex(@"DW_AT_([a-z_]+)\s*:\s*(.*)$")] private static partial Regex Attribute();
    [GeneratedRegex(@"\(DW_OP_addr: (?:0x)?([0-9a-f]+)\)\s*$")] private static partial Regex Location();
    [GeneratedRegex(@"<0x([0-9a-f]+)>")] private static partial Regex Reference();
    [GeneratedRegex(@"^(?:\([^)]*\)\s*)?(\d+)")] private static partial Regex Numeric();
}

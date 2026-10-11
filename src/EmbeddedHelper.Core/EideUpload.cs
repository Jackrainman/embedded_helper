using System.Text.Json;
using YamlDotNet.Core;
using YamlDotNet.Core.Events;
using YamlDotNet.RepresentationModel;

namespace EmbeddedHelper.Core;

public sealed record ManagedYamlField(string[] Keys, string? Before, string After, bool CreatedParent = false);
public sealed record EideUploadChange(FileChange Change, ManagedYamlField[] Fields);

public static class EideUpload
{
    public const string Command = "\"%EMBEDDED_HELPER_HOME%\\embedded-helper-cli.exe\" --project \"${ProjectRoot}\" flash --program \"${programFile}\"";
    public static EideUploadChange Plan(string root, string target, bool enabled, ManagedYamlField[] previous)
    {
        var path = Path.Combine(root, ".eide", "eide.yml"); ConfigTransaction.CheckPath(root, path);
        var before = File.ReadAllText(path); var text = before;
        foreach (var field in previous.Reverse())
        {
            if (Get(text, field.Keys) != field.After) throw new InvalidDataException("EIDE接入字段已被外部修改，保留原值：" + string.Join('/', field.Keys));
            text = Set(text, field.Keys, field.Before);
        }
        foreach (var field in previous.Where(f => f.CreatedParent))
        {
            var keys = field.Keys[..^1]; var node = Find(text, keys);
            if (node is YamlScalarNode scalar && string.IsNullOrEmpty(scalar.Value)) text = Set(text, keys, null);
        }
        var fields = new List<ManagedYamlField>();
        if (enabled)
        {
            if (root.Any(c => "%!^&|<>\"\r\n".Contains(c))) throw new InvalidDataException("此路径包含EIDE命令解释器的特殊字符，请使用普通或中文空格目录，或保留VS Code独立入口。");
            var createdParent = Find(text, ["targets", target, "uploadConfigMap", "Custom"]) == null;
            foreach (var (keys, value) in new[]
            {
                (new[] { "targets", target, "uploader" }, "Custom"),
                (new[] { "targets", target, "uploadConfigMap", "Custom", "bin" }, ""),
                (new[] { "targets", target, "uploadConfigMap", "Custom", "commandLine" }, Command),
                (new[] { "targets", target, "uploadConfigMap", "Custom", "eraseChipCommand" }, "")
            })
            {
                var original = Get(text, keys); var after = JsonSerializer.Serialize(value);
                fields.Add(new ManagedYamlField(keys, original, after, createdParent && keys[^1] == "bin")); text = Set(text, keys, after);
            }
        }
        return new EideUploadChange(new FileChange(path, before, text), fields.ToArray());
    }
    private static YamlMappingNode Parse(string text)
    {
        var yaml = new YamlStream(); yaml.Load(new StringReader(text));
        if (yaml.Documents.Count != 1 || yaml.Documents[0].RootNode is not YamlMappingNode root) throw new InvalidDataException("EIDE需要单一YAML映射。");
        return root;
    }
    public static string? Get(string text, string[] keys)
    {
        var node = Find(text, keys);
        if (node == null) return null;
        if (node is not YamlScalarNode scalar || scalar.Style is ScalarStyle.Literal or ScalarStyle.Folded || !node.Anchor.IsEmpty) throw new InvalidDataException("EIDE字段使用复杂YAML，请先手动处理。");
        return text[(int)node.Start.Index..(int)node.End.Index];
    }
    private static YamlNode? Find(string text, string[] keys)
    {
        YamlNode node = Parse(text);
        foreach (var key in keys)
        {
            if (node is not YamlMappingNode mapping || !mapping.Children.TryGetValue(new YamlScalarNode(key), out var child)) return null;
            node = child;
        }
        return node;
    }
    private static int LineStart(string text, int position) => position == 0 ? 0 : text.LastIndexOf('\n', Math.Min(position - 1, text.Length - 1)) + 1;
    public static string Set(string text, string[] keys, string? raw)
    {
        var mapping = Parse(text);
        for (var index = 0; index < keys.Length; index++)
        {
            if (mapping.Style == MappingStyle.Flow || !mapping.Anchor.IsEmpty) throw new InvalidDataException("EIDE映射使用流式或锚点语法，未修改。");
            var pair = mapping.Children.FirstOrDefault(p => p.Key is YamlScalarNode key && key.Value == keys[index]);
            if (pair.Key == null)
            {
                if (raw == null) return text;
                var indent = mapping.Children.Count > 0 ? ((int)mapping.Children.First().Key.Start.Column - 1) : ((int)mapping.Start.Column - 1);
                var position = (int)mapping.End.Index;
                var lineStart = LineStart(text, position);
                if (text[lineStart..position].All(char.IsWhiteSpace)) position = lineStart;
                var newline = text.Contains("\r\n") ? "\r\n" : "\n";
                var addition = position > 0 && text[position - 1] != '\n' ? newline : "";
                for (var remaining = index; remaining < keys.Length; remaining++)
                    addition += new string(' ', indent + 2 * (remaining - index)) + JsonSerializer.Serialize(keys[remaining]) + ":" + (remaining == keys.Length - 1 ? " " + raw : "") + newline;
                var result = text[..position] + addition + text[position..];
                if (Get(result, keys) != raw) throw new InvalidDataException("EIDE字段插入校验失败，未写入。");
                return result;
            }
            if (index < keys.Length - 1)
            {
                if (pair.Value is YamlScalarNode empty && string.IsNullOrEmpty(empty.Value))
                {
                    if (raw == null) return text;
                    var newline = text.Contains("\r\n") ? "\r\n" : "\n";
                    var end = text.IndexOf('\n', (int)pair.Key.Start.Index); var position = end < 0 ? text.Length : end + 1;
                    var addition = end < 0 ? newline : "";
                    for (var remaining = index + 1; remaining < keys.Length; remaining++)
                        addition += new string(' ', ((int)pair.Key.Start.Column - 1) + 2 * (remaining - index)) + JsonSerializer.Serialize(keys[remaining]) + ":" + (remaining == keys.Length - 1 ? " " + raw : "") + newline;
                    return text[..position] + addition + text[position..];
                }
                mapping = pair.Value as YamlMappingNode ?? throw new InvalidDataException("EIDE字段层级不是映射。"); continue;
            }
            _ = Get(text, keys);
            if (raw != null) return text[..(int)pair.Value.Start.Index] + raw + text[(int)pair.Value.End.Index..];
            var start = LineStart(text, (int)pair.Key.Start.Index);
            var lineEnd = text.IndexOf('\n', (int)pair.Key.Start.Index); if (lineEnd < 0) lineEnd = text.Length;
            var endPosition = lineEnd < text.Length ? lineEnd + 1 : lineEnd;
            var suffix = (int)pair.Value.End.Index <= lineEnd ? text[(int)pair.Value.End.Index..lineEnd].Trim() : "";
            var comment = suffix.StartsWith('#') ? new string(' ', ((int)pair.Key.Start.Column - 1)) + suffix + "\n" : "";
            return text[..start] + comment + text[endPosition..];
        }
        throw new InvalidDataException("空EIDE字段路径。");
    }
}

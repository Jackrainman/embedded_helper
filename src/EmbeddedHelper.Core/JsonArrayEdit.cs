using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace EmbeddedHelper.Core;

public static class JsonArrayEdit
{
    private sealed record Span(int Start, int End, JsonObject Value);
    public static string Change(string text, string array, string key, string identity, JsonObject? value, JsonObject? expected)
    {
        using var document = JsonDocument.Parse(text, ProjectReader.JsonOptions);
        if (document.RootElement.ValueKind != JsonValueKind.Object || document.RootElement.EnumerateObject().GroupBy(p => p.Name).Any(g => g.Count() > 1)) throw new InvalidDataException("JSON根对象无效或含重复字段。");
        if (!document.RootElement.TryGetProperty(array, out var arrayNode))
        {
            if (expected != null) throw new InvalidDataException("管理的数组已被外部删除：" + array);
            return value == null ? text : JsonEdit.Set(text, array, new JsonArray(value.DeepClone()));
        }
        if (arrayNode.ValueKind != JsonValueKind.Array) throw new InvalidDataException("字段不是数组：" + array);
        var bytes = Encoding.UTF8.GetBytes(text);
        var reader = new Utf8JsonReader(bytes, new JsonReaderOptions { AllowTrailingCommas = true, CommentHandling = JsonCommentHandling.Skip });
        var entries = new List<Span>(); int close = -1, open = -1;
        while (reader.Read())
        {
            if (reader.TokenType != JsonTokenType.PropertyName || reader.CurrentDepth != 1 || reader.GetString() != array) continue;
            reader.Read(); open = (int)reader.TokenStartIndex;
            while (reader.Read() && reader.TokenType != JsonTokenType.EndArray)
            {
                var start = (int)reader.TokenStartIndex; reader.Skip();
                var end = (int)reader.BytesConsumed;
                var node = JsonNode.Parse(bytes.AsSpan(start, end - start), documentOptions: ProjectReader.JsonOptions) as JsonObject ?? throw new InvalidDataException("配置数组含非对象条目。");
                entries.Add(new Span(start, end, node));
            }
            close = (int)reader.TokenStartIndex; break;
        }
        if (close < 0) throw new InvalidDataException("无法定位配置数组。");
        var found = entries.Where(e => e.Value[key]?.GetValue<string>() == identity).ToArray();
        if (found.Length > 1) throw new InvalidDataException("存在同名配置条目：" + identity);
        var existing = found.SingleOrDefault();
        if (expected == null && existing != null) throw new InvalidDataException("同名条目不属于本产品，保留原项：" + identity);
        if (expected != null && (existing == null || !JsonNode.DeepEquals(existing.Value, expected))) throw new InvalidDataException("管理条目已被外部修改，请先处理差异：" + identity);
        string Slice(int start, int end) => Encoding.UTF8.GetString(bytes.AsSpan(start, end - start));
        if (existing != null)
        {
            if (value != null && JsonNode.DeepEquals(existing.Value, value)) return text;
            if (value != null) return Slice(0, existing.Start) + value.ToJsonString(Product.Json) + Slice(existing.End, bytes.Length);
            var index = entries.IndexOf(existing);
            var next = index + 1 < entries.Count ? entries[index + 1].Start : close;
            var comma = FindComma(bytes, existing.End, next);
            if (comma >= 0) return Slice(0, existing.Start) + Slice(existing.End, comma) + Slice(comma + 1, bytes.Length);
            if (index > 0)
            {
                comma = FindComma(bytes, entries[index - 1].End, existing.Start);
                return Slice(0, comma) + Slice(comma + 1, existing.Start) + Slice(existing.End, bytes.Length);
            }
            return Slice(0, existing.Start) + Slice(existing.End, bytes.Length);
        }
        if (value == null) return text;
        var last = entries.Count > 0 ? entries[^1].End : open + 1;
        var separator = entries.Count > 0 && FindComma(bytes, last, close) < 0 ? "," : "";
        return Slice(0, last) + separator + Slice(last, close) + "\n" + value.ToJsonString(Product.Json) + "\n" + Slice(close, bytes.Length);
    }
    private static int FindComma(byte[] bytes, int start, int end)
    {
        for (var i = start; i < end; i++)
        {
            if (bytes[i] == ',') return i;
            if (bytes[i] != '/' || i + 1 >= end) continue;
            if (bytes[i + 1] == '/') { while (i < end && bytes[i] != '\n') i++; }
            else if (bytes[i + 1] == '*') { i += 2; while (i + 1 < end && !(bytes[i] == '*' && bytes[i + 1] == '/')) i++; i++; }
        }
        return -1;
    }
}

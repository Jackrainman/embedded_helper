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

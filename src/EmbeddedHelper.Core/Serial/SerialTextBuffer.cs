using System.Collections.ObjectModel;
using System.Text;

namespace EmbeddedHelper.Core;

public sealed record SerialTextSpan(long Start, string Direction, string Text)
{
    public long End => Start + Text.Length;
}

public sealed record SerialTextSnapshot(long Start, long End, ReadOnlyCollection<SerialTextSpan> Spans, string Mode)
{
    public string Text => string.Concat(Spans.Select(s => s.Text));
}

// Display history only. Immutable spans can safely be shared with a frozen snapshot.
public sealed class SerialTextBuffer
{
    private readonly int capacity, spanCapacity;
    private readonly LinkedList<SerialTextSpan> spans = new();
    private readonly Dictionary<string, Decoder> decoders = new();
    private Encoding encoding = Encoding.UTF8;
    private bool hex;
    public long TrimmedCharacters { get; private set; }
    public long End { get; private set; }
    public long Start => spans.First?.Value.Start ?? End;
    public long Revision { get; private set; }
    public int SpanCount => spans.Count;
    public string Mode => hex ? "HEX" : encoding.WebName;
    public string Text => string.Concat(spans.Select(s => s.Text));
    public SerialTextBuffer(int capacity = 65536, int spanCapacity = 4096)
    {
        if (capacity < 1 || spanCapacity < 1) throw new ArgumentOutOfRangeException(nameof(capacity));
        this.capacity = capacity; this.spanCapacity = spanCapacity;
        Configure("utf-8", false);
    }
    public static Encoding GetEncoding(string name)
    {
        Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);
        if (name is not ("utf-8" or "gb18030" or "us-ascii" or "utf-16")) throw new ArgumentException("不支持的文本编码。");
        return Encoding.GetEncoding(name, EncoderFallback.ExceptionFallback, DecoderFallback.ReplacementFallback);
    }
    public void Configure(string name, bool hexadecimal)
    {
        encoding = GetEncoding(name); hex = hexadecimal;
        ResetDecoder(); Clear();
    }
    public void ResetDecoder()
    {
        decoders.Clear(); decoders["TX"] = encoding.GetDecoder(); decoders["RX"] = encoding.GetDecoder();
    }
    // A display clear must not corrupt a character already partially received.
    public void Clear() { spans.Clear(); Revision++; }
    public SerialTextSnapshot Capture() => new(Start, End, Array.AsReadOnly(spans.ToArray()), Mode);
    public void Append(DataBlock block)
    {
        if (!decoders.TryGetValue(block.Direction, out var decoder)) throw new ArgumentException("未知数据方向。");
        string value;
        if (hex) value = Convert.ToHexString(block.Bytes);
        else
        {
            var chars = new char[encoding.GetMaxCharCount(block.Bytes.Length)];
            value = new string(chars, 0, decoder.GetChars(block.Bytes, chars, false));
        }
        if (value.Length == 0) return;
        if (spans.Last is { } last && last.Value.Direction == block.Direction && last.Value.Text.Length + value.Length <= 1024)
            last.Value = last.Value with { Text = last.Value.Text + value };
        else spans.AddLast(new SerialTextSpan(End, block.Direction, value));
        End += value.Length; Revision++;
        while (spans.Count > spanCapacity) RemoveFirst();
        while (End - Start > capacity && spans.First is { } first)
        {
            var remove = (int)Math.Min(first.Value.Text.Length, End - Start - capacity);
            if (remove == first.Value.Text.Length) { RemoveFirst(); continue; }
            if (char.IsLowSurrogate(first.Value.Text[remove])) remove++;
            if (remove == first.Value.Text.Length) { RemoveFirst(); continue; }
            first.Value = first.Value with { Start = first.Value.Start + remove, Text = first.Value.Text[remove..] };
            TrimmedCharacters += remove;
        }
    }
    private void RemoveFirst()
    {
        TrimmedCharacters += spans.First!.Value.Text.Length;
        spans.RemoveFirst();
    }
}

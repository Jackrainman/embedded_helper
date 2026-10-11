using System.Text;

namespace EmbeddedHelper.Core;

// This is a display window only. Raw recording is independent of encoding and pause.
public sealed class SerialTextBuffer
{
    private readonly int capacity;
    private readonly StringBuilder text = new();
    private readonly Dictionary<string, Decoder> decoders = new();
    private Encoding encoding = Encoding.UTF8;
    private bool hex;
    public long TrimmedCharacters { get; private set; }
    public string Text => text.ToString();
    public SerialTextBuffer(int capacity = 65536)
    {
        if (capacity < 1) throw new ArgumentOutOfRangeException(nameof(capacity));
        this.capacity = capacity;
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
        decoders.Clear(); decoders["TX"] = encoding.GetDecoder(); decoders["RX"] = encoding.GetDecoder();
        Clear();
    }
    public void Clear() { text.Clear(); foreach (var decoder in decoders.Values) decoder.Reset(); }
    public void Append(DataBlock block)
    {
        if (!decoders.TryGetValue(block.Direction, out var decoder)) throw new ArgumentException("未知数据方向。");
        string value;
        if (hex) value = Convert.ToHexString(block.Bytes) + "\n";
        else
        {
            var chars = new char[encoding.GetMaxCharCount(block.Bytes.Length)];
            value = new string(chars, 0, decoder.GetChars(block.Bytes, chars, false));
        }
        if (value.Length > 0) text.Append('[').Append(block.Direction).Append("] ").Append(value);
        if (text.Length > capacity)
        {
            var remove = text.Length - capacity;
            if (remove < text.Length && char.IsLowSurrogate(text[remove])) remove++;
            TrimmedCharacters += remove; text.Remove(0, remove);
        }
    }
}

using System.Reflection;
using System.Text.Json;

namespace EmbeddedHelper.Core;

public sealed class PortableData(string baseDirectory)
{
    public string Root { get; } = Path.Combine(Path.GetFullPath(baseDirectory), "data");
    public T Load<T>(string relative, Func<T> fallback)
    {
        var path = Path.Combine(Root, relative);
        if (!File.Exists(path)) return fallback();
        return JsonSerializer.Deserialize<T>(File.ReadAllText(path), Product.Json) ?? throw new InvalidDataException("设置文件为空。");
    }
    public void Save<T>(string relative, T value) => SaveFile(Path.Combine(Root, relative), value);
    public static void SaveFile<T>(string path, T value)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        var temp = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            File.WriteAllText(temp, JsonSerializer.Serialize(value, Product.Json));
            // Windows can briefly deny replacement while another reader releases its handle.
            // Retry only this atomic operation; permanent errors still reach the caller.
            for (var attempt = 0; ; attempt++)
            {
                try { File.Move(temp, path, true); break; }
                catch (Exception ex) when (attempt < 5 && (ex is UnauthorizedAccessException || ex is IOException io && (io.HResult & 0xffff) is 32 or 33))
                { Thread.Sleep(20 << attempt); }
            }
        }
        finally { if (File.Exists(temp)) File.Delete(temp); }
    }
    public void Log(string message)
    {
        var directory = Path.Combine(Root, "logs");
        Directory.CreateDirectory(directory);
        var path = Path.Combine(directory, "application.log");
        if (File.Exists(path) && new FileInfo(path).Length > 2 * 1024 * 1024) File.Move(path, path + ".previous", true);
        File.AppendAllText(path, $"{DateTimeOffset.Now:O} {message}{Environment.NewLine}");
    }
}

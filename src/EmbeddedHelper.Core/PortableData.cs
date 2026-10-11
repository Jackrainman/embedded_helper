using System.Reflection;
using System.Text.Json;

namespace EmbeddedHelper.Core;

public static class Product
{
    public static string HomeDirectory => File.Exists(Path.Combine(AppContext.BaseDirectory, ".portable-runtime"))
        ? Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "../../..")) : AppContext.BaseDirectory;
    public static string Version => typeof(Product).Assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()!.InformationalVersion.Split('+')[0];
    public static readonly JsonSerializerOptions Json = new() { WriteIndented = true, PropertyNameCaseInsensitive = true };
}

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
            File.Move(temp, path, true);
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

using System.Text.Json;

namespace EmbeddedHelper.Core;

public sealed record WatchSettings(string Target, string[] Identities, int Rate);

public static class WatchSettingsStore
{
    private static string FilePath(string root) => Path.Combine(root, ".embedded_helper", "watch.json");
    public static async Task<WatchSettings?> LoadAsync(string root)
    {
        var path = FilePath(root);
        return File.Exists(path) ? JsonSerializer.Deserialize<WatchSettings>(await File.ReadAllTextAsync(path), Product.Json) : null;
    }
    public static void Save(string root, WatchSettings settings) => PortableData.SaveFile(FilePath(root), settings);
}

using System.Text.Json;

namespace EmbeddedHelper.Core;

public sealed record ConnectionSettings(string Target, string Transport, string ProbeSerial);

public static class ConnectionSettingsStore
{
    public static async Task<ConnectionSettings?> LoadAsync(string root, string target)
    {
        var path = Path.Combine(root, ".embedded_helper", "connection.json");
        ConnectionSettings? connection;
        if (File.Exists(path))
            connection = JsonSerializer.Deserialize<ConnectionSettings>(await File.ReadAllTextAsync(path), Product.Json)
                ?? throw new InvalidDataException("连接配置为空，请重新保存。");
        else
        {
            var legacy = await WatchSettingsStore.LoadAsync(root);
            connection = legacy == null ? null : new(legacy.Target, legacy.Transport, legacy.ProbeSerial);
        }
        if (connection == null) return null;
        Validate(connection);
        if (connection.Target != target) return null;
        return connection;
    }
    public static void Validate(ConnectionSettings settings)
    {
        if (string.IsNullOrWhiteSpace(settings.Target) || settings.Transport is not ("local" or "remote"))
            throw new InvalidDataException("连接目标或方式无效，请重新保存连接配置。");
        if (settings.Transport == "local" && (!int.TryParse(settings.ProbeSerial, out var serial) || serial <= 0))
            throw new InvalidDataException("请在连接配置中填写具体本地探针序列号。");
    }
    public static void Save(string root, ConnectionSettings settings)
    {
        Validate(settings);
        PortableData.SaveFile(Path.Combine(root, ".embedded_helper", "connection.json"), settings);
    }
    public static async Task<Uri> RemoteRttUriAsync(ProjectInfo project, string target)
    {
        var connection = await LoadAsync(project.Root, target);
        if (connection?.Transport != "remote") throw new InvalidDataException("请在连接配置中选择远程 IP 并保存。");
        using var remote = JsonDocument.Parse(await File.ReadAllTextAsync(project.RemotePath), ProjectReader.JsonOptions);
        var web = remote.RootElement.TryGetProperty("WebUrl", out var value) ? value.GetString() : null;
        if (!Uri.TryCreate(web, UriKind.Absolute, out var uri) || uri.Scheme is not ("http" or "https"))
            throw new InvalidDataException("请在远程配置中保存完整的 RTT 网页地址。");
        return uri;
    }
}

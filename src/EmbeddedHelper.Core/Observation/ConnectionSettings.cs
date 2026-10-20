using System.Text.Json;

namespace EmbeddedHelper.Core;

public sealed record ConnectionSettings(string Target, string Transport, string ProbeSerial);
public enum ConnectionConfigurationState { Pending, Valid, Invalid }
public sealed record ConnectionConfiguration(ConnectionConfigurationState State, string Message);

public static class ConnectionSettingsStore
{
    public static async Task<ConnectionConfiguration> InspectAsync(ProjectInfo project, BuildTarget? target, string transport)
    {
        try
        {
            if (target == null) return new(ConnectionConfigurationState.Pending, "请选择工程目标");
            if (string.IsNullOrWhiteSpace(target.Device) || target.Interface is not ("SWD" or "JTAG") || target.SpeedKHz <= 0)
                return new(ConnectionConfigurationState.Invalid, "工程目标的 J-Link 参数不完整，请检查目标配置");
            if (transport == "remote")
            {
                if (!File.Exists(project.RemotePath)) return new(ConnectionConfigurationState.Pending, "请到远程配置填写主机和端口");
                await RemoteEndpointAsync(project);
            }
            else
            {
                var saved = await LoadAsync(project.Root, target.Name, transport, validate: false);
                if (saved == null || string.IsNullOrWhiteSpace(saved.ProbeSerial))
                    return new(ConnectionConfigurationState.Pending, "请填写本地 J-Link 序列号");
                Validate(saved);
            }
            return new(ConnectionConfigurationState.Valid, "连接配置有效");
        }
        catch (Exception ex) { return new(ConnectionConfigurationState.Invalid, ex.Message); }
    }
    public static async Task<ConnectionSettings?> LoadAsync(string root, string target, string? transport = null, bool validate = true)
    {
        if (transport == "remote") return new(target, "remote", "");
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
        if (connection == null) return transport == "remote" ? new(target, "remote", "") : null;
        if (string.IsNullOrWhiteSpace(connection.Target)) throw new InvalidDataException("连接配置缺少目标。");
        if (connection.Target != target) return transport == "remote" ? new(target, "remote", "") : null;
        if (transport != null) connection = connection with { Transport = transport };
        if (validate) Validate(connection);
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
    public static async Task<(string Host, int Port)> RemoteEndpointAsync(ProjectInfo project)
    {
        if (!File.Exists(project.RemotePath)) throw new InvalidDataException("项目未配置远程连接");
        try
        {
            using var remote = JsonDocument.Parse(await File.ReadAllTextAsync(project.RemotePath), ProjectReader.JsonOptions);
            var host = remote.RootElement.GetProperty("ServerName").GetString() ?? "";
            var port = remote.RootElement.GetProperty("Port").GetInt32();
            if (string.IsNullOrWhiteSpace(host) || port is < 1 or > 65535) throw new InvalidDataException("远程主机或端口无效。");
            return (host, port);
        }
        catch (Exception ex) { throw new InvalidDataException("项目远程连接配置错误：" + ex.Message, ex); }
    }
    public static async Task<Uri> RemoteRttUriAsync(ProjectInfo project, string target, string? transport = null)
    {
        var connection = await LoadAsync(project.Root, target, transport);
        if (connection?.Transport != "remote") throw new InvalidDataException("请在连接配置中选择远程 IP 并保存。");
        using var remote = JsonDocument.Parse(await File.ReadAllTextAsync(project.RemotePath), ProjectReader.JsonOptions);
        var web = remote.RootElement.TryGetProperty("WebUrl", out var value) ? value.GetString() : null;
        if (!Uri.TryCreate(web, UriKind.Absolute, out var uri) || uri.Scheme is not ("http" or "https"))
            throw new InvalidDataException("请在远程配置中保存完整的 RTT 网页地址。");
        return uri;
    }
}

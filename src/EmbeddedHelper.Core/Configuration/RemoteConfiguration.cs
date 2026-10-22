using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace EmbeddedHelper.Core;

public static class RemoteConfiguration
{
    public static int ParsePort(string text, string field, int maximum = 65535)
    {
        if (!int.TryParse(text, System.Globalization.NumberStyles.None, System.Globalization.CultureInfo.InvariantCulture, out var port) || port < 1 || port > maximum)
            throw new ArgumentException($"{field}须为1～{maximum}的整数，请修改该字段。");
        return port;
    }
    public static ConfigPlan Plan(ProjectInfo project, BuildTarget target, string host, int port, string? webAddress = null)
    {
        if (project.Family is not ("G4" or "H7" or "F4" or "F1")) throw new InvalidDataException("该系列尚未完成配置适配，保留只读。");
        if (!string.IsNullOrEmpty(webAddress) && (!Uri.TryCreate(webAddress, UriKind.Absolute, out var uri) || uri.Scheme is not ("http" or "https"))) throw new ArgumentException("网页地址须为完整HTTP(S)地址或留空。");
        if (string.IsNullOrWhiteSpace(host) || host.Any(char.IsWhiteSpace) || Uri.CheckHostName(host) == UriHostNameType.Unknown || port is < 1 or > 65535)
            throw new ArgumentException("请填写主机名/IP 和 1～65535 的探针端口。");
        if (string.IsNullOrWhiteSpace(target.Device) || target.Interface == "未知" || target.SpeedKHz <= 0) throw new InvalidDataException("连接参数不完整。");
        if (project.Diagnostics.Any(d => d.Contains("多份远程配置"))) throw new InvalidDataException("远程配置存在歧义。");
        var before = File.Exists(project.RemotePath) ? File.ReadAllText(project.RemotePath) : null;
        var text = before ?? "{}";
        foreach (var (key, value) in new Dictionary<string, JsonNode?> { ["ServerName"] = JsonValue.Create(host), ["Port"] = JsonValue.Create(port), ["Device"] = JsonValue.Create(target.Device), ["Interface"] = JsonValue.Create(target.Interface), ["SpeedKHz"] = JsonValue.Create(target.SpeedKHz) }) text = JsonEdit.Set(text, key, value);
        if (before == null) text = JsonEdit.Set(text, "JLinkExe", JsonValue.Create(""));
        if (webAddress != null) text = JsonEdit.Set(text, "WebUrl", JsonValue.Create(webAddress));
        using var parsed = JsonDocument.Parse(text, ProjectReader.JsonOptions);
        return new ConfigPlan(project.Root, [new FileChange(project.RemotePath, before, text)]);
    }
}

using System.Text.Json;
using YamlDotNet.RepresentationModel;

namespace EmbeddedHelper.Core;

public sealed record BuildTarget(string Name, string Device, string Interface, int SpeedKHz, string ElfPath);
public sealed record ProjectInfo(string Root, string Name, string Family, string Mcu, BuildTarget[] Targets, string RemotePath, string[] Diagnostics);

public static class ProjectReader
{
    private static string MissingElf(BuildTarget target) => $"目标 {target.Name} 的 ELF 不存在：{target.ElfPath}";
    public static ProjectInfo RefreshFileDiagnostics(ProjectInfo project, string? selectedTarget = null, string? selectedElf = null)
    {
        var diagnostics = project.Diagnostics.Where(d => !project.Targets.Any(t => d == MissingElf(t))).ToList();
        foreach (var target in project.Targets)
        {
            var path = target.Name == selectedTarget && selectedElf != null ? selectedElf : target.ElfPath;
            if (!File.Exists(path)) diagnostics.Add(MissingElf(target));
        }
        return project with { Diagnostics = diagnostics.ToArray() };
    }
    public static readonly JsonDocumentOptions JsonOptions = new() { CommentHandling = JsonCommentHandling.Skip, AllowTrailingCommas = true };
    public static ProjectInfo Read(string input)
    {
        var root = Path.GetFullPath(input);
        if (File.Exists(root))
        {
            using var workspace = JsonDocument.Parse(File.ReadAllText(root), JsonOptions);
            var folders = workspace.RootElement.GetProperty("folders").EnumerateArray().ToArray();
            if (folders.Length != 1) throw new InvalidDataException("工作区含多个工程，请直接打开要使用的工程目录。");
            root = Path.GetFullPath(folders[0].GetProperty("path").GetString()!, Path.GetDirectoryName(root)!);
        }
        var path = Path.Combine(root, ".eide", "eide.yml");
        if (!File.Exists(path)) throw new InvalidDataException("未找到 .eide/eide.yml，请选择 EIDE 工程根目录。");
        var yaml = new YamlStream();
        using (var reader = File.OpenText(path)) yaml.Load(reader);
        if (yaml.Documents.Count != 1 || yaml.Documents[0].RootNode is not YamlMappingNode map)
            throw new InvalidDataException("EIDE 配置应为单个 YAML 文档。");
        var name = Text(map, "name");
        var mcu = Text(map, "deviceName");
        var family = new[] { "G4", "H7", "F4", "F1" }.FirstOrDefault(f => mcu.StartsWith("STM32" + f, StringComparison.OrdinalIgnoreCase)) ?? "未知";
        var diagnostics = new List<string>();
        var launchPath = Path.Combine(root, ".vscode", "launch.json");
        JsonElement[] localLaunches = [];
        if (File.Exists(launchPath))
        {
            using var launch = JsonDocument.Parse(File.ReadAllText(launchPath), JsonOptions);
            if (launch.RootElement.TryGetProperty("configurations", out var configurations))
                localLaunches = configurations.EnumerateArray().Where(c => c.TryGetProperty("servertype", out var kind) && kind.GetString() == "jlink").Select(c => c.Clone()).ToArray();
        }
        diagnostics.Add(family == "未知" ? "该系列尚未完成配置适配；工程只读。" : "该系列已验证配置样本适配；硬件链路未验收。");
        var targets = Map(map, "targets").Children.Select(pair =>
        {
            var targetName = ((YamlScalarNode)pair.Key).Value ?? throw new InvalidDataException("目标名称缺失。");
            var target = (YamlMappingNode)pair.Value;
            var upload = OptionalMap(OptionalMap(target, "uploadConfigMap"), "JLink");
            var cpu = Text(OptionalMap(upload, "cpuInfo"), "cpuName", "");
            var protocol = Text(upload, "proType", "");
            var iface = protocol switch { "0" => "JTAG", "1" => "SWD", _ => "未知" };
            if (localLaunches.Length == 1)
            {
                var launch = localLaunches[0];
                if (launch.TryGetProperty("device", out var device) && device.GetString() != cpu)
                    throw new InvalidDataException("EIDE 与本地 launch 的芯片配置冲突，请先检查工程。");
                if (launch.TryGetProperty("interface", out var connection) && !string.Equals(connection.GetString(), iface, StringComparison.OrdinalIgnoreCase))
                    throw new InvalidDataException("EIDE 与本地 launch 的连接接口冲突，请先检查工程。");
            }
            else if (localLaunches.Length > 1) diagnostics.Add("存在多个本地 J-Link 调试配置，请核对所选目标参数。");
            int.TryParse(Text(upload, "speed", "0"), out var speed);
            if (cpu.Length == 0 || iface == "未知" || speed <= 0) diagnostics.Add($"目标 {targetName} 的 J-Link 参数不完整。");
            var elf = Path.GetFullPath(Path.Combine(Text(map, "outDir", "Build"), targetName, name + ".elf"), root);
            if (!File.Exists(elf)) diagnostics.Add(MissingElf(new BuildTarget(targetName, cpu, iface, speed, elf)));
            return new BuildTarget(targetName, cpu, iface, speed, elf);
        }).ToArray();
        if (targets.Length == 0) throw new InvalidDataException("工程没有构建目标。");
        var candidates = new[] { Path.Combine(root, "Debug_LAN", "remote-jlink.json"), Path.Combine(root, "remote-jlink.json") }.Where(File.Exists).ToArray();
        if (candidates.Length > 1) diagnostics.Add("存在多份远程配置，请先解决歧义；不能自动写入。");
        return new ProjectInfo(root, name, family, mcu, targets, candidates.FirstOrDefault() ?? Path.Combine(root, "Debug_LAN", "remote-jlink.json"), diagnostics.ToArray());
    }

    private static YamlMappingNode Map(YamlMappingNode node, string key) => node.Children.TryGetValue(new YamlScalarNode(key), out var value) && value is YamlMappingNode mapping ? mapping : throw new InvalidDataException($"缺少配置：{key}");
    private static YamlMappingNode OptionalMap(YamlMappingNode node, string key) => node.Children.TryGetValue(new YamlScalarNode(key), out var value) && value is YamlMappingNode mapping ? mapping : new YamlMappingNode();
    private static string Text(YamlMappingNode node, string key, string? fallback = null) => node.Children.TryGetValue(new YamlScalarNode(key), out var value) && value is YamlScalarNode scalar ? scalar.Value ?? "" : fallback ?? throw new InvalidDataException($"缺少配置：{key}");
}

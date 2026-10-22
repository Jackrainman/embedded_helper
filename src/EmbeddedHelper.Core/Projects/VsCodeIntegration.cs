using System.Text.Json;
using System.Text.Json.Nodes;

namespace EmbeddedHelper.Core;

public sealed record ManagedEntry(string File, string Array, string Key, string Identity, JsonObject Value);
public sealed record IntegrationState(int SchemaVersion, string Target, ManagedEntry[] Entries, ManagedYamlField[]? EideFields = null, int GdbPort = 2331);

public static class VsCodeIntegration
{
    private const string OwnerFile = ".embedded_helper/integration.json";
    public static IntegrationState? ReadState(string root)
    {
        var path = Path.Combine(root, OwnerFile);
        if (!File.Exists(path)) return null;
        try
        {
            var state = JsonSerializer.Deserialize<IntegrationState>(File.ReadAllText(path), Product.Json) ?? throw new InvalidDataException("接入归属记录无效。");
            if (state.SchemaVersion != 1 || state.Target == null || state.Entries == null || state.Entries.Any(e => e == null || e.Value == null || string.IsNullOrWhiteSpace(e.Identity) || (e.File, e.Array, e.Key) is not ((".vscode/tasks.json", "tasks", "label") or (".vscode/launch.json", "configurations", "name")))) throw new InvalidDataException("接入归属记录包含缺失或不支持的条目。");
            if (state.EideFields?.Any(f => f == null || f.Keys is not ["targets", _, "uploader"] and not ["targets", _, "uploadConfigMap", "Custom", "bin" or "commandLine" or "eraseChipCommand"]) == true) throw new InvalidDataException("EIDE归属字段无效。");
            return state;
        }
        catch (Exception ex) when (ex is JsonException or IOException or UnauthorizedAccessException or InvalidDataException)
        { throw new InvalidDataException("接入归属记录不可读：" + path + "；" + ex.Message, ex); }
    }
    public static ConfigPlan Plan(ProjectInfo project, BuildTarget target, string host, int port, int gdbPort, ToolRelease tool, bool eideUpload = false, string? webAddress = null)
    {
        if (gdbPort is < 1 or > 65533) throw new ArgumentException("本机GDB端口须为1～65533，并为相邻两个端口留出空间。");
        var remote = RemoteConfiguration.Plan(project, target, host, port, webAddress);
        var relativeElf = Path.GetRelativePath(project.Root, ProjectReader.ResolveElfPath(target.ElfPath)).Replace('\\', '/');
        if (Path.IsPathRooted(relativeElf) || relativeElf.StartsWith("../")) throw new InvalidDataException("共享调试配置要求ELF位于工程内。");
        var suffix = target.Name;
        var flashLabel = "LAN_Debug: flash (" + suffix + ")";
        var gdbLabel = "LAN_Debug: GDB (" + suffix + ")";
        var debugName = "LAN_Debug: debug (" + suffix + ")";
        var entry = ProjectToolCache.RelativeEntry;
        var config = Path.GetRelativePath(project.Root, project.RemotePath).Replace('\\', '/');
        ConfigTransaction.CheckPath(project.Root, project.RemotePath);
        JsonObject Task(string label, params string[] arguments) => new()
        {
            ["label"] = label,
            ["type"] = "process",
            ["command"] = "python",
            ["linux"] = new JsonObject { ["command"] = "python3" },
            ["args"] = new JsonArray(new[] { "-u", "${workspaceFolder}/" + entry, "--config", "${workspaceFolder}/" + config }.Concat(arguments).Select(a => (JsonNode?)JsonValue.Create(a)).ToArray()),
            ["options"] = new JsonObject { ["cwd"] = "${workspaceFolder}" },
            ["problemMatcher"] = new JsonArray()
        };
        var flash = Task(flashLabel, "--program", "${workspaceFolder}/" + Path.ChangeExtension(relativeElf, ".hex"));
        var gdb = Task(gdbLabel, "--gdb-server", "--gdb-port", gdbPort.ToString(System.Globalization.CultureInfo.InvariantCulture));
        gdb["isBackground"] = true; gdb["runOptions"] = new JsonObject { ["instanceLimit"] = 1 };
        gdb["problemMatcher"] = new JsonObject
        {
            ["owner"] = "embedded-helper",
            ["pattern"] = new JsonObject { ["regexp"] = "^(.*):(\\d+):(\\d+): (.*)$", ["file"] = 1, ["line"] = 2, ["column"] = 3, ["message"] = 4 },
            ["background"] = new JsonObject { ["activeOnStart"] = true, ["beginsPattern"] = "^\\[remote-jlink\\] GDB STARTING$", ["endsPattern"] = "^\\[remote-jlink\\] GDB READY$" }
        };
        var launch = new JsonObject
        {
            ["name"] = debugName,
            ["type"] = "cortex-debug",
            ["request"] = "launch",
            ["cwd"] = "${workspaceFolder}",
            ["servertype"] = "external",
            ["gdbTarget"] = "127.0.0.1:" + gdbPort,
            ["preLaunchTask"] = gdbLabel,
            ["executable"] = "${workspaceFolder}/" + relativeElf,
            ["device"] = target.Device,
            ["runToEntryPoint"] = "main",
            ["overrideLaunchCommands"] = new JsonArray("monitor halt", "load", "monitor reset"),
            ["overrideRestartCommands"] = new JsonArray("monitor reset")
        };
        var launchPath = Path.Combine(project.Root, ".vscode/launch.json");
        if (File.Exists(launchPath))
        {
            using var existing = JsonDocument.Parse(File.ReadAllText(launchPath), ProjectReader.JsonOptions);
            var local = ProjectReader.LaunchConfigurations(existing.RootElement, launchPath).Where(e => e.TryGetProperty("servertype", out var type) && type.GetString() == "jlink").ToArray();
            if (local.Length == 1 && local[0].TryGetProperty("svdFile", out var svd)) launch["svdFile"] = svd.GetString();
        }
        ManagedEntry[] desired = [new(".vscode/tasks.json", "tasks", "label", flashLabel, flash), new(".vscode/tasks.json", "tasks", "label", gdbLabel, gdb), new(".vscode/launch.json", "configurations", "name", debugName, launch)];
        var command = "python \"${ProjectRoot}/" + entry + "\" --config \"${ProjectRoot}/" + config + "\" --program \"${ProjectRoot}/" + Path.ChangeExtension(relativeElf, ".hex") + "\"";
        return MakePlan(project.Root, target.Name, desired, remote.Changes.Append(ToolCache.SelectVersion(project.Root, tool)).ToArray(), eideUpload, gdbPort, command);
    }
    public static ConfigPlan Remove(string root) => MakePlan(root, "", [], []);
    private static ConfigPlan MakePlan(string root, string target, ManagedEntry[] desired, FileChange[] additional, bool eideUpload = false, int gdbPort = 2331, string? command = null)
    {
        var state = ReadState(root);
        if (desired.Length == 0 && state == null) throw new InvalidDataException("没有共享接入归属记录，无法确认可移除条目；原配置保持不变。");
        var changes = new List<FileChange>(additional);
        var eide = EideUpload.Plan(root, target, eideUpload, state?.EideFields ?? [], command);
        changes.Add(eide.Change);
        var old = state?.Entries ?? [];
        foreach (var file in old.Select(e => e.File).Concat(desired.Select(e => e.File)).Distinct())
        {
            var path = Path.Combine(root, file); ConfigTransaction.CheckPath(root, path);
            var before = File.Exists(path) ? File.ReadAllText(path) : null;
            var text = before ?? (file.EndsWith("tasks.json") ? "{\"version\":\"2.0.0\",\"tasks\":[]}" : "{\"version\":\"0.2.0\",\"configurations\":[]}");
            foreach (var previous in old.Where(e => e.File == file && !desired.Any(d => d.File == file && d.Identity == e.Identity)))
                text = JsonArrayEdit.Change(text, previous.Array, previous.Key, previous.Identity, null, previous.Value);
            foreach (var entry in desired.Where(e => e.File == file))
                text = JsonArrayEdit.Change(text, entry.Array, entry.Key, entry.Identity, entry.Value, old.SingleOrDefault(e => e.File == file && e.Identity == entry.Identity)?.Value);
            changes.Add(new FileChange(path, before, text));
        }
        var owner = Path.Combine(root, OwnerFile); ConfigTransaction.CheckPath(root, owner);
        changes.Add(new FileChange(owner, File.Exists(owner) ? File.ReadAllText(owner) : null, JsonSerializer.Serialize(new IntegrationState(1, target, desired, eide.Fields, gdbPort), Product.Json)));
        return new ConfigPlan(root, changes.Where(c => c.Before != c.After).ToArray());
    }
}

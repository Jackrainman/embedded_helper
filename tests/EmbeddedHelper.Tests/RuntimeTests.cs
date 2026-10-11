using System.Text.Json;
using System.Text.Json.Nodes;
using EmbeddedHelper.Core;
using Xunit;

namespace EmbeddedHelper.Tests;

[Collection("Offline client")]
public sealed class RuntimeTests : TestWorkspace
{
    [Fact]
    public void RuntimeUpgradeKeepsHomeDataAndExternalEntryStable()
    {
        var home = Path.Combine(root, "软件 中文 空格");
        var old = Path.Combine(home, "data", "runtime", "old-hash");
        var next = Path.Combine(home, "data", "runtime", "new-hash");
        foreach (var directory in new[] { old, next })
        {
            Directory.CreateDirectory(directory);
            File.WriteAllText(Path.Combine(directory, ".portable-runtime"), "embedded-helper-portable-v1");
        }
        var before = RuntimePaths.FromComponents(old);
        var after = RuntimePaths.FromComponents(next);
        Assert.Equal(home, before.HomeDirectory);
        Assert.Equal(before.DataDirectory, after.DataDirectory);
        Assert.Equal(before.ExternalEntry(null), after.ExternalEntry(null));
        Assert.NotEqual(before.WorkerExecutable, after.WorkerExecutable);
        var renamed = Path.Combine(home, "助手.exe");
        Assert.Equal(renamed, after.ExternalEntry(renamed));
        Assert.Throws<InvalidDataException>(() => after.ExternalEntry(Path.Combine(root, "other.exe")));
        Directory.Delete(old, true);
        Assert.Equal(Path.Combine(home, "EmbeddedHelper.exe"), after.ExternalEntry(null));
        var development = RuntimePaths.FromComponents(root);
        Assert.Equal(Path.Combine(root, "embedded-helper-cli.exe"), development.ExternalEntry(null));
    }

    [Fact]
    public void LegacyIntegrationMigratesThroughOwnershipAndStillRestoresOriginalEide()
    {
        var path = CopyProject(); var project = ProjectReader.Read(path); var target = project.Targets[0];
        var yamlPath = Path.Combine(path, ".eide/eide.yml");
        var original = File.ReadAllText(yamlPath);
        ConfigPlan Plan() => VsCodeIntegration.Plan(project, target, "example.invalid", 19011, 2331, ToolRelease.Known[0], true);
        ConfigTransaction.Apply(Plan(), Path.Combine(root, "journal"));
        var state = VsCodeIntegration.ReadState(path)!;
        var taskPath = Path.Combine(path, ".vscode/tasks.json");
        var tasks = JsonNode.Parse(File.ReadAllText(taskPath))!;
        foreach (var entry in state.Entries.Where(e => e.File == ".vscode/tasks.json"))
        {
            entry.Value["command"] = "${env:EMBEDDED_HELPER_HOME}/embedded-helper-cli.exe";
            entry.Value["args"]!.AsArray().RemoveAt(0);
            var task = tasks["tasks"]!.AsArray().Single(t => t!["label"]!.GetValue<string>() == entry.Identity)!;
            task["command"] = entry.Value["command"]!.DeepClone();
            task["args"] = entry.Value["args"]!.DeepClone();
        }
        File.WriteAllText(taskPath, tasks.ToJsonString(Product.Json));
        const string legacy = "\"%EMBEDDED_HELPER_HOME%\\embedded-helper-cli.exe\" --project \"${ProjectRoot}\" flash --program \"${programFile}\"";
        var fields = state.EideFields!.Select(f => f.Keys[^1] == "commandLine" ? f with { After = JsonSerializer.Serialize(legacy) } : f).ToArray();
        var command = fields.Single(f => f.Keys[^1] == "commandLine");
        File.WriteAllText(yamlPath, EideUpload.Set(File.ReadAllText(yamlPath), command.Keys, command.After));
        PortableData.SaveFile(Path.Combine(path, ".embedded_helper/integration.json"), state with { EideFields = fields });
        ConfigTransaction.Apply(Plan(), Path.Combine(root, "journal"));
        Assert.Contains("EMBEDDED_HELPER_ENTRY", File.ReadAllText(taskPath));
        Assert.Contains("--cli", File.ReadAllText(yamlPath));
        Assert.Empty(Plan().Changes);
        ConfigTransaction.Apply(VsCodeIntegration.Remove(path), Path.Combine(root, "journal"));
        Assert.Equal(original, File.ReadAllText(yamlPath));
    }
}

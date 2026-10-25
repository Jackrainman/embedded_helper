using System.Text.Json;
using System.Text.Json.Nodes;
using EmbeddedHelper.Core;
using Xunit;

namespace EmbeddedHelper.Tests;

[Collection("Offline client")]
public sealed class IntegrationTests : TestWorkspace
{
    [Fact]
    public void ImportReplacesCustomCliByDefaultAndRemovalRestoresOriginal()
    {
        var path = CopyProject(); var project = ProjectReader.Read(path); var target = project.Targets[0];
        var yaml = Path.Combine(path, ".eide/eide.yml");
        var original = File.ReadAllText(yaml);
        var plan = VsCodeIntegration.Plan(project, target, "example.invalid", 19011, 2331, ToolRelease.Known[0]);
        ConfigTransaction.Apply(plan, Path.Combine(root, "journal"));
        var saved = File.ReadAllText(yaml);
        Assert.Equal("\"Custom\"", EideUpload.Get(saved, ["targets", target.Name, "uploader"]));
        Assert.Contains("${ProjectRoot}/LAN_Debug/jlink_flash.py", EideUpload.Get(saved, ["targets", target.Name, "uploadConfigMap", "Custom", "commandLine"]));
        ConfigTransaction.Apply(VsCodeIntegration.Remove(path), Path.Combine(root, "journal"));
        Assert.Equal(original, File.ReadAllText(yaml));
    }
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void ImportWithoutReplacementKeepsCurrentEideAndItsRestoreHistory(bool alreadyManaged)
    {
        var path = CopyProject(); var project = ProjectReader.Read(path); var target = project.Targets[0];
        var yaml = Path.Combine(path, ".eide/eide.yml");
        var original = File.ReadAllText(yaml);
        if (alreadyManaged)
            ConfigTransaction.Apply(VsCodeIntegration.Plan(project, target, "example.invalid", 19011, 2331, ToolRelease.Known[0], true), Path.Combine(root, "journal"));
        var before = File.ReadAllText(yaml);
        var plan = VsCodeIntegration.Plan(project, target, "example.invalid", 19011, 2450, ToolRelease.Known[0], false);
        Assert.DoesNotContain(plan.Changes, c => c.Path == yaml);
        ConfigTransaction.Apply(plan, Path.Combine(root, "journal"));
        Assert.Equal(before, File.ReadAllText(yaml));
        Assert.Equal(alreadyManaged, VsCodeIntegration.ReadState(path)!.EideFields!.Length > 0);
        Assert.Empty(VsCodeIntegration.Plan(project, target, "example.invalid", 19011, 2450, ToolRelease.Known[0], false).Changes);
        ConfigTransaction.Apply(VsCodeIntegration.Remove(path), Path.Combine(root, "journal"));
        Assert.Equal(original, File.ReadAllText(yaml));
    }
    [Fact]
    public void IntegrationIsIdempotentPreservesLocalBytesAndRemovesWithoutPrivateBackups()
    {
        var path = CopyProject(); var project = ProjectReader.Read(path);
        var tasks = Path.Combine(path, ".vscode", "tasks.json");
        const string original = "{\"version\":\"2.0.0\", /* root */ \"tasks\":[\n// keep , comment\n{\"label\":\"Local build\",\"command\":\"unchanged\",\"unknown\":[1,2]}, // trailing , comment\n]}";
        File.WriteAllText(tasks, original);
        var plan = VsCodeIntegration.Plan(project, project.Targets[0], "example.invalid", 19011, 2331, ToolRelease.Known[0]);
        ConfigTransaction.Apply(plan, Path.Combine(root, "private-journal"));
        var saved = File.ReadAllText(tasks);
        Assert.Contains("{\"label\":\"Local build\",\"command\":\"unchanged\",\"unknown\":[1,2]}", saved);
        Assert.Contains("// keep , comment", saved); Assert.Contains("// trailing , comment", saved);
        Assert.Empty(VsCodeIntegration.Plan(project, project.Targets[0], "example.invalid", 19011, 2331, ToolRelease.Known[0]).Changes);
        using var launch = JsonDocument.Parse(File.ReadAllText(Path.Combine(path, ".vscode/launch.json")), ProjectReader.JsonOptions);
        Assert.Equal(2, launch.RootElement.GetProperty("configurations").GetArrayLength());
        var remove = VsCodeIntegration.Remove(path);
        ConfigTransaction.Apply(remove, Path.Combine(root, "another-computer-journal"));
        using var remaining = JsonDocument.Parse(File.ReadAllText(tasks), ProjectReader.JsonOptions);
        Assert.Equal("Local build", Assert.Single(remaining.RootElement.GetProperty("tasks").EnumerateArray()).GetProperty("label").GetString());
        Assert.Contains("// keep , comment", File.ReadAllText(tasks));
        Assert.Empty(VsCodeIntegration.Remove(path).Changes);
    }
    [Fact]
    public void IntegrationRejectsChangedOwnedEntriesAndRollsBackMultiFileFailure()
    {
        var path = CopyProject(); var project = ProjectReader.Read(path);
        var original = Directory.GetFiles(path, "*", SearchOption.AllDirectories).ToDictionary(p => p, File.ReadAllText);
        var plan = VsCodeIntegration.Plan(project, project.Targets[0], "example.invalid", 19011, 2331, ToolRelease.Known[0]);
        Assert.Throws<IOException>(() => ConfigTransaction.Apply(plan, Path.Combine(root, "journal"), count => { if (count == 3) throw new IOException("injected write failure"); }));
        foreach (var file in original) Assert.Equal(file.Value, File.ReadAllText(file.Key));
        Assert.False(File.Exists(Path.Combine(path, ".vscode/tasks.json")));
        ConfigTransaction.Apply(plan, Path.Combine(root, "journal"));
        var tasks = Path.Combine(path, ".vscode/tasks.json");
        File.WriteAllText(tasks, File.ReadAllText(tasks).Replace("${workspaceFolder}/" + ProjectToolCache.RelativeEntry, "user-owned.exe"));
        Assert.Throws<InvalidDataException>(() => VsCodeIntegration.Remove(path));
        Assert.Contains("user-owned.exe", File.ReadAllText(tasks));
    }
    [Theory]
    [InlineData("G4 中文 空格", "G4")]
    [InlineData("H7 配置样本", "H7")]
    [InlineData("F4 配置样本", "F4")]
    [InlineData("F1 配置样本", "F1")]
    public void TemplateAdaptersRestoreEideUploadBytesAndKeepLocalEntries(string fixture, string family)
    {
        var path = CopyProject(fixture); var project = ProjectReader.Read(path);
        Assert.Equal(family, project.Family);
        var yaml = Path.Combine(path, ".eide/eide.yml");
        var before = File.ReadAllText(yaml);
        var eide = EideUpload.Plan(path, project.Targets[0].Name, true, [], "python script.py --program firmware.hex");
        ConfigTransaction.Apply(new ConfigPlan(path, [eide.Change]), Path.Combine(root, "journal"));
        Assert.Equal("\"Custom\"", EideUpload.Get(File.ReadAllText(yaml), ["targets", project.Targets[0].Name, "uploader"]));
        Assert.Equal(eide.Change.After, EideUpload.Plan(path, project.Targets[0].Name, true, eide.Fields, "python script.py --program firmware.hex").Change.After);
        var removal = EideUpload.Plan(path, project.Targets[0].Name, false, eide.Fields);
        ConfigTransaction.Apply(new ConfigPlan(path, [removal.Change]), Path.Combine(root, "journal"));
        Assert.Equal(before, File.ReadAllText(yaml));
        var launch = Path.Combine(path, ".vscode/launch.json");
        using var localBefore = System.Text.Json.JsonDocument.Parse(File.ReadAllText(launch), ProjectReader.JsonOptions);
        var localEntries = localBefore.RootElement.GetProperty("configurations").EnumerateArray().Select(e => e.GetRawText()).ToArray();
        var complete = VsCodeIntegration.Plan(project, project.Targets[0], "server.local", 19011, 2450, ToolRelease.Known[0], true, "http://server.local:8000");
        ConfigTransaction.Apply(complete, Path.Combine(root, "journal"));
        Assert.Empty(VsCodeIntegration.Plan(project, project.Targets[0], "server.local", 19011, 2450, ToolRelease.Known[0], true, "http://server.local:8000").Changes);
        Assert.Equal(2450, VsCodeIntegration.ReadState(path)!.GdbPort);
        ConfigTransaction.Apply(VsCodeIntegration.Remove(path), Path.Combine(root, "journal"));
        Assert.Equal(before, File.ReadAllText(yaml));
        using var localAfter = System.Text.Json.JsonDocument.Parse(File.ReadAllText(launch), ProjectReader.JsonOptions);
        Assert.Equal(localEntries, localAfter.RootElement.GetProperty("configurations").EnumerateArray().Select(e => e.GetRawText()));
    }
    [Fact]
    public void EideRestoreKeepsUnknownFieldsAndRejectsExternalManagedEdits()
    {
        var path = CopyProject(); var project = ProjectReader.Read(path);
        var yaml = Path.Combine(path, ".eide/eide.yml");
        var input = File.ReadAllText(yaml);
        var prefix = new[] { "targets", project.Targets[0].Name, "uploadConfigMap", "Custom" };
        input = EideUpload.Set(input, [.. prefix, "unknown"], "\"keep me\"");
        input = EideUpload.Set(input, [.. prefix, "commandLine"], "\"original command\"");
        File.WriteAllText(yaml, input);
        var eide = EideUpload.Plan(path, project.Targets[0].Name, true, [], "python script.py --program firmware.hex");
        File.WriteAllText(yaml, eide.Change.After);
        Assert.Equal(input, EideUpload.Plan(path, project.Targets[0].Name, false, eide.Fields).Change.After);
        File.WriteAllText(yaml, EideUpload.Set(File.ReadAllText(yaml), [.. prefix, "commandLine"], "\"external command\""));
        Assert.Throws<InvalidDataException>(() => EideUpload.Plan(path, project.Targets[0].Name, false, eide.Fields));
    }
}

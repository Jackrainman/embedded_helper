using System.Security.Cryptography;
using System.Text.Json;
using EmbeddedHelper.Core;
using Xunit;

namespace EmbeddedHelper.Tests;

[Collection("Offline client")]
public sealed class ProjectToolTests : TestWorkspace
{
    [Fact]
    public async Task ProjectScriptsAreTransactionalIdempotentAndPreserveUserChanges()
    {
        var (zip, release) = ScriptPackage(); var data = Path.Combine(root, "data"); var project = CopyProject();
        await new ToolCache(data).InstallAsync(zip, release);
        var changes = ProjectToolCache.Plan(project, data, release);
        var plan = new ConfigPlan(project, changes.Append(ToolCache.SelectVersion(project, release)).ToArray());
        Assert.Throws<IOException>(() => ConfigTransaction.Apply(plan, Path.Combine(root, "journal"), count => { if (count == 2) throw new IOException("failure"); }));
        Assert.False(File.Exists(Path.Combine(project, "LAN_Debug/jlink_flash.py")));
        Assert.False(File.Exists(Path.Combine(project, ".embedded_helper/tools.lock.json")));
        ConfigTransaction.Apply(plan, Path.Combine(root, "journal"));
        var entry = ProjectToolCache.Resolve(project, release);
        Assert.Empty(ProjectToolCache.Plan(project, data, release));
        Assert.False(File.Exists(Path.Combine(project, "LAN_Debug/.gitignore")));
        Assert.False(File.Exists(Path.Combine(project, "LAN_Debug/remote-jlink.json")));
        File.WriteAllText(entry, "user changed");
        Assert.Throws<InvalidDataException>(() => ProjectToolCache.Plan(project, data, release));
        Assert.Throws<InvalidDataException>(() => ProjectToolCache.Resolve(project, release));
        Assert.Equal("user changed", File.ReadAllText(entry));
        File.Delete(entry);
        ConfigTransaction.Apply(new ConfigPlan(project, ProjectToolCache.Plan(project, data, release)), Path.Combine(root, "journal"));
        Assert.Equal(entry, ProjectToolCache.Resolve(project, release));
    }
    [Fact]
    public async Task SwitchingScriptsUpdatesLockAndRollsBackBothOnFailure()
    {
        var data = Path.Combine(root, "data"); var project = CopyProject();
        var old = ScriptPackage(); var next = ScriptPackage();
        foreach (var package in new[] { old, next }) await new ToolCache(data).InstallAsync(package.Zip, package.Release);
        ConfigPlan Plan(ToolRelease release) => new(project, ProjectToolCache.Plan(project, data, release).Append(ToolCache.SelectVersion(project, release)).ToArray());
        ConfigTransaction.Apply(Plan(old.Release), Path.Combine(root, "journal"));
        Assert.Throws<IOException>(() => ConfigTransaction.Apply(Plan(next.Release), Path.Combine(root, "journal"), count => { if (count == 2) throw new IOException("failure"); }));
        Assert.Equal(old.Release, ToolCache.ReadLock(project));
        ProjectToolCache.Resolve(project, old.Release);
        ConfigTransaction.Apply(Plan(next.Release), Path.Combine(root, "journal"));
        Assert.Equal(next.Release, ToolCache.ReadLock(project));
        ProjectToolCache.Resolve(project, next.Release);
    }
    [Fact]
    public void NewProjectsUseLanDebugRelativeTasksAndExistingLegacyConfigurationRemainsCompatible()
    {
        var path = CopyProject(); File.Delete(Path.Combine(path, "remote-jlink.json")); var project = ProjectReader.Read(path);
        Assert.EndsWith(Path.Combine("LAN_Debug", "remote-jlink.json"), project.RemotePath);
        var legacy = Path.Combine(path, "Debug_LAN", "remote-jlink.json"); Directory.CreateDirectory(Path.GetDirectoryName(legacy)!);
        File.WriteAllText(legacy, "{\"ServerName\":\"old.local\",\"Port\":19011,\"CustomField\":42}");
        project = ProjectReader.Read(path); Assert.Equal(legacy, project.RemotePath);
        var plan = VsCodeIntegration.Plan(project, project.Targets[0], "old.local", 19011, 2331, ToolRelease.Known[0], true);
        ConfigTransaction.Apply(plan, Path.Combine(root, "journal"));
        var tasks = File.ReadAllText(Path.Combine(path, ".vscode", "tasks.json"));
        Assert.Contains("LAN_Debug/jlink_flash.py", tasks); Assert.Contains("${workspaceFolder}/Debug_LAN/remote-jlink.json", tasks);
        Assert.DoesNotContain("EMBEDDED_HELPER_ENTRY", tasks); Assert.DoesNotContain(path, tasks); Assert.Contains("--gdb-server", tasks);
        var yaml = File.ReadAllText(Path.Combine(path, ".eide", "eide.yml"));
        Assert.Contains("${ProjectRoot}/LAN_Debug/jlink_flash.py", yaml); Assert.DoesNotContain("EMBEDDED_HELPER_ENTRY", yaml);
        Assert.Contains("\"CustomField\":42", File.ReadAllText(legacy));
        Assert.False(File.Exists(Path.Combine(path, "LAN_Debug", "remote-jlink.json")));
        Assert.Empty(VsCodeIntegration.Plan(project, project.Targets[0], "old.local", 19011, 2331, ToolRelease.Known[0], true).Changes);
    }
    [Fact]
    public void LegacyManagedTasksCanMigrateWithoutTouchingLocalTasksAndAmbiguousConfigsStop()
    {
        var path = CopyProject(); var project = ProjectReader.Read(path);
        ConfigTransaction.Apply(VsCodeIntegration.Plan(project, project.Targets[0], "test.local", 19011, 2331, ToolRelease.Known[0]), Path.Combine(root, "journal"));
        foreach (var relative in new[] { ".vscode/tasks.json", ".vscode/launch.json", ".embedded_helper/integration.json" })
        {
            var file = Path.Combine(path, relative);
            File.WriteAllText(file, File.ReadAllText(file).Replace("LAN_Debug:", "Embedded Helper: LAN").Replace("${workspaceFolder}/" + ProjectToolCache.RelativeEntry, "${env:EMBEDDED_HELPER_ENTRY}"));
        }
        var upgrade = VsCodeIntegration.Plan(project, project.Targets[0], "test.local", 19011, 2331, ToolRelease.Known[0]);
        ConfigTransaction.Apply(upgrade, Path.Combine(root, "journal"));
        var tasks = File.ReadAllText(Path.Combine(path, ".vscode/tasks.json"));
        Assert.DoesNotContain("Embedded Helper: LAN", tasks); Assert.DoesNotContain("EMBEDDED_HELPER_ENTRY", tasks);
        var old = Path.Combine(path, "Debug_LAN"); Directory.CreateDirectory(old); File.WriteAllText(Path.Combine(old, "remote-jlink.json"), "{}");
        var ambiguous = ProjectReader.Read(path);
        Assert.Throws<InvalidDataException>(() => VsCodeIntegration.Plan(ambiguous, ambiguous.Targets[0], "test.local", 19011, 2331, ToolRelease.Known[0]));
    }
}

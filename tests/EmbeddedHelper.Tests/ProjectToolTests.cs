using System.Security.Cryptography;
using System.Text.Json;
using EmbeddedHelper.Core;
using Xunit;

namespace EmbeddedHelper.Tests;

[Collection("Offline client")]
public sealed class ProjectToolTests : TestWorkspace
{
    [Fact]
    public void ProjectToolCopyChecksEveryFileIsIdempotentAndPreservesEditedCache()
    {
        var release = ToolRelease.Known[0]; var data = Path.Combine(root, "data"); var project = CopyProject();
        var source = Path.Combine(data, "tools", "debug-lan", release.Version); Directory.CreateDirectory(Path.Combine(source, "runtime"));
        File.WriteAllText(Path.Combine(source, "debug-lan.exe"), "synthetic bytes; never executed");
        File.WriteAllText(Path.Combine(source, "runtime", "dependency.dll"), "dependency bytes");
        var files = Directory.GetFiles(source, "*", SearchOption.AllDirectories).ToDictionary(p => Path.GetRelativePath(source, p), p => Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(p))).ToLowerInvariant());
        PortableData.SaveFile(Path.Combine(source, "installed.json"), new InstalledTool(release.Version, release.Sha256, release.Commit, files));
        var entry = ProjectToolCache.Prepare(project, data, release);
        Assert.Equal(Path.GetFullPath(Path.Combine(project, ProjectToolCache.RelativeEntry(release))), Path.GetFullPath(entry));
        Assert.Equal(entry, ProjectToolCache.Prepare(project, data, release));
        Assert.Equal("*\n", File.ReadAllText(Path.Combine(Path.GetDirectoryName(entry)!, ".gitignore")));
        File.WriteAllText(entry, "user changed");
        Assert.Throws<InvalidDataException>(() => ProjectToolCache.Prepare(project, data, release));
        Assert.Equal("user changed", File.ReadAllText(entry));
        Assert.Throws<FileNotFoundException>(() => ProjectToolCache.Prepare(project, data, ToolRelease.Known[1]));
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
        Assert.Contains("LAN_Debug/tools/debug-lan/", tasks); Assert.Contains("${workspaceFolder}/Debug_LAN/remote-jlink.json", tasks);
        Assert.DoesNotContain("EMBEDDED_HELPER_ENTRY", tasks); Assert.DoesNotContain(path, tasks); Assert.Contains("--gdb-server", tasks);
        var yaml = File.ReadAllText(Path.Combine(path, ".eide", "eide.yml"));
        Assert.Contains("${ProjectRoot}/LAN_Debug/tools/debug-lan/", yaml); Assert.DoesNotContain("EMBEDDED_HELPER_ENTRY", yaml);
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
            File.WriteAllText(file, File.ReadAllText(file).Replace("LAN_Debug:", "Embedded Helper: LAN").Replace("${workspaceFolder}/" + ProjectToolCache.RelativeEntry(ToolRelease.Known[0]), "${env:EMBEDDED_HELPER_ENTRY}"));
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

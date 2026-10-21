using System.Text.Json;
using System.Text.Json.Nodes;
using EmbeddedHelper.Core;
using Xunit;

namespace EmbeddedHelper.Tests;

[Collection("Offline client")]
public sealed class ProjectsTests : TestWorkspace
{
    [Fact]
    public void RefreshElfsRemovesResolvedDiagnosticAndKeepsOtherTargetsAndWarnings()
    {
        var a = new BuildTarget("Debug", "chip", "SWD", 1000, Path.Combine(root, "debug.elf"));
        var b = a with { Name = "Release", ElfPath = Path.Combine(root, "release.elf") };
        var input = new ProjectInfo(root, "test", "G4", "chip", [a, b], "unused", ["其他配置诊断"]);
        var missing = ProjectReader.RefreshFileDiagnostics(input);
        Assert.Equal(3, missing.Diagnostics.Length);
        File.WriteAllBytes(a.ElfPath, [1]);
        var refreshed = ProjectReader.RefreshFileDiagnostics(missing);
        Assert.Contains("其他配置诊断", refreshed.Diagnostics);
        Assert.DoesNotContain(refreshed.Diagnostics, d => d.Contains("Debug 的 ELF/AXF 不存在"));
        Assert.Contains(refreshed.Diagnostics, d => d.Contains("Release 的 ELF/AXF 不存在"));
        Assert.Equal(3, missing.Diagnostics.Length);
    }
    [Fact]
    public void ArtifactLookupPrefersElfAndRefreshesAfterBuildWithoutSearchingOtherTargets()
    {
        var path = CopyProject();
        var project = ProjectReader.Read(path); var target = Assert.Single(project.Targets);
        var elf = target.ElfPath; var axf = Path.ChangeExtension(elf, ".axf");
        Directory.CreateDirectory(Path.GetDirectoryName(elf)!);
        File.WriteAllText(Path.Combine(Path.GetDirectoryName(elf)!, "other.axf"), "unrelated");
        File.WriteAllText(Path.ChangeExtension(elf, ".viewer.axf"), "viewer copy");
        Assert.Equal(elf, ProjectReader.ResolveElfPath(elf));
        Assert.Contains(project.Diagnostics, d => d.Contains(elf) && d.Contains(axf));
        File.Copy(Path.Combine(fixtures, "structures.elf"), axf);
        Assert.Equal(axf, ProjectReader.ResolveElfPath(elf));
        Assert.Equal(axf, Assert.Single(ProjectReader.Read(path).Targets).ElfPath);
        Assert.DoesNotContain(ProjectReader.RefreshFileDiagnostics(project).Diagnostics, d => d.Contains("ELF/AXF 不存在"));
        var plan = VsCodeIntegration.Plan(project, target, "test.local", 19011, 2331, ToolRelease.Known[0]);
        var launch = plan.Changes.Single(c => c.Path.EndsWith("launch.json"));
        using var launchJson = JsonDocument.Parse(launch.After, ProjectReader.JsonOptions);
        var managed = launchJson.RootElement.GetProperty("configurations").EnumerateArray().Single(c => c.GetProperty("name").GetString()!.StartsWith("LAN_Debug:"));
        Assert.EndsWith(Path.GetFileName(axf), managed.GetProperty("executable").GetString());
        File.WriteAllText(elf, "corrupt elf must not cause axf substitution");
        Assert.Equal(elf, ProjectReader.ResolveElfPath(axf));
        Assert.Equal(elf, Assert.Single(ProjectReader.Read(path).Targets).ElfPath);
        Assert.ThrowsAny<Exception>(() => ElfIndex.Read(elf));
        var release = Path.Combine(path, "Build", "Release", Path.GetFileName(elf));
        Assert.Equal(release, ProjectReader.ResolveElfPath(release));
    }
    [Fact]
    public void ProjectRecognitionDoesNotWriteOrRunHooks()
    {
        var path = CopyProject();
        var before = Directory.GetFiles(path, "*", SearchOption.AllDirectories).ToDictionary(p => p, File.ReadAllBytes);
        var project = ProjectReader.Read(path);
        Assert.Equal("G4", project.Family); Assert.Equal("STM32G474VETx", project.Mcu);
        Assert.Single(project.Targets); Assert.Equal("STM32G474VE", project.Targets[0].Device);
        Assert.Equal("SWD", project.Targets[0].Interface); Assert.Equal(8000, project.Targets[0].SpeedKHz);
        foreach (var (file, bytes) in before) Assert.Equal(bytes, File.ReadAllBytes(file));
        var workspace = Path.Combine(path, "project.code-workspace");
        File.WriteAllText(workspace, "{\"folders\":[{\"path\":\".\"}]}");
        Assert.Equal(project.Root, ProjectReader.Read(workspace).Root);
    }
}

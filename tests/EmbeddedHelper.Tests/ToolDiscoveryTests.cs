using System.Reflection.PortableExecutable;
using System.Text.Json;
using EmbeddedHelper.Core;
using Xunit;

namespace EmbeddedHelper.Tests;

public sealed class ToolDiscoveryTests : IDisposable
{
    private readonly string root = Path.Combine(Path.GetTempPath(), "tool-discovery-" + Guid.NewGuid().ToString("N"));
    public ToolDiscoveryTests() => Directory.CreateDirectory(root);
    public void Dispose() => Directory.Delete(root, true);
    private string FileAt(string relative, string text = "fixture")
    {
        var path = Path.GetFullPath(Path.Combine(root, relative)); Directory.CreateDirectory(Path.GetDirectoryName(path)!); File.WriteAllText(path, text); return path;
    }
    private static ToolCandidate Accept(string path, string source) => new(path, source, "test-version", File.Exists(path), "fixture validator");
    private ToolSearchContext Context(string? project = null, string[]? settings = null, string[]? paths = null) => new(root, project, settings ?? [], [], paths ?? []);

    [Fact]
    public void ProjectAndUserSettingsResolvePathsAndDeduplicateWithoutExecutingCommands()
    {
        var project = Path.Combine(root, "project with spaces");
        var dll = FileAt("project with spaces/tools/JLink_x64.dll");
        FileAt("project with spaces/.vscode/launch.json", "{ // comment\n\"configurations\":[{\"serverpath\":\"ignore.exe\",\"cortex-debug.JLinkGDBServerPath.windows\":\"${workspaceFolder}/tools/JLinkGDBServerCL.exe\"}],}");
        var user = FileAt("settings.json", JsonSerializer.Serialize(new Dictionary<string, string> { ["EIDE.JLink.ExePath"] = dll, ["EIDE.JLink.OtherPath"] = "${command:runMe}/JLink.exe" }));
        var result = ToolDiscovery.Find(LocalToolKind.JLink, "", Context(project, [user], [Path.GetDirectoryName(dll)!]), Accept);
        var candidate = Assert.Single(result.Candidates);
        Assert.Equal(dll, candidate.Path); Assert.Equal("工程调试设置", candidate.Source);
        Assert.Contains(result.Notes, note => note.Contains("未解析变量"));
        Assert.Equal(dll, ToolDiscovery.Suggest("", result));
    }
    [Fact]
    public void MultipleVersionsRequireSelectionAndMissingSavedPathIsNeverReplaced()
    {
        var first = FileAt("one/JLink_x64.dll"); var second = FileAt("two/JLink_x64.dll");
        var context = Context(paths: [Path.GetDirectoryName(first)!, Path.GetDirectoryName(second)!]);
        var result = ToolDiscovery.Find(LocalToolKind.JLink, "", context, Accept);
        Assert.Equal(2, result.Candidates.Length); Assert.Equal("", ToolDiscovery.Suggest("", result));
        var missing = Path.Combine(root, "removed/JLink_x64.dll");
        result = ToolDiscovery.Find(LocalToolKind.JLink, missing, context, Accept);
        Assert.False(result.Candidates[0].Valid);
        Assert.Equal(missing, ToolDiscovery.Suggest(missing, result));
        Assert.Equal(second, ToolDiscovery.Suggest(second, result));
    }
    [Fact]
    public void ReadElfFindsCustomCompilerBinAndMalformedConfigDoesNotBlockFallback()
    {
        var exe = FileAt("compiler/bin/arm-none-eabi-readelf.exe");
        var config = FileAt("settings.json", "{\"EIDE.ARM.GCC.InstallDirectory\":\"${userHome}/compiler\"}");
        var invalid = FileAt("bad.json", "{broken");
        var result = ToolDiscovery.Find(LocalToolKind.ReadElf, "", Context(settings: [invalid, config]), Accept);
        Assert.Equal(exe, Assert.Single(result.Candidates).Path);
        Assert.Contains(result.Notes, n => n.Contains("无法读取"));
    }
    [Fact]
    public void DefaultEideLocationIsFoundButUnrelatedDirectoriesAreNotScanned()
    {
        var dll = FileAt(".eide/tools/jlink/JLink_x64.dll"); FileAt("unrelated/JLink_x64.dll");
        var result = ToolDiscovery.Find(LocalToolKind.JLink, "", Context(), Accept);
        Assert.Equal(dll, Assert.Single(result.Candidates).Path);
    }
    [Fact]
    public void ValidationRejectsMissingTextManagedDllAndWrongArchitecture()
    {
        Assert.False(ToolDiscovery.Inspect(LocalToolKind.JLink, Path.Combine(root, "missing"), "test").Valid);
        Assert.False(ToolDiscovery.Inspect(LocalToolKind.JLink, FileAt("fake.dll"), "test").Valid);
        Assert.False(ToolDiscovery.Inspect(LocalToolKind.JLink, typeof(ToolDiscovery).Assembly.Location, "test").Valid);
        // A known managed image is altered only in this isolated copy; no binary is executed.
        var bytes = File.ReadAllBytes(typeof(ToolDiscovery).Assembly.Location);
        var offset = BitConverter.ToInt32(bytes, 0x3c);
        BitConverter.GetBytes((ushort)Machine.Arm64).CopyTo(bytes, offset + 4);
        var path = FileAt("arm64.exe"); File.WriteAllBytes(path, bytes);
        Assert.False(ToolDiscovery.Inspect(LocalToolKind.ReadElf, path, "test").Valid);
    }
}

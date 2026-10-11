using System.Text.Json;
using System.Text.Json.Nodes;
using EmbeddedHelper.Core;
using Xunit;

namespace EmbeddedHelper.Tests;

[Collection("Offline client")]
public sealed class ToolsTests : TestWorkspace
{
    [Theory]
    [InlineData("../escape.exe")]
    [InlineData("/absolute.exe")]
    [InlineData("C:/drive.exe")]
    [InlineData("a/../escape.exe")]
    [InlineData("CON.txt")]
    [InlineData("nested/trailing. ")]
    public void ToolArchiveRejectsWindowsTraversalAndReservedNames(string name)
    {
        using var bytes = new MemoryStream();
        using (var create = new System.IO.Compression.ZipArchive(bytes, System.IO.Compression.ZipArchiveMode.Create, true)) create.CreateEntry(name);
        bytes.Position = 0;
        using var archive = new System.IO.Compression.ZipArchive(bytes, System.IO.Compression.ZipArchiveMode.Read);
        Assert.Throws<InvalidDataException>(() => ToolCache.ValidateArchive(archive));
    }
    [Fact]
    public async Task ToolCacheRejectsWrongDigestBeforeExecutingAndLocksExactVersion()
    {
        var zip = Path.Combine(root, "untrusted.zip"); File.WriteAllText(zip, "invalid");
        await Assert.ThrowsAsync<InvalidDataException>(() => new ToolCache(root).InstallAsync(zip, ToolRelease.Known[0]));
        var project = CopyProject();
        foreach (var release in ToolRelease.Known)
        {
            var change = ToolCache.SelectVersion(project, release);
            ConfigTransaction.Apply(new ConfigPlan(project, [change]), Path.Combine(root, "journal"));
            Assert.Equal(release, ToolCache.ReadLock(project));
        }
        var lockPath = Path.Combine(project, ".embedded_helper", "tools.lock.json");
        File.WriteAllText(lockPath, File.ReadAllText(lockPath).Replace(ToolRelease.Known[1].Sha256, new string('0', 64)));
        Assert.Throws<InvalidDataException>(() => ToolCache.ReadLock(project));
    }
}

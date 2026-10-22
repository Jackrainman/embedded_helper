using System.IO.Compression;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Nodes;
using EmbeddedHelper.Core;
using Xunit;

namespace EmbeddedHelper.Tests;

[Collection("Offline client")]
public sealed class AuditBoundaryTests : TestWorkspace
{
    [Theory]
    [InlineData("sourceRepository", "untrusted/source")]
    [InlineData("packageName", "wrong.zip")]
    [InlineData("version", "invalid")]
    [InlineData("packageSha256", "invalid")]
    [InlineData("pythonMinimum", "2.7")]
    [InlineData("manifestSchema", "99")]
    [InlineData("packageBytes", "0")]
    [InlineData("packageBytes", "2097153")]
    public void ManifestRejectsUntrustedFieldsAndSizeLimits(string field, string value)
    {
        var release = ToolRelease.Known[0];
        var node = JsonSerializer.SerializeToNode(new
        {
            manifestSchema = 2,
            toolId = ToolRelease.ToolId,
            sourceRepository = ToolRelease.Repository,
            version = release.Version,
            upstreamCommit = release.Commit,
            entrypoint = "jlink_flash.py",
            pythonMinimum = "3.10",
            packageName = release.AssetName,
            packageBytes = 10,
            packageSha256 = release.Sha256
        })!;
        node[field] = field is "manifestSchema" or "packageBytes" ? JsonValue.Create(int.Parse(value)) : JsonValue.Create(value);
        Assert.Throws<InvalidDataException>(() => ToolRelease.FromManifest(node.ToJsonString()));
    }
    [Fact]
    public void JsoncRootDuplicatesNestedNamesAndTrailingCommentAreHandled()
    {
        Assert.Throws<InvalidDataException>(() => JsonEdit.Set("{\"a\":1,\"a\":2}", "a", JsonValue.Create(3)));
        const string text = "{\"nested\":{\"a\":1},\"a\":2 // keep comment\n}";
        var replaced = JsonEdit.Set(text, "a", JsonValue.Create(3));
        Assert.Equal("{\"nested\":{\"a\":1},\"a\":3 // keep comment\n}", replaced);
        var inserted = JsonEdit.Set(replaced, "new", JsonValue.Create(true));
        Assert.Contains("\"a\":3, // keep comment", inserted);
        using var doc = JsonDocument.Parse(inserted, ProjectReader.JsonOptions);
        Assert.True(doc.RootElement.GetProperty("new").GetBoolean());
        Assert.Equal(1, doc.RootElement.GetProperty("nested").GetProperty("a").GetInt32());
    }
    [Fact]
    public void EntirelyNewRemoteConfigurationCanBeAppliedReadAndRepeated()
    {
        var path = CopyProject(); File.Delete(Path.Combine(path, "remote-jlink.json"));
        var project = ProjectReader.Read(path); Assert.False(File.Exists(project.RemotePath));
        var plan = VsCodeIntegration.Plan(project, project.Targets[0], "offline.invalid", 19011, 2331, ToolRelease.Known[0], true);
        ConfigTransaction.Apply(plan, Path.Combine(root, "journals"));
        var reread = ProjectReader.Read(path);
        using var saved = JsonDocument.Parse(File.ReadAllText(reread.RemotePath));
        Assert.Equal("offline.invalid", saved.RootElement.GetProperty("ServerName").GetString());
        Assert.Equal(19011, saved.RootElement.GetProperty("Port").GetInt32());
        Assert.False(string.IsNullOrWhiteSpace(saved.RootElement.GetProperty("Device").GetString()));
        Assert.False(string.IsNullOrWhiteSpace(saved.RootElement.GetProperty("Interface").GetString()));
        Assert.True(saved.RootElement.GetProperty("SpeedKHz").GetInt32() > 0);
        Assert.Empty(VsCodeIntegration.Plan(reread, reread.Targets[0], "offline.invalid", 19011, 2331, ToolRelease.Known[0], true).Changes);
    }
    [Theory]
    [InlineData("after-end")]
    [InlineData("duplicate")]
    [InlineData("backwards")]
    public void InvalidRecordingSequenceKeepsSourceAndPreviousExport(string mode)
    {
        var source = Path.Combine(root, "record.jsonl"); var output = Path.Combine(root, "existing.xlsx");
        var variable = new Variable("global:x", "x", 0x20000000, "int32", 4, "可读取");
        ObservationEvent Sample(long n) => new("sample", "", new() { [variable.Identity] = "1" }, Sequence: n, SessionId: "offline");
        using (var record = new ObservationRecording(source, [variable], 200, "offline"))
        {
            record.Append(Sample(2));
            if (mode == "after-end") record.Complete();
            record.Append(Sample(mode == "duplicate" ? 2 : mode == "backwards" ? 1 : 3)); record.Complete();
        }
        File.WriteAllText(output, "previous export"); var before = File.ReadAllBytes(source);
        var error = Assert.Throws<InvalidDataException>(() => ObservationExcel.Export(source, output));
        Assert.Contains(mode == "after-end" ? "结束标记之后" : "序号未递增", error.Message);
        Assert.Equal(before, File.ReadAllBytes(source)); Assert.Equal("previous export", File.ReadAllText(output));
        Assert.Empty(Directory.GetFiles(root, "*.tmp"));
    }
    [Theory]
    [InlineData("jlink_flash.py")]
    [InlineData("vscode/tasks.json")]
    [InlineData("version")]
    [InlineData("commit")]
    [InlineData("packageSha256")]
    public async Task InstalledCacheRejectsTamperingWithoutOverwritingIt(string field)
    {
        var package = ScriptPackage(); var cache = new ToolCache(root);
        var entry = await cache.InstallAsync(package.Zip, package.Release); var directory = Path.GetDirectoryName(entry)!;
        var path = Path.Combine(directory, field.Contains('/') || field.EndsWith(".py") ? field : "installed.json");
        if (path.EndsWith("installed.json"))
        {
            var marker = JsonNode.Parse(File.ReadAllText(path))!; marker[field] = "tampered"; File.WriteAllText(path, marker.ToJsonString());
        }
        else File.WriteAllText(path, "tampered");
        var bytes = File.ReadAllBytes(path);
        Assert.Throws<InvalidDataException>(() => cache.Resolve(package.Release));
        Assert.Equal(bytes, File.ReadAllBytes(path));
    }
    [Theory]
    [InlineData("missing")]
    [InlineData("version")]
    [InlineData("utf8")]
    [InlineData("expanded-limit")]
    public async Task InvalidScriptArchiveIsRejectedAndPrivateStagingIsRemoved(string mode)
    {
        var package = ScriptPackage();
        var version = package.Release.Version + "x";
        using (var zip = ZipFile.Open(package.Zip, ZipArchiveMode.Update))
        {
            zip.GetEntry("VERSION")!.Delete();
            using (var writer = new StreamWriter(zip.CreateEntry("VERSION").Open())) writer.Write(version);
            var name = mode == "version" ? "VERSION" : "README.md"; zip.GetEntry(name)!.Delete();
            if (mode != "missing")
            {
                using var output = zip.CreateEntry(name).Open();
                output.Write(mode == "utf8" ? new byte[] { 0xff, 0xff } : mode == "expanded-limit" ? new byte[2 * 1024 * 1024 + 1] : "wrong"u8.ToArray());
            }
        }
        // A distinct explicitly imported manifest describes these exact bytes, so install reaches content checks.
        var release = package.Release with { Version = version, Sha256 = Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(package.Zip))).ToLowerInvariant() };
        // Register a separate version without colliding with the initial helper's accepted manifest.
        ToolRelease.ImportManifest(JsonSerializer.Serialize(new
        {
            manifestSchema = 2,
            toolId = ToolRelease.ToolId,
            sourceRepository = ToolRelease.Repository,
            version,
            upstreamCommit = release.Commit,
            entrypoint = "jlink_flash.py",
            pythonMinimum = "3.10",
            packageName = release.AssetName,
            packageBytes = new FileInfo(package.Zip).Length,
            packageSha256 = release.Sha256
        }));
        var error = await Record.ExceptionAsync(() => new ToolCache(root).InstallAsync(package.Zip, release));
        Assert.NotNull(error);
        if (mode == "utf8") Assert.IsType<System.Text.DecoderFallbackException>(error);
        else Assert.IsType<InvalidDataException>(error);
        Assert.Empty(Directory.GetDirectories(Path.Combine(root, "tools", ToolRelease.ToolId), ".install-*"));
        Assert.False(Directory.Exists(Path.Combine(root, "tools", ToolRelease.ToolId, version)));
    }
    [Theory]
    [InlineData("source")]
    [InlineData("count")]
    [InlineData("size")]
    [InlineData("conflict")]
    public void LocalCatalogRejectsInvalidSourceLimitsAndConflicts(string mode)
    {
        var path = Path.Combine(root, "tools", "LAN_Debug_client-catalog.json"); Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        var releases = mode == "count" ? Enumerable.Repeat(ToolRelease.Known[0], 101).ToArray() : new[] { ToolRelease.Known[0] with { Commit = mode == "conflict" ? new string('b', 40) : ToolRelease.Known[0].Commit } };
        var text = mode == "size" ? new string(' ', 1024 * 1024 + 1) : JsonSerializer.Serialize(new { sourceRepository = mode == "source" ? "untrusted/source" : ToolRelease.Repository, releases }, Product.Json);
        File.WriteAllText(path, text);
        Assert.Throws<InvalidDataException>(() => ToolCatalog.Load(root)); Assert.Equal(text, File.ReadAllText(path));
    }
}

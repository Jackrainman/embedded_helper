using System.Net;
using System.Text.Json;
using EmbeddedHelper.Core;
using Xunit;

namespace EmbeddedHelper.Tests;

[Collection("Offline client")]
public sealed class ToolCatalogTests : TestWorkspace
{
    [Theory]
    [InlineData("size")]
    [InlineData("digest")]
    [InlineData("commit")]
    [InlineData("valid")]
    public async Task RemoteCandidateIsAcceptedOnlyAfterAllChecks(string mode)
    {
        var version = "9.8.1-test." + Guid.NewGuid().ToString("N");
        using var handler = new ResponseHandler(url => url.Contains("/releases?") ? JsonSerializer.Serialize(new[] { new
        {
            tag_name = ToolRelease.ToolId + "-v" + version, draft = false,
            assets = new[] { new { name = ToolRelease.ToolId + "-" + version + ".zip", size = mode == "size" ? 11 : 10, digest = "sha256:" + new string(mode == "digest" ? 'c' : 'b', 64) }, new { name = "release-manifest.json", size = 100, digest = "" } }
        } }) : url.EndsWith("release-manifest.json") ? Manifest(version) : JsonSerializer.Serialize(new { @object = new { type = "commit", sha = new string(mode == "commit" ? 'c' : 'a', 40) } }));
        if (mode == "valid")
        {
            await ToolCatalog.RefreshAsync(root, handler);
            Assert.Equal(version, ToolRelease.Find(version).Version);
            Assert.Throws<InvalidDataException>(() => ToolRelease.ImportManifest(Manifest(version).Replace(new string('b', 64), new string('c', 64))));
        }
        else
        {
            await Assert.ThrowsAsync<InvalidDataException>(() => ToolCatalog.RefreshAsync(root, handler));
            Assert.DoesNotContain(ToolRelease.Available, r => r.Version == version);
            Assert.Throws<InvalidDataException>(() => ToolRelease.Find(version));
            ToolCatalog.Save(root);
            Assert.DoesNotContain(version, File.ReadAllText(Path.Combine(root, "tools/LAN_Debug_client-catalog.json")));
        }
    }
    private sealed class ResponseHandler(Func<string, string> response) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) =>
            Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(response(request.RequestUri!.AbsoluteUri)) });
    }
    private static string Manifest(string version) => JsonSerializer.Serialize(new
    {
        manifestSchema = 2,
        toolId = ToolRelease.ToolId,
        sourceRepository = ToolRelease.Repository,
        version,
        upstreamCommit = new string('a', 40),
        entrypoint = "jlink_flash.py",
        pythonMinimum = "3.10",
        packageName = ToolRelease.ToolId + "-" + version + ".zip",
        packageBytes = 10,
        packageSha256 = new string('b', 64)
    });
    [Fact]
    public void ParsingManifestDoesNotAcceptIt()
    {
        var version = "9.8.0-test." + Guid.NewGuid().ToString("N");
        _ = ToolRelease.FromManifest(Manifest(version));
        Assert.DoesNotContain(ToolRelease.Available, r => r.Version == version);
        Assert.Throws<InvalidDataException>(() => ToolRelease.Find(version));
        ToolCatalog.Save(root);
        Assert.DoesNotContain(version, File.ReadAllText(Path.Combine(root, "tools/LAN_Debug_client-catalog.json")));
    }
}

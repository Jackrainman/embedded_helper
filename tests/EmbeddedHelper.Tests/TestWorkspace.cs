using System.Text.Json;
using System.Text.Json.Nodes;
using EmbeddedHelper.Core;
using Xunit;

namespace EmbeddedHelper.Tests;

[CollectionDefinition("Offline client")]
public sealed class OfflineClientCollection { }

public abstract class TestWorkspace : IDisposable
{
    protected readonly string root;
    protected readonly string fixtures;
    protected TestWorkspace()
    {
        var repository = new DirectoryInfo(AppContext.BaseDirectory);
        while (repository != null && !File.Exists(Path.Combine(repository.FullName, "EmbeddedHelper.slnx"))) repository = repository.Parent;
        if (repository == null) throw new DirectoryNotFoundException("repository");
        fixtures = Path.Combine(repository.FullName, "tests", "fixtures");
        root = Path.Combine(repository.FullName, "tmp", "csharp-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
    }
    protected string CopyProject(string fixture = "G4 中文 空格")
    {
        var source = Path.Combine(fixtures, fixture);
        var target = Path.Combine(root, "工程 含空格");
        foreach (var file in Directory.GetFiles(source, "*", SearchOption.AllDirectories))
        {
            var destination = Path.Combine(target, Path.GetRelativePath(source, file));
            Directory.CreateDirectory(Path.GetDirectoryName(destination)!); File.Copy(file, destination);
        }
        return target;
    }
    protected (string Zip, ToolRelease Release) ScriptPackage(string? version = null)
    {
        version ??= "9.9.0-test." + Guid.NewGuid().ToString("N");
        var zip = Path.Combine(root, "LAN_Debug_client-" + version + ".zip");
        using (var archive = System.IO.Compression.ZipFile.Open(zip, System.IO.Compression.ZipArchiveMode.Create))
            foreach (var name in new[] { "jlink_flash.py", "VERSION", "README.md", "remote-jlink.example.json", "vscode/tasks.json", "vscode/launch.json" })
            {
                using var stream = new StreamWriter(archive.CreateEntry(name).Open());
                stream.Write(name == "VERSION" ? version + "\n" : name == "jlink_flash.py" ? "# synthetic script; never executed\n" : "{}");
            }
        var sha = Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(File.ReadAllBytes(zip))).ToLowerInvariant();
        var manifest = JsonSerializer.Serialize(new
        {
            manifestSchema = 2,
            toolId = ToolRelease.ToolId,
            sourceRepository = ToolRelease.Repository,
            version,
            upstreamCommit = new string('a', 40),
            entrypoint = "jlink_flash.py",
            pythonMinimum = "3.10",
            packageName = Path.GetFileName(zip),
            packageBytes = new FileInfo(zip).Length,
            packageSha256 = sha
        });
        return (zip, ToolRelease.ImportManifest(manifest));
    }
    public void Dispose() => Directory.Delete(root, true);
}

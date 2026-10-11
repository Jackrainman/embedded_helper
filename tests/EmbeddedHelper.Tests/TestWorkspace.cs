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
    public void Dispose() => Directory.Delete(root, true);
}

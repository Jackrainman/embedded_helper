namespace EmbeddedHelper.Core;

public sealed record RuntimePaths(string HomeDirectory, string ComponentDirectory)
{
    public static RuntimePaths Current { get; } = FromComponents(AppContext.BaseDirectory);
    public string DataDirectory => Path.Combine(HomeDirectory, "data");
    public string WorkerExecutable => Path.Combine(ComponentDirectory, "EmbeddedHelper.Worker.exe");
    public bool IsPortable => !Path.TrimEndingDirectorySeparator(HomeDirectory).Equals(Path.TrimEndingDirectorySeparator(ComponentDirectory), StringComparison.OrdinalIgnoreCase);
    public static RuntimePaths FromComponents(string directory)
    {
        var components = Path.TrimEndingDirectorySeparator(Path.GetFullPath(directory));
        var home = File.Exists(Path.Combine(components, ".portable-runtime")) ? Path.GetFullPath(Path.Combine(components, "../../..")) : components;
        return new(home, components);
    }
}

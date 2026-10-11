namespace EmbeddedHelper.Core;

public sealed record RuntimePaths(string HomeDirectory, string ComponentDirectory)
{
    public const string EntryVariable = "EMBEDDED_HELPER_ENTRY";
    public const string LauncherVariable = "EMBEDDED_HELPER_LAUNCHER";
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
    public string ExternalEntry(string? launcher)
    {
        if (!IsPortable) return Path.Combine(ComponentDirectory, "embedded-helper-cli.exe");
        var entry = Path.GetFullPath(launcher ?? Path.Combine(HomeDirectory, "EmbeddedHelper.exe"));
        if (!string.Equals(Path.GetDirectoryName(entry), HomeDirectory, StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException("便携启动器必须位于软件目录。");
        return entry;
    }
}

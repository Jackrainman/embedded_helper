using System.Text.Json;

namespace EmbeddedHelper.Core;

/// <summary>Project-relative executable cache; shared task files never contain installation paths.</summary>
public static class ProjectToolCache
{
    public static string RelativeEntry(ToolRelease release) => $"LAN_Debug/tools/debug-lan/{ToolRelease.Find(release.Version).Version}/debug-lan.exe";
    public static string Resolve(string projectRoot, ToolRelease release) => new ToolCache(Path.Combine(projectRoot, "LAN_Debug")).Resolve(release);

    public static string Prepare(string projectRoot, string dataRoot, ToolRelease release)
    {
        var source = Path.GetDirectoryName(new ToolCache(dataRoot).Resolve(release))!;
        var destination = Path.GetDirectoryName(Path.Combine(projectRoot, RelativeEntry(release)))!;
        ConfigTransaction.CheckPath(projectRoot, destination);
        if (Directory.Exists(destination)) return Resolve(projectRoot, release);
        var parent = Path.GetDirectoryName(destination)!;
        Directory.CreateDirectory(parent);
        var staging = Path.Combine(parent, ".prepare-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(staging);
        try
        {
            var marker = File.ReadAllText(Path.Combine(source, "installed.json"));
            var installed = JsonSerializer.Deserialize<InstalledTool>(marker, Product.Json) ?? throw new InvalidDataException("工具安装记录无效。");
            foreach (var relative in installed.Files.Keys)
            {
                var input = Path.Combine(source, relative); ConfigTransaction.CheckPath(source, input);
                var output = Path.Combine(staging, relative); ConfigTransaction.CheckPath(staging, output);
                Directory.CreateDirectory(Path.GetDirectoryName(output)!);
                File.Copy(input, output, false);
                using var copied = File.OpenRead(output);
                if (Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(copied)).ToLowerInvariant() != installed.Files[relative])
                    throw new InvalidDataException("复制工具时文件内容发生变化，请重试。");
            }
            File.WriteAllText(Path.Combine(staging, "installed.json"), marker);
            // Generated binaries are local preparation, not shared project configuration.
            using (var ignore = new StreamWriter(new FileStream(Path.Combine(staging, ".gitignore"), FileMode.CreateNew, FileAccess.Write))) ignore.Write("*\n");
            Directory.Move(staging, destination);
            return Resolve(projectRoot, release);
        }
        finally
        {
            if (Directory.Exists(staging)) { ConfigTransaction.CheckPath(parent, staging); Directory.Delete(staging, true); }
        }
    }
}

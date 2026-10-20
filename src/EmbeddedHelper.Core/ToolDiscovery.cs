using System.Diagnostics;
using System.Reflection.PortableExecutable;
using System.Text.Json;

namespace EmbeddedHelper.Core;

public enum LocalToolKind { JLink, ReadElf }
public sealed record ToolCandidate(string Path, string Source, string Version, bool Valid, string Status)
{
    public string Label => $"{Source} · {Version} · {Status}";
}
public sealed record ToolSearchContext(string UserHome, string? ProjectRoot, string[] SettingsFiles, string[] InstallRoots, string[] PathDirectories);
public sealed record ToolSearchResult(ToolCandidate[] Candidates, string[] Notes);

public static class ToolDiscovery
{
    public static string FileName(LocalToolKind kind) => kind == LocalToolKind.JLink ? "JLink_x64.dll" : "arm-none-eabi-readelf.exe";
    public static ToolSearchContext Current(string? projectRoot = null)
    {
        var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        var roaming = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
        return new(home, projectRoot,
            [Path.Combine(roaming, "Code", "User", "settings.json"), Path.Combine(roaming, "Code - Insiders", "User", "settings.json")],
            [Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "SEGGER"), Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86), "SEGGER")],
            (Environment.GetEnvironmentVariable("PATH") ?? "").Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries));
    }
    // Discovery reads known configuration only. It never executes configuration commands or loads DLLs.
    public static ToolSearchResult Find(LocalToolKind kind, string saved, ToolSearchContext context, Func<string, string, ToolCandidate>? inspect = null)
    {
        inspect ??= (path, source) => Inspect(kind, path, source);
        var candidates = new List<ToolCandidate>(); var notes = new List<string>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        void Add(string value, string source, bool keepMissing = false)
        {
            try
            {
                var path = Path.GetFullPath(value);
                if ((keepMissing || File.Exists(path)) && seen.Add(path)) candidates.Add(inspect(path, source));
            }
            catch (Exception ex) when (ex is ArgumentException or IOException or UnauthorizedAccessException or NotSupportedException)
            { notes.Add(source + "：无法检查路径"); }
        }
        void Hint(string value, string source, string root)
        {
            value = value.Replace("${userHome}", context.UserHome).Replace("${workspaceFolder}", root);
            value = Environment.ExpandEnvironmentVariables(value).Trim().Trim('"');
            if (value.Contains("${") || string.IsNullOrWhiteSpace(value)) { notes.Add(source + "：含未解析变量，需手动指定"); return; }
            try
            {
                var path = Path.GetFullPath(value, root);
                var dir = Directory.Exists(path) || !Path.HasExtension(path) ? path : Path.GetDirectoryName(path)!;
                Add(Path.Combine(dir, FileName(kind)), source);
                Add(Path.Combine(dir, "bin", FileName(kind)), source);
            }
            catch (Exception ex) when (ex is ArgumentException or IOException or NotSupportedException) { notes.Add(source + "：路径格式无效"); }
        }
        void Config(string file, string source, string root)
        {
            if (!File.Exists(file)) return;
            try
            {
                if (new FileInfo(file).Length > 2 * 1024 * 1024) { notes.Add(source + "：配置过大，已跳过"); return; }
                using var document = JsonDocument.Parse(File.ReadAllText(file), ProjectReader.JsonOptions);
                void Visit(JsonElement element)
                {
                    if (element.ValueKind == JsonValueKind.Object)
                        foreach (var property in element.EnumerateObject())
                        {
                            var key = property.Name.ToLowerInvariant();
                            var accepted = kind == LocalToolKind.JLink
                                ? key == "jlinkexe" || (key.Contains("jlink") && (key.Contains("path") || key.Contains("installdirectory"))) || (key == "serverpath" && element.TryGetProperty("servertype", out var serverType) && serverType.ValueKind == JsonValueKind.String && serverType.GetString() == "jlink")
                                : (key.Contains("gcc") || key.Contains("armtoolchain") || key.Contains("readelf")) && (key.Contains("path") || key.Contains("installdirectory"));
                            if (accepted && property.Value.ValueKind == JsonValueKind.String) Hint(property.Value.GetString()!, source, root);
                            else Visit(property.Value);
                        }
                    else if (element.ValueKind == JsonValueKind.Array)
                        foreach (var item in element.EnumerateArray()) Visit(item);
                }
                Visit(document.RootElement);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException) { notes.Add(source + "：配置无法读取，已跳过"); }
        }
        if (!string.IsNullOrWhiteSpace(saved)) Add(saved, "当前指定", true);
        if (context.ProjectRoot is { } project)
        {
            Config(Path.Combine(project, ".vscode", "settings.json"), "工程 VS Code 设置", project);
            Config(Path.Combine(project, ".vscode", "launch.json"), "工程调试设置", project);
            Config(Path.Combine(project, "remote-jlink.json"), "工程 J-Link 设置", project);
        }
        foreach (var file in context.SettingsFiles) Config(file, "VS Code 用户设置", context.ProjectRoot ?? context.UserHome);
        Add(Path.Combine(context.UserHome, ".eide", "tools", kind == LocalToolKind.JLink ? "jlink" : "gcc_arm", kind == LocalToolKind.JLink ? "" : "bin", FileName(kind)), "EIDE 默认工具目录");
        if (kind == LocalToolKind.JLink)
            foreach (var root in context.InstallRoots.Where(Directory.Exists))
                try
                {
                    Add(Path.Combine(root, FileName(kind)), "SEGGER 安装目录");
                    foreach (var dir in Directory.EnumerateDirectories(root, "JLink*").Take(100).Order(StringComparer.OrdinalIgnoreCase))
                        Add(Path.Combine(dir, FileName(kind)), "SEGGER 安装目录");
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { notes.Add("SEGGER 目录无法读取"); }
        foreach (var dir in context.PathDirectories.Take(200))
            if (Path.IsPathFullyQualified(dir.Trim('"'))) Add(Path.Combine(dir.Trim('"'), FileName(kind)), "PATH 工具目录");
        return new(candidates.ToArray(), notes.Distinct().ToArray());
    }
    // Keep an explicit selection, including a broken one; never silently upgrade or replace it.
    public static string Suggest(string current, ToolSearchResult result) => !string.IsNullOrWhiteSpace(current) ? current
        : result.Candidates.Where(c => c.Valid).Take(2).ToArray() is [var only] ? only.Path : "";

    public static ToolCandidate Inspect(LocalToolKind kind, string path, string source)
    {
        try
        {
            if (!File.Exists(path)) return new(path, source, "版本未知", false, "文件不存在");
            using var stream = File.OpenRead(path);
            using var pe = new PEReader(stream);
            var header = pe.PEHeaders.CoffHeader;
            var dll = header.Characteristics.HasFlag(Characteristics.Dll);
            if (kind == LocalToolKind.JLink && (header.Machine != Machine.Amd64 || !dll))
                return new(path, source, "版本未知", false, "需要64位 J-Link DLL");
            if (kind == LocalToolKind.ReadElf && (dll || header.Machine is not (Machine.Amd64 or Machine.I386)))
                return new(path, source, "版本未知", false, "需要 Windows x86/x64 可执行工具");
            if (kind == LocalToolKind.JLink)
            {
                var directory = pe.PEHeaders.PEHeader?.ExportTableDirectory ?? default;
                if (directory.RelativeVirtualAddress == 0) throw new BadImageFormatException("缺少导出接口");
                var reader = pe.GetSectionData(directory.RelativeVirtualAddress).GetReader();
                reader.Offset = 24; var count = reader.ReadUInt32(); reader.ReadUInt32(); var names = reader.ReadInt32();
                if (count > 100000) throw new BadImageFormatException("导出表无效");
                var table = pe.GetSectionData(names).GetReader(); var exports = new HashSet<string>();
                for (uint i = 0; i < count; i++)
                {
                    var name = pe.GetSectionData(table.ReadInt32()).GetReader();
                    var bytes = new List<byte>();
                    while (bytes.Count < 256) { var b = name.ReadByte(); if (b == 0) break; bytes.Add(b); }
                    exports.Add(System.Text.Encoding.ASCII.GetString(bytes.ToArray()));
                }
                string[] required = ["JLINKARM_Open", "JLINKARM_Close", "JLINKARM_Connect", "JLINKARM_ReadMemEx", "JLINKARM_SelectIP", "JLINKARM_EMU_SelectByUSBSN", "JLINKARM_TIF_Select", "JLINKARM_SetSpeed", "JLINKARM_ExecCommand", "JLINK_RTTERMINAL_Control", "JLINK_RTTERMINAL_Read"];
                if (required.Any(name => !exports.Contains(name))) return new(path, source, "版本未知", false, "缺少所需 J-Link 接口");
            }
            var version = FileVersionInfo.GetVersionInfo(path).FileVersion;
            return new(path, source, string.IsNullOrWhiteSpace(version) ? "版本未标注" : version, true,
                kind == LocalToolKind.JLink ? "文件校验通过（未加载/未连接）" : "文件校验通过（未运行）");
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or BadImageFormatException or ArgumentException or InvalidOperationException)
        { return new(path, source, "版本未知", false, "文件无效或无法读取"); }
    }
}

using System.IO;
using System.Security.Cryptography;
using EmbeddedHelper.Core;

namespace EmbeddedHelper.App;

internal static class ElfIndexService
{
    public static Task<Variable[]> ReadAsync(string path, string tool) => Task.Run(() =>
    {
        if (string.IsNullOrWhiteSpace(path)) throw new FileNotFoundException("尚未选择 ELF。");
        var info = new FileInfo(path);
        if (!info.Exists) throw new FileNotFoundException("ELF 文件不存在。", path);
        if (info.Length > 256 * 1024 * 1024) throw new InvalidDataException("ELF 超过 256 MiB 解析上限。");
        if (info.Length < 6) throw new InvalidDataException("ELF 文件为空或已损坏。");
        static string Digest(string file) { using var stream = File.OpenRead(file); return Convert.ToHexString(SHA256.HashData(stream)); }
        var before = Digest(path);
        var result = ElfIndex.Read(path, tool);
        if (before != Digest(path)) throw new IOException("解析期间 ELF 发生变化，请等待构建完成后重试。");
        return result;
    });
}

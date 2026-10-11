using System.IO;
using System.Security.Cryptography;
using EmbeddedHelper.Core;

namespace EmbeddedHelper.App;

internal static class ElfIndexService
{
    public static Task<Variable[]> ReadAsync(string path, string tool) => Task.Run(() =>
    {
        static string Digest(string file) { using var stream = File.OpenRead(file); return Convert.ToHexString(SHA256.HashData(stream)); }
        var before = Digest(path);
        var result = ElfIndex.Read(path, tool);
        if (before != Digest(path)) throw new IOException("解析期间 ELF 发生变化，请等待构建完成后重试。");
        return result;
    });
}

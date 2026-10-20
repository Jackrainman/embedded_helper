using System.IO;
using EmbeddedHelper.Core;

namespace EmbeddedHelper.App;

public sealed record ElfParseFeedback(string Message, string Details = "")
{
    public static ElfParseFeedback Success(Variable[] index)
    {
        var count = index.Count(v => v.Supported);
        return new(count == 0
            ? $"解析完成：0 个可观察变量（共 {index.Length} 项）。请检查变量类型、固定地址及调试信息。"
            : $"解析成功：{count} 个可观察变量（共 {index.Length} 项）");
    }
    public static ElfParseFeedback Failure(Exception error)
    {
        var reason = error.Message;
        var action = error switch
        {
            FileNotFoundException or DirectoryNotFoundException => "请重新选择存在的 ELF 文件。",
            UnauthorizedAccessException => "请确认文件读取权限后重试。",
            _ when reason.Contains("256 MiB", StringComparison.Ordinal) => "请选择不超过 256 MiB 的 ELF。",
            _ when reason.Contains("readelf", StringComparison.OrdinalIgnoreCase) => "请到设置与诊断选择有效的 ARM readelf，或使用 DWARF4 构建后重试。",
            _ when reason.Contains("调试信息", StringComparison.Ordinal) || reason.Contains("固定地址变量", StringComparison.Ordinal) => "请启用 -g -gdwarf-4 并保留固定地址变量后重新构建。",
            _ when reason.Contains("发生变化", StringComparison.Ordinal) => "请等待构建完成后重试。",
            _ => "请确认 ELF 完整且为 ELF32 小端 ARM 文件，然后重试。"
        };
        var summary = error is FileNotFoundException or DirectoryNotFoundException ? "文件未选择或不存在"
            : error is UnauthorizedAccessException ? "无法读取文件"
            : reason.Contains("readelf", StringComparison.OrdinalIgnoreCase) ? "解析工具缺失或执行失败"
            : reason.Contains("调试信息", StringComparison.Ordinal) ? "缺少可用调试信息"
            : reason.Contains("256 MiB", StringComparison.Ordinal) ? "文件超过解析上限"
            : reason.Contains("发生变化", StringComparison.Ordinal) ? "解析期间文件变化"
            : "文件损坏或格式不受支持";
        return new($"解析失败：{summary}。{action}", error.ToString());
    }
}

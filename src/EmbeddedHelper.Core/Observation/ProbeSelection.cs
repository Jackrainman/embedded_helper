namespace EmbeddedHelper.Core;

public sealed record UsbProbe(int SerialNumber, string Model)
{
    public string Label => $"{Model} · 序列号 {SerialNumber}";
}
public sealed record ProbeSelection(UsbProbe? Selected, string Message)
{
    public static ProbeSelection Resolve(IReadOnlyList<UsbProbe> probes, string remembered)
    {
        if (!string.IsNullOrWhiteSpace(remembered))
        {
            var match = int.TryParse(remembered, out var serial) ? probes.SingleOrDefault(p => p.SerialNumber == serial) : null;
            return match == null ? new(null, $"工程记住的探针 {remembered} 未在线；没有自动改用其他探针。") : new(match, "已找到工程记住的实体探针。");
        }
        return probes.Count switch
        {
            0 => new(null, "未找到本机 USB J-Link，请接好探针后刷新。"),
            1 => new(probes[0], "仅发现一支 USB J-Link，可使用并记住此探针。"),
            _ => new(null, "发现多支 USB J-Link，请明确选择要连接的探针。")
        };
    }
}

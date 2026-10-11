namespace EmbeddedHelper.Core;

public sealed record ReadPlan(long Generation, int SampleRate, Variable[] Variables)
{
    public void Validate()
    {
        if (Generation < 1 || SampleRate is < 1 or > 1000 || Variables == null || Variables.Length > 64) throw new InvalidDataException("采样率应为 1～1000 Hz，最多 64 项，计划代次须为正数。");
        var names = new HashSet<string>(StringComparer.Ordinal);
        foreach (var variable in Variables)
        {
            var width = variable.Type switch { "int8" or "uint8" => 1, "int16" or "uint16" => 2, "int32" or "uint32" or "float32" => 4, _ => 0 };
            if (!variable.Supported || width == 0 || variable.Size != width || variable.Address > uint.MaxValue - (ulong)(width - 1) || string.IsNullOrWhiteSpace(variable.Identity) || !names.Add(variable.Identity))
                throw new InvalidDataException("读取计划包含不支持类型、越界地址或重复变量。");
        }
    }
}

// The SDK thread alone takes pending plans, at the boundary between complete batches.
public sealed class ReadPlanMailbox(ReadPlan initial)
{
    private readonly object sync = new();
    private ReadPlan? pending;
    private long latest = initial.Generation;
    public void Submit(ReadPlan plan)
    {
        plan.Validate();
        lock (sync)
        {
            if (plan.Generation <= latest) throw new InvalidDataException("读取计划代次未递增。");
            pending = plan with { Variables = (Variable[])plan.Variables.Clone() }; latest = plan.Generation;
        }
    }
    public ReadPlan? Take() { lock (sync) { var result = pending; pending = null; return result; } }
}

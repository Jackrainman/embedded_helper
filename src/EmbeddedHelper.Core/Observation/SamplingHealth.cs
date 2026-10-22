namespace EmbeddedHelper.Core;

/// <summary>Whole-round failures must persist for three seconds and at least three rounds.</summary>
public sealed class SamplingHealth
{
    private TimeSpan? failedSince;
    private int failedRounds;
    public void Reset() { failedSince = null; failedRounds = 0; }
    public void Observe(int attempted, int successful, TimeSpan elapsed)
    {
        if (attempted == 0 || successful > 0) { Reset(); return; }
        failedSince ??= elapsed;
        failedRounds++;
        if (failedRounds >= 3 && elapsed - failedSince.Value >= TimeSpan.FromSeconds(3))
            throw new IOException("所有变量持续读取失败超过3秒，采集已停止；请检查连接并重试。");
    }
}

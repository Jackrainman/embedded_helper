using System.Text.Json;

namespace EmbeddedHelper.Core;

public static class ObservationControl
{
    public static int Read(TextReader input, ReadPlanMailbox mailbox, TextWriter errors)
    {
        try
        {
            while (input.ReadLine() is { } line)
            {
                if (line.Length > 1024 * 1024) throw new InvalidDataException("控制请求过长。");
                mailbox.Submit(JsonSerializer.Deserialize<ReadPlan>(line) ?? throw new InvalidDataException("缺少计划。"));
            }
            return 0;
        }
        catch (Exception ex) { errors.WriteLine("控制通道失败：" + ex.Message); return 1; }
    }
}

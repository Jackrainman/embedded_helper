using System.Diagnostics;
using System.Text;
using System.Text.Json;

namespace EmbeddedHelper.Core;

public static class ProbeDiscoveryProcess
{
    public static async Task<UsbProbe[]> ReadAsync(string worker, string dll)
    {
        var start = new ProcessStartInfo(worker)
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            StandardOutputEncoding = Encoding.UTF8,
            StandardErrorEncoding = Encoding.UTF8
        };
        start.ArgumentList.Add("--list-probes");
        using var child = Process.Start(start) ?? throw new IOException("无法启动探针枚举。");
        var output = child.StandardOutput.ReadToEndAsync();
        var errors = child.StandardError.ReadToEndAsync();
        try
        {
            await child.StandardInput.WriteLineAsync(JsonSerializer.Serialize(dll));
            child.StandardInput.Close();
            await child.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(8));
            if (child.ExitCode != 0) throw new IOException((await errors).Trim());
            return JsonSerializer.Deserialize<UsbProbe[]>(await output) ?? throw new InvalidDataException("探针枚举未返回结果。");
        }
        catch (TimeoutException)
        {
            throw new TimeoutException("探针枚举超时，请检查本机 J-Link 软件后重试。");
        }
        finally
        {
            if (!child.HasExited) { child.Kill(entireProcessTree: true); await child.WaitForExitAsync(); }
        }
    }
}

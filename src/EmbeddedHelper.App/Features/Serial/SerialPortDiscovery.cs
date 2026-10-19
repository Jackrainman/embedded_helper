using System.IO.Ports;
using System.Text.RegularExpressions;
using Microsoft.Win32;

namespace EmbeddedHelper.App;

internal static class SerialPortDiscovery
{
    public static string PortName(string text)
    {
        var match = Regex.Match(text.Trim(), @"^(COM\d+)(?:\s+[—-].*)?$", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
        return match.Success ? match.Groups[1].Value.ToUpperInvariant() : text.Trim();
    }
    public static string[] List()
    {
        var ports = SerialPort.GetPortNames().Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
        var names = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        try
        {
            using var root = Registry.LocalMachine.OpenSubKey(@"SYSTEM\CurrentControlSet\Enum");
            if (root != null)
                foreach (var bus in root.GetSubKeyNames())
                {
                    using var busKey = root.OpenSubKey(bus);
                    if (busKey == null) continue;
                    foreach (var device in busKey.GetSubKeyNames())
                    {
                        using var deviceKey = busKey.OpenSubKey(device);
                        if (deviceKey == null) continue;
                        foreach (var instance in deviceKey.GetSubKeyNames())
                        {
                            try
                            {
                                using var key = deviceKey.OpenSubKey(instance);
                                using var parameters = key?.OpenSubKey("Device Parameters");
                                if (parameters?.GetValue("PortName") is string port && ports.Contains(port, StringComparer.OrdinalIgnoreCase) && key?.GetValue("FriendlyName") is string name)
                                    names[port] = name;
                            }
                            catch (Exception ex) when (ex is System.Security.SecurityException or UnauthorizedAccessException or System.IO.IOException) { }
                        }
                    }
                }
        }
        catch (Exception ex) when (ex is System.Security.SecurityException or UnauthorizedAccessException or System.IO.IOException) { }
        return ports.OrderBy(p => int.TryParse(p.AsSpan(3), out var number) ? number : int.MaxValue)
            .Select(p => names.TryGetValue(p, out var name) ? p + " — " + name : p + " — 设备名称未知").ToArray();
    }
}

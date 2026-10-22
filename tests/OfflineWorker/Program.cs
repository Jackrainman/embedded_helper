// Controlled protocol peer. Never loads a native library, enumerates hardware or opens a network connection.
using System.Text.Json;
using EmbeddedHelper.Core;

if (args is ["--inspect", var library])
{
    var result = ToolDiscovery.Inspect(LocalToolKind.JLink, library, "offline static sample");
    Console.WriteLine(JsonSerializer.Serialize(result));
    Console.WriteLine(Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(File.ReadAllBytes(library))));
    return result.Valid ? 0 : 1;
}
if (args is ["--compare-elf", var elf, var readelf])
{
    var native = ElfIndex.Read(elf, "must-not-fallback");
    foreach (var nativeIdentity in new[] { false, true })
        if (!native.SequenceEqual(ReadElfIndex.Read(readelf, elf, nativeIdentity))) throw new InvalidDataException("Reader mismatch");
    Console.WriteLine(JsonSerializer.Serialize(native));
    return 0;
}
var input = Console.ReadLine()!;
using var request = JsonDocument.Parse(input);
var settingsPath = args[0] == "--list-probes" ? request.RootElement.GetString()! : request.RootElement.GetProperty("Dll").GetString()!;
using var settings = JsonDocument.Parse(File.ReadAllText(settingsPath));
var mode = settings.RootElement.GetProperty("mode").GetString();
File.WriteAllText(settings.RootElement.GetProperty("pidFile").GetString()!, Environment.ProcessId.ToString());
if (args[0] == "--list-probes")
{
    if (mode == "probe-error") { Console.Error.WriteLine("controlled enumeration failure"); return 7; }
    if (mode == "probe-empty") { Console.WriteLine("[]"); return 0; }
    await Task.Delay(Timeout.Infinite); return 0;
}
var session = request.RootElement.GetProperty("SessionId").GetString()!;
if (mode == "wrong-session") { Console.WriteLine(JsonSerializer.Serialize(new ObservationEvent("plan", "", SessionId: "other"))); }
else if (mode == "overlong") Console.WriteLine(new string('x', 1024 * 1024 + 1));
else if (mode != "no-ack") Console.WriteLine(JsonSerializer.Serialize(new ObservationEvent("plan", "", SessionId: session)));
// Ignore EOF deliberately, so Dispose must exercise the bounded kill path.
await Task.Delay(Timeout.Infinite);
return 0;

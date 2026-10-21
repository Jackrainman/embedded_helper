using EmbeddedHelper.Core;

namespace EmbeddedHelper.App;

public sealed record AppSettings(string Dll = "", string Probe = "", string[]? Recent = null, double Width = 1280, double Height = 860, SerialSettings? Serial = null, string ReadElf = "", string SerialEncoding = "utf-8", string[]? SendHistory = null, string LastProject = "", string LastTarget = "");

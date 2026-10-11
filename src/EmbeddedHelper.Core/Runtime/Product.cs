using System.Reflection;
using System.Text.Json;

namespace EmbeddedHelper.Core;

public static class Product
{
    public static string HomeDirectory => RuntimePaths.Current.HomeDirectory;
    public static string Version => typeof(Product).Assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()!.InformationalVersion.Split('+')[0];
    public static readonly JsonSerializerOptions Json = new() { WriteIndented = true, PropertyNameCaseInsensitive = true };
}

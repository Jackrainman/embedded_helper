using EmbeddedHelper.Core;
using System.IO;
using System.Text.Json;

namespace EmbeddedHelper.App;

internal sealed class SettingsStore(PortableData data)
{
    public AppSettings Load() => data.Load("settings/window.json", () => new AppSettings());
    public AppSettings SaveAndRead(AppSettings settings)
    {
        Save(settings);
        var restored = Load();
        if (JsonSerializer.Serialize(restored, Product.Json) != JsonSerializer.Serialize(settings, Product.Json))
            throw new InvalidDataException("设置写入后回读不一致，请重试。");
        return restored;
    }
    public void Save(AppSettings settings) => data.Save("settings/window.json", settings);
}

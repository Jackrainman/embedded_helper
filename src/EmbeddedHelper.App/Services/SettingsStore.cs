using EmbeddedHelper.Core;

namespace EmbeddedHelper.App;

internal sealed class SettingsStore(PortableData data)
{
    public AppSettings Load() => data.Load("settings/window.json", () => new AppSettings());
    public void Save(AppSettings settings) => data.Save("settings/window.json", settings);
}

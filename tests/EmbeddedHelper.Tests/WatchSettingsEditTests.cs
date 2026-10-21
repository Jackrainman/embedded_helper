using EmbeddedHelper.Core;
using Xunit;

namespace EmbeddedHelper.Tests;

[Collection("Offline client")]
public sealed class WatchSettingsEditTests : TestWorkspace
{
    [Fact]
    public void EmptySelectionRoundTripsAndTargetChangesDoNotCopyLegacyConnection()
    {
        WatchSettingsStore.Save(root, new("Debug", ["a"], 200) { Transport = "remote", ProbeSerial = "123" });
        var edit = new WatchSettingsEdit(root);
        var empty = edit.Save("Debug", [], 75);
        Assert.Empty(new WatchSettingsEdit(root).Value!.Identities); Assert.Equal("123", empty.ProbeSerial);
        var target = edit.Save("Release", ["b"], 1000);
        Assert.Equal("", target.ProbeSerial); Assert.Equal("local", target.Transport);
        Assert.Null(new WatchSettingsEdit(Path.Combine(root, "other-project")).Value);
    }
    [Fact]
    public void InvalidInputAndConcurrentEditorNeverOverwriteTheSavedSettings()
    {
        var first = new WatchSettingsEdit(root); var stale = new WatchSettingsEdit(root);
        first.Save("Debug", ["a"], 200);
        Assert.Throws<InvalidDataException>(() => first.Save("Debug", [], 0));
        Assert.Throws<InvalidDataException>(() => first.Save("Debug", ["a", "a"], 100));
        Assert.Throws<InvalidDataException>(() => stale.Save("Debug", ["b"], 300));
        Assert.Equal(200, new WatchSettingsEdit(root).Value!.Rate);
        new WatchSettingsEdit(root).Save("Debug", [], 300);
        Assert.Equal(300, new WatchSettingsEdit(root).Value!.Rate);
    }
    [Fact]
    public void FailedWriteCanRetryWithoutChangingTheLoadedBaseline()
    {
        var edit = new WatchSettingsEdit(root);
        var directory = Path.Combine(root, ".embedded_helper");
        File.WriteAllText(directory, "blocked parent");
        Assert.Throws<IOException>(() => edit.Save("Debug", ["a"], 100));
        Assert.Null(edit.Value);
        File.Delete(directory);
        edit.Save("Debug", ["a"], 100);
        Assert.Equal(100, new WatchSettingsEdit(root).Value!.Rate);
    }
}

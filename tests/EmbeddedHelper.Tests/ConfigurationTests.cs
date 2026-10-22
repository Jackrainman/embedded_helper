using System.Text.Json;
using System.Text.Json.Nodes;
using EmbeddedHelper.Core;
using Xunit;

namespace EmbeddedHelper.Tests;

[Collection("Offline client")]
public sealed class ConfigurationTests : TestWorkspace
{
    [Fact]
    public void ExplicitConflictClosurePreservesEvidenceAndRejectsRaces()
    {
        var file = Path.Combine(root, "config.json"); File.WriteAllText(file, "external");
        var plan = new ConfigPlan(root, [new(file, "before", "after")]);
        var journals = Path.Combine(root, "journal"); Directory.CreateDirectory(journals);
        var journal = Path.Combine(journals, "pending.json");
        PortableData.SaveFile(journal, new { status = "conflict", plan });
        var preview = ConfigTransaction.PreviewKeepCurrent(root, journal);
        Assert.Throws<IOException>(() => ConfigTransaction.Apply(plan, journals));
        File.WriteAllText(file, "changed-again");
        Assert.Throws<IOException>(() => ConfigTransaction.KeepCurrent(preview, journals));
        Assert.Single(ConfigTransaction.Pending(journals));
        Assert.Throws<InvalidDataException>(() => ConfigTransaction.PreviewKeepCurrent(Path.Combine(root, "other"), journal));
        preview = ConfigTransaction.PreviewKeepCurrent(root, journal);
        ConfigTransaction.KeepCurrent(preview, journals);
        Assert.Empty(ConfigTransaction.Pending(journals));
        Assert.Equal("changed-again", File.ReadAllText(file));
        Assert.Contains("before", File.ReadAllText(journal));
        Assert.Throws<IOException>(() => ConfigTransaction.KeepCurrent(preview, journals));
        File.WriteAllText(journal, "broken");
        Assert.Contains(journal, Assert.Throws<InvalidDataException>(() => ConfigTransaction.Pending(journals)).Message);
    }
    [Fact]
    public void JsoncEditPreservesCommentsAndUnknownProperties()
    {
        const string input = "{\n// 用户注释\n\"unknown\": [1,2],\n\"host\": \"before\", //尾部\n}";
        var output = JsonEdit.Set(input, "host", JsonValue.Create("中文.example"));
        output = JsonEdit.Set(output, "port", JsonValue.Create(19011));
        Assert.Contains("// 用户注释", output); Assert.Contains("//尾部", output); Assert.Contains("\"unknown\": [1,2]", output);
        using var doc = JsonDocument.Parse(output, ProjectReader.JsonOptions);
        Assert.Equal("中文.example", doc.RootElement.GetProperty("host").GetString());
        Assert.Equal(19011, doc.RootElement.GetProperty("port").GetInt32());
    }
    [Fact]
    public void ConfigRejectsExternalChangesAndRollsBackInjectedFailure()
    {
        var first = Path.Combine(root, "a.json"); var second = Path.Combine(root, "b.json");
        File.WriteAllText(first, "old");
        var plan = new ConfigPlan(root, [new FileChange(first, "old", "new"), new FileChange(second, null, "new2")]);
        Assert.Throws<IOException>(() => ConfigTransaction.Apply(plan, Path.Combine(root, "journal"), _ => throw new IOException("disk full")));
        Assert.Equal("old", File.ReadAllText(first)); Assert.False(File.Exists(second));
        File.WriteAllText(first, "external");
        Assert.Throws<IOException>(() => ConfigTransaction.Apply(plan, Path.Combine(root, "journal")));
        Assert.Equal("external", File.ReadAllText(first));
        Assert.Throws<IOException>(() => ConfigTransaction.CheckPath(root, Path.Combine(root, "..", "outside.json")));
    }
    [Fact]
    public void RemoteConfigRetainsUnknownFieldsAndIsIdempotent()
    {
        var project = ProjectReader.Read(CopyProject());
        var plan = RemoteConfiguration.Plan(project, project.Targets[0], "test.example.invalid", 19012);
        ConfigTransaction.Apply(plan, Path.Combine(root, "journal"));
        var second = RemoteConfiguration.Plan(project, project.Targets[0], "test.example.invalid", 19012);
        Assert.Equal(second.Changes[0].Before, second.Changes[0].After);
        using var old = JsonDocument.Parse(plan.Changes[0].Before!);
        using var saved = JsonDocument.Parse(File.ReadAllText(project.RemotePath));
        foreach (var property in old.RootElement.EnumerateObject().Where(p => p.Name is not ("ServerName" or "Port" or "Device" or "Interface" or "SpeedKHz")))
            Assert.Equal(property.Value.GetRawText(), saved.RootElement.GetProperty(property.Name).GetRawText());
    }
    [Fact]
    public void IncompleteTransactionRecoveryPreservesExternalChanges()
    {
        var path = Path.Combine(root, "config.json"); var conflict = Path.Combine(root, "other.json");
        File.WriteAllText(path, "after"); File.WriteAllText(conflict, "external");
        var plan = new ConfigPlan(root, [new FileChange(path, "before", "after"), new FileChange(conflict, "before2", "after2")]);
        var journal = Path.Combine(root, "journal"); Directory.CreateDirectory(journal);
        PortableData.SaveFile(Path.Combine(journal, "interrupted.json"), new { status = "prepared", plan });
        Assert.Single(ConfigTransaction.Pending(journal));
        Assert.Equal(conflict, Assert.Single(ConfigTransaction.RecoverIncomplete(root, journal)));
        Assert.Equal("before", File.ReadAllText(path)); Assert.Equal("external", File.ReadAllText(conflict));
        File.WriteAllText(conflict, "before2");
        Assert.Empty(ConfigTransaction.RecoverIncomplete(root, journal)); Assert.Empty(ConfigTransaction.Pending(journal));
    }
}

using System.Text.Json;
using System.Text.Json.Nodes;
using EmbeddedHelper.Core;
using Xunit;

namespace EmbeddedHelper.Tests;

[Collection("Offline client")]
public sealed class ConfigurationTests : TestWorkspace
{
    [Theory]
    [InlineData("")]
    [InlineData("abc")]
    [InlineData("0")]
    [InlineData("65536")]
    public void InvalidPortsIdentifyTheField(string text)
    {
        Assert.Contains("探针端口", Assert.Throws<ArgumentException>(() => RemoteConfiguration.ParsePort(text, "探针端口")).Message);
        Assert.Equal(65535, RemoteConfiguration.ParsePort("65535", "探针端口"));
        Assert.Throws<ArgumentException>(() => RemoteConfiguration.ParsePort("65534", "GDB端口", 65533));
    }
    [Theory]
    [InlineData("{}", "folders")]
    [InlineData("{\"folders\":1}", "folders")]
    [InlineData("{\"folders\":[{}]}", "path")]
    [InlineData("{\"folders\":[{\"path\":4}]}", "path")]
    public void WorkspaceFieldsNameTheFileAndField(string text, string field)
    {
        var file = Path.Combine(root, "bad.code-workspace"); File.WriteAllText(file, text);
        var error = Assert.Throws<InvalidDataException>(() => ProjectReader.Read(file));
        Assert.Contains(file, error.Message); Assert.Contains(field, error.Message);
        Assert.Equal(text, File.ReadAllText(file));
    }
    [Theory]
    [InlineData("{}")]
    [InlineData("{\"configurations\":3}")]
    public void LaunchConfigurationFieldIsExplicit(string text)
    {
        var project = CopyProject(); var file = Path.Combine(project, ".vscode/launch.json"); File.WriteAllText(file, text);
        var error = Assert.Throws<InvalidDataException>(() => ProjectReader.Read(project));
        Assert.Contains(Path.GetFullPath(file), error.Message); Assert.Contains("configurations", error.Message);
        Assert.Equal(text, File.ReadAllText(file));
    }
    [Theory]
    [InlineData("{")]
    [InlineData("{}")]
    [InlineData("{\"schemaVersion\":99,\"entries\":[]}")]
    [InlineData("{\"schemaVersion\":1,\"target\":\"Debug\",\"entries\":null}")]
    [InlineData("{\"schemaVersion\":1,\"target\":\"Debug\",\"entries\":[null]}")]
    public void InvalidOwnershipIsDistinctFromMissingAndNamesTheFile(string text)
    {
        Assert.Null(VsCodeIntegration.ReadState(root));
        var file = Path.Combine(root, ".embedded_helper/integration.json"); Directory.CreateDirectory(Path.GetDirectoryName(file)!);
        File.WriteAllText(file, text);
        Assert.Contains(file, Assert.Throws<InvalidDataException>(() => VsCodeIntegration.ReadState(root)).Message);
        Assert.Equal(text, File.ReadAllText(file));
    }
    [Fact]
    public async Task AncestorJunctionWorksButInternalLinksAndWriteTimeChangesAreRejected()
    {
        if (!OperatingSystem.IsWindows()) return;
        var physical = Path.Combine(root, "physical"); Directory.CreateDirectory(Path.Combine(physical, "project"));
        var alias = Path.Combine(root, "alias");
        static void Junction(string link, string target)
        {
            var start = new System.Diagnostics.ProcessStartInfo("powershell.exe") { UseShellExecute = false, CreateNoWindow = true };
            start.ArgumentList.Add("-NoProfile"); start.ArgumentList.Add("-Command");
            start.ArgumentList.Add("New-Item -ItemType Junction -Path '" + link.Replace("'", "''") + "' -Target '" + target.Replace("'", "''") + "' | Out-Null");
            using var process = System.Diagnostics.Process.Start(start)!; process.WaitForExit(); Assert.Equal(0, process.ExitCode);
        }
        Junction(alias, physical);
        var project = Path.Combine(alias, "project");
        var file = Path.Combine(project, "a.json");
        ConfigTransaction.CheckPath(project + Path.DirectorySeparatorChar, file);
        var journals = Path.Combine(root, "journals");
        ConfigTransaction.Apply(new(project, [new(file, null, "saved")]), journals);
        Assert.Equal("saved", File.ReadAllText(Path.Combine(physical, "project/a.json")));
        var package = ScriptPackage(); var data = Path.Combine(root, "data");
        await new ToolCache(data).InstallAsync(package.Zip, package.Release);
        Assert.NotEmpty(ProjectToolCache.Plan(project, data, package.Release));
        var outside = Path.Combine(root, "outside"); Directory.CreateDirectory(outside);
        var link = Path.Combine(project, "escape"); Junction(link, outside);
        Assert.Throws<IOException>(() => ConfigTransaction.CheckPath(project, Path.Combine(link, "evil.json")));
        Assert.Throws<IOException>(() => ConfigTransaction.CheckPath(project, project + "-other/config.json"));
        Directory.Delete(link);
        var second = Path.Combine(project, "late/b.json");
        Assert.Throws<IOException>(() => ConfigTransaction.Apply(new(project, [new(file, "saved", "next"), new(second, null, "bad")]), journals, n => { if (n == 1) Junction(Path.GetDirectoryName(second)!, outside); }));
        Assert.False(File.Exists(Path.Combine(outside, "b.json")));
        Directory.Delete(Path.GetDirectoryName(second)!); Directory.Delete(alias);
    }
    [Fact]
    public void AncestorRetargetAfterPreviewCannotWriteAnotherProject()
    {
        var first = Path.Combine(root, "first"); var other = Path.Combine(root, "other");
        Directory.CreateDirectory(Path.Combine(first, "project")); Directory.CreateDirectory(Path.Combine(other, "project"));
        var alias = Path.Combine(root, "alias");
        void Link(string target)
        {
            var start = new System.Diagnostics.ProcessStartInfo("powershell.exe") { UseShellExecute = false, CreateNoWindow = true };
            start.ArgumentList.Add("-NoProfile"); start.ArgumentList.Add("-Command");
            start.ArgumentList.Add("New-Item -ItemType Junction -Path '" + alias.Replace("'", "''") + "' -Target '" + target.Replace("'", "''") + "' | Out-Null");
            using var child = System.Diagnostics.Process.Start(start)!; child.WaitForExit(); Assert.Equal(0, child.ExitCode);
        }
        Link(first);
        try
        {
            var project = Path.Combine(alias, "project"); var file = Path.Combine(project, "new.json");
            var plan = new ConfigPlan(project, [new(file, null, "saved")]);
            var serialized = JsonSerializer.Serialize(plan, Product.Json);
            Directory.Delete(alias); Link(other);
            Assert.Throws<IOException>(() => ConfigTransaction.Apply(plan, Path.Combine(root, "journals")));
            var restored = JsonSerializer.Deserialize<ConfigPlan>(serialized, Product.Json)!;
            Assert.Throws<IOException>(() => ConfigTransaction.Apply(restored, Path.Combine(root, "journals")));
            Assert.False(File.Exists(Path.Combine(other, "project/new.json")));
        }
        finally { Directory.Delete(alias); }
    }
    [Fact]
    public void RollbackCannotFollowAReplacedDirectoryOutsideTheProject()
    {
        var project = Path.Combine(root, "project"); var inner = Path.Combine(project, "config");
        var outside = Path.Combine(root, "outside"); Directory.CreateDirectory(inner); Directory.CreateDirectory(outside);
        var file = Path.Combine(inner, "a.json"); var external = Path.Combine(outside, "a.json");
        File.WriteAllText(file, "before"); File.WriteAllText(external, "after");
        var journals = Path.Combine(root, "journals");
        try
        {
            Assert.Throws<IOException>(() => ConfigTransaction.Apply(new(project, [new(file, "before", "after")]), journals, _ =>
            {
                Directory.Move(inner, inner + "-saved");
                var start = new System.Diagnostics.ProcessStartInfo("powershell.exe") { UseShellExecute = false, CreateNoWindow = true };
                start.ArgumentList.Add("-NoProfile"); start.ArgumentList.Add("-Command");
                start.ArgumentList.Add("New-Item -ItemType Junction -Path '" + inner.Replace("'", "''") + "' -Target '" + outside.Replace("'", "''") + "' | Out-Null");
                using var child = System.Diagnostics.Process.Start(start)!; child.WaitForExit(); Assert.Equal(0, child.ExitCode);
                throw new IOException("injected failure after directory replacement");
            }));
            Assert.Equal("after", File.ReadAllText(external));
            Assert.Single(ConfigTransaction.Pending(journals));
        }
        finally { if ((File.GetAttributes(inner) & FileAttributes.ReparsePoint) != 0) Directory.Delete(inner); }
    }
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
    [Theory]
    [InlineData("{}")]
    [InlineData("{\"status\":\"commited\"}")]
    [InlineData("{\"status\":\"conflict\"}")]
    [InlineData("{\"status\":\"prepared\",\"plan\":{\"root\":\"x\",\"changes\":null}}")]
    [InlineData("{\"status\":\"conflict\",\"plan\":{\"root\":\"x\",\"changes\":[null]}}")]
    public void MalformedTransactionCannotBeSkippedOrLoseItsFilename(string content)
    {
        var journals = Path.Combine(root, "journals"); Directory.CreateDirectory(journals);
        var file = Path.Combine(journals, "broken.json"); File.WriteAllText(file, content);
        Assert.Contains(file, Assert.Throws<InvalidDataException>(() => ConfigTransaction.Pending(journals)).Message);
        Assert.Contains(file, Assert.Throws<InvalidDataException>(() => ConfigTransaction.RecoverIncomplete(root, journals)).Message);
        Assert.Contains(file, Assert.Throws<InvalidDataException>(() => ConfigTransaction.PreviewKeepCurrent(root, file)).Message);
        Assert.Equal(content, File.ReadAllText(file));
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

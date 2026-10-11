using System.Text.Json;
using System.Text.Json.Nodes;
using EmbeddedHelper.Core;
using Xunit;

namespace EmbeddedHelper.Tests;

public sealed class CoreTests : IDisposable
{
    private readonly string root;
    private readonly string fixtures;
    public CoreTests()
    {
        var repository = new DirectoryInfo(AppContext.BaseDirectory);
        while (repository != null && !File.Exists(Path.Combine(repository.FullName, "EmbeddedHelper.slnx"))) repository = repository.Parent;
        if (repository == null) throw new DirectoryNotFoundException("repository");
        fixtures = Path.Combine(repository.FullName, "tests", "fixtures");
        root = Path.Combine(repository.FullName, "tmp", "csharp-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
    }
    private string CopyProject()
    {
        var source = Path.Combine(fixtures, "G4 中文 空格");
        var target = Path.Combine(root, "工程 含空格");
        foreach (var file in Directory.GetFiles(source, "*", SearchOption.AllDirectories))
        {
            var destination = Path.Combine(target, Path.GetRelativePath(source, file));
            Directory.CreateDirectory(Path.GetDirectoryName(destination)!); File.Copy(file, destination);
        }
        return target;
    }
    [Fact]
    public void ProjectRecognitionDoesNotWriteOrRunHooks()
    {
        var path = CopyProject();
        var before = Directory.GetFiles(path, "*", SearchOption.AllDirectories).ToDictionary(p => p, File.ReadAllBytes);
        var project = ProjectReader.Read(path);
        Assert.Equal("G4", project.Family); Assert.Equal("STM32G474VETx", project.Mcu);
        Assert.Single(project.Targets); Assert.Equal("STM32G474VE", project.Targets[0].Device);
        Assert.Equal("SWD", project.Targets[0].Interface); Assert.Equal(8000, project.Targets[0].SpeedKHz);
        foreach (var (file, bytes) in before) Assert.Equal(bytes, File.ReadAllBytes(file));
        var workspace = Path.Combine(path, "project.code-workspace");
        File.WriteAllText(workspace, "{\"folders\":[{\"path\":\".\"}]}");
        Assert.Equal(project.Root, ProjectReader.Read(workspace).Root);
    }
    [Fact]
    public void DwarfFixtureTypesAndAddressesComeFromElf()
    {
        var variables = ElfIndex.Read(Path.Combine(fixtures, "scalars.elf"));
        Assert.Equal(7, variables.Count(v => v.Supported));
        foreach (var type in new[] { "int8", "uint8", "int16", "uint16", "int32", "uint32", "float32" }) Assert.Single(variables, v => v.Type == type);
        Assert.All(variables, v => Assert.InRange(v.Address, 0x20000000UL, 0x20010000UL));
        Assert.False(variables.Single(v => v.Name == "unsupported64").Supported);
        Assert.Equal(-32, ElfIndex.Decode(variables.Single(v => v.Name == "signed32"), [0xe0, 0xff, 0xff, 0xff]));
        Assert.Equal(1.25, ElfIndex.Decode(variables.Single(v => v.Name == "float32"), [0, 0, 0xa0, 0x3f]));
        Assert.Throws<InvalidDataException>(() => ElfIndex.Decode(variables.First(v => v.Size == 4), [1]));
        Assert.ThrowsAny<Exception>(() => ElfIndex.Read(Path.Combine(fixtures, "invalid.elf")));
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
    public async Task BidirectionalRecordingKeepsRawBytesAndSegmentSequence()
    {
        var output = Path.Combine(root, "record");
        var bytes = Enumerable.Range(0, 256).Select(i => (byte)i).ToArray();
        await using (var recorder = new OrderedRecorder(output, 500))
        {
            await Task.WhenAll(Task.Run(() => { for (int i = 0; i < 30; i++) recorder.Record("TX", bytes); }), Task.Run(() => { for (int i = 0; i < 30; i++) recorder.Record("RX", bytes); }));
            Assert.Equal(0, recorder.Discarded);
        }
        var lines = Directory.GetFiles(output, "*.jsonl").Order().SelectMany(File.ReadAllLines).Select(line => JsonDocument.Parse(line)).ToArray();
        Assert.Equal(60, lines.Length);
        Assert.Equal(Enumerable.Range(1, 60).Select(i => (long)i), lines.Select(d => d.RootElement.GetProperty("sequence").GetInt64()));
        Assert.Equal(30, lines.Count(d => d.RootElement.GetProperty("direction").GetString() == "TX"));
        foreach (var line in lines) { Assert.Equal(bytes, Convert.FromBase64String(line.RootElement.GetProperty("data").GetString()!)); Assert.False(line.RootElement.TryGetProperty("timestamp", out _)); line.Dispose(); }
        Assert.True(Directory.GetFiles(output, "*.index.json").Length > 1);
    }
    [Fact]
    public void SerialHexValidationAndPortableSettingsRoundtrip()
    {
        Assert.Equal(new byte[] { 0, 255, 10 }, SerialSession.ParseSend("00 FF 0A", true, "\n"));
        Assert.Throws<FormatException>(() => SerialSession.ParseSend("zz", true, ""));
        var data = new PortableData(root); data.Save("settings/check.json", new { value = "中文" });
        Assert.Equal("中文", data.Load("settings/check.json", () => new Dictionary<string, string>())["value"]);
    }
    [Fact]
    public void ReadElfDwarf5AdapterResolvesTypeChainsWithoutGuessing()
    {
        const string dump = """
         <0><c>: Abbrev Number: 1 (DW_TAG_compile_unit)
            <d> DW_AT_name : (strp) (offset: 0x1): firmware.c
         <1><20>: Abbrev Number: 2 (DW_TAG_base_type)
            <21> DW_AT_byte_size : (data1) 4
            <22> DW_AT_encoding : (data1) 5 (signed)
         <1><30>: Abbrev Number: 3 (DW_TAG_volatile_type)
            <31> DW_AT_type : (ref4) <0x20>, int
         <1><40>: Abbrev Number: 4 (DW_TAG_variable)
            <41> DW_AT_name : (string) counter
            <42> DW_AT_type : (ref4) <0x30>
            <43> DW_AT_external : (flag_present) 1
            <44> DW_AT_location : (exprloc) 5 byte block: 3 0 0 0 20 (DW_OP_addr: 20000000)
         <1><50>: Abbrev Number: 4 (DW_TAG_variable)
            <51> DW_AT_name : (string) local
            <52> DW_AT_type : (ref4) <0x30>
            <54> DW_AT_location : (exprloc) 2 byte block: 91 4 (DW_OP_fbreg: 4)
        """;
        var variable = Assert.Single(ReadElfIndex.Parse(dump));
        Assert.Equal("global:counter", variable.Identity); Assert.Equal("int32", variable.Type);
        Assert.Equal(0x20000000UL, variable.Address);
    }
    [Fact]
    public async Task MissingSdkAndInvalidSerialSettingsFailBeforeDeviceAccess()
    {
        await using var session = new ObservationSession();
        var request = new ObservationRequest(Path.Combine(root, "missing.dll"), 1, "", 19011, "STM32G474VE", "SWD", 8000, 100, false, []);
        await Assert.ThrowsAsync<FileNotFoundException>(() => session.StartAsync("not-started.exe", request));
        Assert.Equal("未连接", session.State);
        await using var serial = new SerialSession();
        await Assert.ThrowsAsync<ArgumentException>(() => serial.OpenAsync(new SerialSettings("not-a-port", -1), null));
        Assert.False(serial.Connected);
    }
    public void Dispose() => Directory.Delete(root, true);
}

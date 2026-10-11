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
    [Fact]
    public async Task RecordingReportsInteriorAndTrailingByteBudgetGaps()
    {
        var output = Path.Combine(root, "bounded-record");
        var recorder = new OrderedRecorder(output, 1024, 4);
        recorder.Record("RX", [1, 2, 3, 4, 5]);
        recorder.Record("TX", [255, 0]);
        recorder.Record("RX", [6, 7, 8, 9, 10]);
        await recorder.DisposeAsync();
        Assert.Equal(2, recorder.Discarded); Assert.Equal(10, recorder.DiscardedBytes);
        Assert.InRange(recorder.PeakBytes, 0, 4); Assert.Equal(0, recorder.QueuedBytes);
        using var line = JsonDocument.Parse(Assert.Single(File.ReadAllLines(Assert.Single(Directory.GetFiles(output, "*.jsonl")))));
        Assert.Equal(2, line.RootElement.GetProperty("sequence").GetInt64());
        Assert.Equal(1, line.RootElement.GetProperty("gapFrom").GetInt64());
        Assert.Equal(1, line.RootElement.GetProperty("gapTo").GetInt64());
        using var summary = JsonDocument.Parse(File.ReadAllText(Assert.Single(Directory.GetFiles(output, "*.summary.json"))));
        Assert.Equal(3, summary.RootElement.GetProperty("trailingGapFrom").GetInt64());
        Assert.Equal(3, summary.RootElement.GetProperty("trailingGapTo").GetInt64());
        Assert.Throws<ObjectDisposedException>(() => recorder.Record("TX", [1]));
    }
    [Fact]
    public void SerialDisplayDecodesAcrossChunksAndBoundsPausedHistory()
    {
        var display = new SerialTextBuffer(40);
        display.Append(new DataBlock(1, "RX", [0xe4]));
        display.Append(new DataBlock(2, "TX", [65]));
        display.Append(new DataBlock(3, "RX", [0xb8, 0xad]));
        Assert.Equal("[TX] A[RX] 中", display.Text);
        for (int i = 0; i < 50; i++) display.Append(new DataBlock(i + 4, "RX", [66]));
        Assert.InRange(display.Text.Length, 1, 40); Assert.True(display.TrimmedCharacters > 0);
        display.Configure("gb18030", false);
        var bytes = SerialSession.ParseSend("中文", false, "\r\n", SerialTextBuffer.GetEncoding("gb18030"));
        display.Append(new DataBlock(55, "TX", bytes));
        Assert.Equal("[TX] 中文\r\n", display.Text);
        Assert.Throws<System.Text.EncoderFallbackException>(() => SerialSession.ParseSend("中文", false, "", SerialTextBuffer.GetEncoding("us-ascii")));
    }
    [Fact]
    public async Task SerialDisposedSessionRejectsReopenWithoutTouchingAPort()
    {
        var session = new SerialSession();
        await Task.WhenAll(session.DisposeAsync().AsTask(), session.DisposeAsync().AsTask());
        await Assert.ThrowsAsync<ObjectDisposedException>(() => session.OpenAsync(new SerialSettings("not-accessed"), null));
        Assert.False(session.Connected);
    }
    [Fact]
    public async Task RecordingReportsActualFileCreationFailure()
    {
        var output = Path.Combine(root, "removed-record-directory");
        var recorder = new OrderedRecorder(output);
        Directory.Move(output, output + "-moved");
        recorder.Record("RX", [1, 2, 3]);
        await recorder.DisposeAsync();
        Assert.NotNull(recorder.Error);
    }
    [Fact]
    public void ReadPlanBoundaryRejectsOldGenerationAndPreservesPendingValidPlan()
    {
        var variable = new Variable("global:value", "value", 0x20000000, "int32", 4, "可读取");
        var initial = new ReadPlan(1, 100, [variable]); initial.Validate();
        var mailbox = new ReadPlanMailbox(initial);
        mailbox.Submit(new ReadPlan(2, 70, [variable with { Address = 0x20000004 }]));
        Assert.Throws<InvalidDataException>(() => mailbox.Submit(new ReadPlan(3, 70, [variable with { Address = uint.MaxValue }])));
        Assert.Throws<InvalidDataException>(() => mailbox.Submit(new ReadPlan(2, 70, [variable])));
        var next = mailbox.Take()!;
        Assert.Equal(2, next.Generation); Assert.Equal(0x20000004UL, next.Variables[0].Address); Assert.Null(mailbox.Take());
        Assert.Throws<InvalidDataException>(() => new ReadPlan(3, 100, [variable, variable]).Validate());
        Assert.Throws<InvalidDataException>(() => new ReadPlan(3, 100, [variable with { Size = 1 }]).Validate());
    }
    [Fact]
    public async Task ActualWorkerRejectsInvalidLibraryAndIsReapedBeforeRetry()
    {
        var repository = Directory.GetParent(Directory.GetParent(fixtures)!.FullName)!.FullName;
        var worker = Path.Combine(repository, "src/EmbeddedHelper.Worker/bin/Release/net10.0/EmbeddedHelper.Worker.exe");
        Assert.True(File.Exists(worker), "Build the complete solution in Release before this process integration test.");
        var invalidLibrary = Path.Combine(root, "not-a-native-library.dll");
        File.WriteAllText(invalidLibrary, "This is deliberately invalid; no SEGGER library or hardware is used.");
        var request = new ObservationRequest(invalidLibrary, 1, "", 19011, "STM32G474VE", "SWD", 8000, 100, false, []);
        await using var session = new ObservationSession();
        await Assert.ThrowsAnyAsync<Exception>(() => session.StartAsync(worker, request));
        Assert.True(session.CanRetry);
        await session.DisposeAsync();
        Assert.Equal("已释放", session.State);
        await using var retry = new ObservationSession();
        await Assert.ThrowsAnyAsync<Exception>(() => retry.StartAsync(worker, request));
        await retry.DisposeAsync();
        Assert.NotEqual(session.SessionId, retry.SessionId);
        Assert.Equal("已释放", retry.State);
    }
    [Theory]
    [InlineData("../escape.exe")]
    [InlineData("/absolute.exe")]
    [InlineData("C:/drive.exe")]
    [InlineData("a/../escape.exe")]
    [InlineData("CON.txt")]
    [InlineData("nested/trailing. ")]
    public void ToolArchiveRejectsWindowsTraversalAndReservedNames(string name)
    {
        using var bytes = new MemoryStream();
        using (var create = new System.IO.Compression.ZipArchive(bytes, System.IO.Compression.ZipArchiveMode.Create, true)) create.CreateEntry(name);
        bytes.Position = 0;
        using var archive = new System.IO.Compression.ZipArchive(bytes, System.IO.Compression.ZipArchiveMode.Read);
        Assert.Throws<InvalidDataException>(() => ToolCache.ValidateArchive(archive));
    }
    [Fact]
    public async Task ToolCacheRejectsWrongDigestBeforeExecutingAndLocksExactVersion()
    {
        var zip = Path.Combine(root, "untrusted.zip"); File.WriteAllText(zip, "invalid");
        await Assert.ThrowsAsync<InvalidDataException>(() => new ToolCache(root).InstallAsync(zip, ToolRelease.Known[0]));
        var project = CopyProject();
        foreach (var release in ToolRelease.Known)
        {
            var change = ToolCache.SelectVersion(project, release);
            ConfigTransaction.Apply(new ConfigPlan(project, [change]), Path.Combine(root, "journal"));
            Assert.Equal(release, ToolCache.ReadLock(project));
        }
        var lockPath = Path.Combine(project, ".embedded_helper", "tools.lock.json");
        File.WriteAllText(lockPath, File.ReadAllText(lockPath).Replace(ToolRelease.Known[1].Sha256, new string('0', 64)));
        Assert.Throws<InvalidDataException>(() => ToolCache.ReadLock(project));
    }
    [Fact]
    public void IntegrationIsIdempotentPreservesLocalBytesAndRemovesWithoutPrivateBackups()
    {
        var path = CopyProject(); var project = ProjectReader.Read(path);
        var tasks = Path.Combine(path, ".vscode", "tasks.json");
        const string original = "{\"version\":\"2.0.0\", /* root */ \"tasks\":[\n// keep , comment\n{\"label\":\"Local build\",\"command\":\"unchanged\",\"unknown\":[1,2]}, // trailing , comment\n]}";
        File.WriteAllText(tasks, original);
        var plan = VsCodeIntegration.Plan(project, project.Targets[0], "example.invalid", 19011, 2331, ToolRelease.Known[0]);
        ConfigTransaction.Apply(plan, Path.Combine(root, "private-journal"));
        var saved = File.ReadAllText(tasks);
        Assert.Contains("{\"label\":\"Local build\",\"command\":\"unchanged\",\"unknown\":[1,2]}", saved);
        Assert.Contains("// keep , comment", saved); Assert.Contains("// trailing , comment", saved);
        Assert.Empty(VsCodeIntegration.Plan(project, project.Targets[0], "example.invalid", 19011, 2331, ToolRelease.Known[0]).Changes);
        using var launch = JsonDocument.Parse(File.ReadAllText(Path.Combine(path, ".vscode/launch.json")), ProjectReader.JsonOptions);
        Assert.Equal(2, launch.RootElement.GetProperty("configurations").GetArrayLength());
        var remove = VsCodeIntegration.Remove(path);
        ConfigTransaction.Apply(remove, Path.Combine(root, "another-computer-journal"));
        using var remaining = JsonDocument.Parse(File.ReadAllText(tasks), ProjectReader.JsonOptions);
        Assert.Equal("Local build", Assert.Single(remaining.RootElement.GetProperty("tasks").EnumerateArray()).GetProperty("label").GetString());
        Assert.Contains("// keep , comment", File.ReadAllText(tasks));
        Assert.Empty(VsCodeIntegration.Remove(path).Changes);
    }
    [Fact]
    public void IntegrationRejectsChangedOwnedEntriesAndRollsBackMultiFileFailure()
    {
        var path = CopyProject(); var project = ProjectReader.Read(path);
        var original = Directory.GetFiles(path, "*", SearchOption.AllDirectories).ToDictionary(p => p, File.ReadAllText);
        var plan = VsCodeIntegration.Plan(project, project.Targets[0], "example.invalid", 19011, 2331, ToolRelease.Known[0]);
        Assert.Throws<IOException>(() => ConfigTransaction.Apply(plan, Path.Combine(root, "journal"), count => { if (count == 3) throw new IOException("injected write failure"); }));
        foreach (var file in original) Assert.Equal(file.Value, File.ReadAllText(file.Key));
        Assert.False(File.Exists(Path.Combine(path, ".vscode/tasks.json")));
        ConfigTransaction.Apply(plan, Path.Combine(root, "journal"));
        var tasks = Path.Combine(path, ".vscode/tasks.json");
        File.WriteAllText(tasks, File.ReadAllText(tasks).Replace("embedded-helper-cli.exe", "user-owned.exe"));
        Assert.Throws<InvalidDataException>(() => VsCodeIntegration.Remove(path));
        Assert.Contains("user-owned.exe", File.ReadAllText(tasks));
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
    public void Dispose() => Directory.Delete(root, true);
}

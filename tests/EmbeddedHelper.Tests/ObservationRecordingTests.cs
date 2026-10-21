using System.Diagnostics;
using System.IO.Compression;
using System.Xml.Linq;
using EmbeddedHelper.App;
using EmbeddedHelper.Core;
using Xunit;

namespace EmbeddedHelper.Tests;

public sealed class ObservationRecordingTests : TestWorkspace
{
    private static readonly Variable variable = new("global:state.nested.value", "state.nested.value", 0x20000000, "int32", 4, "可读取");
    private static ObservationEvent Sample(long sequence, string session = "test", long generation = 1, Dictionary<string, string>? values = null) =>
        new("sample", "", values ?? new() { [variable.Identity] = sequence.ToString(System.Globalization.CultureInfo.InvariantCulture) }, Sequence: sequence,
            SessionId: session, Generation: generation, ReadStartTicks: sequence * Stopwatch.Frequency, ReadEndTicks: sequence * Stopwatch.Frequency + 100, ObservedAt: DateTimeOffset.UnixEpoch.AddSeconds(sequence));
    private static string[][] Rows(ZipArchive zip, int sheet)
    {
        XNamespace ns = "http://schemas.openxmlformats.org/spreadsheetml/2006/main";
        using var stream = zip.GetEntry($"xl/worksheets/sheet{sheet}.xml")!.Open();
        return XDocument.Load(stream).Descendants(ns + "row").Select(r => r.Elements(ns + "c").Select(c => c.Value).ToArray()).ToArray();
    }
    [Fact]
    public void CompleteRecordingExceedsPlotLimitAndSplitsWithoutLosingBoundaryRows()
    {
        var source = Path.Combine(root, "record.jsonl"); var output = Path.Combine(root, "中文.xlsx");
        using (var record = new ObservationRecording(source, [variable], 200, "test"))
        { for (var i = 1; i <= 6001; i++) record.Append(Sample(i)); record.Complete(); }
        Assert.Equal(6001, ObservationExcel.Export(source, output, 2000));
        using var zip = ZipFile.OpenRead(output);
        Assert.Equal(2001, Rows(zip, 1).Length); Assert.Equal(2001, Rows(zip, 3).Length); Assert.Equal(2, Rows(zip, 4).Length);
        Assert.Equal("1", Rows(zip, 1)[1][0]); Assert.Equal("6001", Rows(zip, 4)[1][8]);
        Assert.Equal(variable.Name, Rows(zip, 1)[0][8]);
        Assert.Equal("1970-01-01T00:00:01.0000000Z", Rows(zip, 1)[1][1]);
    }
    [Fact]
    public void PlanChangesMissingValuesAndSequenceGapsRemainExplicit()
    {
        var source = Path.Combine(root, "record.jsonl"); var output = Path.Combine(root, "plan.xlsx");
        var added = variable with { Identity = "global:new", Name = "other.nested.value" };
        using (var record = new ObservationRecording(source, [variable], 200, "test"))
        {
            record.Append(Sample(1)); record.Plan([added], 100, 2);
            record.Append(Sample(2)); // Old generation already in flight when the new plan was submitted.
            record.Append(Sample(4, generation: 2, values: new()));
            record.Append(Sample(5, generation: 2, values: new() { [added.Identity] = "读取失败" }));
            record.Complete();
        }
        Assert.Equal(4, ObservationExcel.Export(source, output));
        using var zip = ZipFile.OpenRead(output);
        Assert.Equal(3, Rows(zip, 1).Length);
        var rows = Rows(zip, 2); Assert.Equal(added.Name, rows[0][8]); Assert.Equal("未返回", rows[1][8]);
        Assert.Equal("1", rows[1][2]); Assert.Equal("2", rows[1][4]); Assert.Equal("100", rows[1][5]); Assert.Equal("读取失败", rows[2][8]);
    }
    [Fact]
    public void ExportPreservesTextAndLargeIntegersWithoutFormulaEvaluation()
    {
        var source = Path.Combine(root, "record.jsonl"); var output = Path.Combine(root, "values.xlsx");
        using (var record = new ObservationRecording(source, [variable], 200, "123"))
        {
            record.Append(Sample(1, "123", values: new() { [variable.Identity] = "18446744073709551615" }));
            record.Append(Sample(2, "123", values: new() { [variable.Identity] = "=1+2" })); record.Complete();
        }
        ObservationExcel.Export(source, output);
        using var zip = ZipFile.OpenRead(output); var rows = Rows(zip, 1);
        Assert.Equal("18446744073709551615", rows[1][8]); Assert.Equal("=1+2", rows[2][8]);
        using var reader = new StreamReader(zip.GetEntry("xl/worksheets/sheet1.xml")!.Open()); var xml = reader.ReadToEnd();
        Assert.DoesNotContain("<f>", xml); Assert.Contains("<t xml:space=\"preserve\">123</t>", xml);
    }
    [Fact]
    public void EmptyIncompleteAndForeignSessionsCannotReplaceAnExistingExport()
    {
        var source = Path.Combine(root, "empty.jsonl"); var output = Path.Combine(root, "keep.xlsx"); File.WriteAllText(output, "keep");
        using (var record = new ObservationRecording(source, [variable], 200, "test")) record.Complete();
        Assert.Throws<InvalidDataException>(() => ObservationExcel.Export(source, output));
        Assert.Equal("keep", File.ReadAllText(output));
        source = Path.Combine(root, "partial.jsonl");
        using (var record = new ObservationRecording(source, [variable], 200, "test"))
        { record.Append(Sample(1)); Assert.Throws<InvalidDataException>(() => record.Append(Sample(2, "another"))); }
        Assert.Throws<InvalidDataException>(() => ObservationExcel.Export(source, output));
        Assert.Equal("keep", File.ReadAllText(output)); Assert.Empty(Directory.GetFiles(root, "*.tmp"));
    }
    [Fact]
    public async Task RecordingCreationFailurePreventsWorkerLaunchAndExportFailureKeepsSource()
    {
        var source = Path.Combine(root, "record.jsonl"); Directory.CreateDirectory(source);
        var dll = Path.Combine(root, "fake.dll"); File.WriteAllText(dll, "not loaded");
        await using var session = new ObservationSession();
        await Assert.ThrowsAsync<UnauthorizedAccessException>(() => session.StartAsync("must-not-start.exe", new(dll, 1, "", 1, "G4", "SWD", 8000, 200, false, [variable], RecordingPath: source)));
        source = Path.Combine(root, "valid.jsonl");
        using (var record = new ObservationRecording(source, [variable], 200, "test")) { record.Append(Sample(1)); record.Complete(); }
        var before = File.ReadAllBytes(source);
        Assert.Throws<DirectoryNotFoundException>(() => ObservationExcel.Export(source, Path.Combine(root, "missing", "out.xlsx")));
        Assert.Equal(before, File.ReadAllBytes(source)); Assert.Equal(1, ObservationExcel.Export(source, Path.Combine(root, "retry.xlsx")));
        Assert.Throws<ArgumentOutOfRangeException>(() => ObservationExcel.Export(source, Path.Combine(root, "limit.xlsx"), 1_048_576));
    }
    [Fact]
    public void ExportRequiresStoppedSessionAndPausingDoesNotEnableIt()
    {
        var vm = new ObservationViewModel { HasRecording = true, HasSession = true, Paused = true };
        Assert.False(vm.CanExport); vm.HasSession = false; Assert.True(vm.CanExport);
        vm.Exporting = true; Assert.False(vm.CanExport); vm.Exporting = false; vm.Busy = true; Assert.False(vm.CanExport);
    }
}

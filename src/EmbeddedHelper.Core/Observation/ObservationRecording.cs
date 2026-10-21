using System.Globalization;
using System.IO.Compression;
using System.Text;
using System.Text.Json;
using System.Xml;

namespace EmbeddedHelper.Core;

public sealed record ObservationRecordingHeader(Variable[] Variables, int SampleRate, string SessionId, long Generation, long TickFrequency, string Kind = "recording-plan");

/// <summary>Records received samples before the bounded display queue; the plot is not the recording.</summary>
public sealed class ObservationRecording : IDisposable
{
    private readonly StreamWriter writer;
    private readonly object sync = new();
    private readonly string sessionId;
    public ObservationRecording(string path, Variable[] variables, int sampleRate, string sessionId, long generation = 1)
    {
        this.sessionId = sessionId;
        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(path))!);
        writer = new StreamWriter(new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.Read), new UTF8Encoding(false));
        try { Plan(variables, sampleRate, generation); }
        catch { writer.Dispose(); throw; }
    }
    public void Plan(Variable[] variables, int sampleRate, long generation)
    {
        lock (sync)
        {
            writer.WriteLine(JsonSerializer.Serialize(new ObservationRecordingHeader(variables, sampleRate, sessionId, generation, System.Diagnostics.Stopwatch.Frequency)));
            writer.Flush();
        }
    }
    public void Append(ObservationEvent sample)
    {
        if (sample.SessionId != sessionId) throw new InvalidDataException("记录收到其他会话的数据。");
        if (sample.Kind == "sample") lock (sync) writer.WriteLine(JsonSerializer.Serialize(sample));
    }
    public void Complete()
    {
        lock (sync) { writer.WriteLine(JsonSerializer.Serialize(new { Kind = "recording-end", SessionId = sessionId })); writer.Flush(); }
    }
    public void Dispose() { lock (sync) writer.Dispose(); }
}

public static class ObservationExcel
{
    private const string SheetNs = "http://schemas.openxmlformats.org/spreadsheetml/2006/main";
    public static long Export(string recording, string destination, int rowsPerSheet = 1_048_575)
    {
        if (rowsPerSheet is < 1 or > 1_048_575) throw new ArgumentOutOfRangeException(nameof(rowsPerSheet));
        if (Path.GetFullPath(recording).Equals(Path.GetFullPath(destination), StringComparison.OrdinalIgnoreCase)) throw new IOException("导出文件不能覆盖原始记录。");
        // Exclusive writer ownership must have ended before exporting a complete session.
        using var input = new StreamReader(new FileStream(recording, FileMode.Open, FileAccess.Read, FileShare.Read));
        var header = JsonSerializer.Deserialize<ObservationRecordingHeader>(input.ReadLine() ?? "") ?? throw new InvalidDataException("采集记录缺少表头。");
        if (header.Kind != "recording-plan" || string.IsNullOrEmpty(header.SessionId) || header.TickFrequency <= 0) throw new InvalidDataException("采集记录表头无效。");
        var plans = new Dictionary<long, ObservationRecordingHeader> { [header.Generation] = header };
        var ended = false;
        ObservationEvent? Next()
        {
            while (input.ReadLine() is { } line)
            {
                using var doc = JsonDocument.Parse(line);
                if (ended) throw new InvalidDataException("结束标记之后仍有数据。");
                if (doc.RootElement.GetProperty("Kind").GetString() == "recording-end")
                {
                    if (doc.RootElement.GetProperty("SessionId").GetString() != header.SessionId) throw new InvalidDataException("结束标记会话不匹配。");
                    ended = true; continue;
                }
                if (doc.RootElement.GetProperty("Kind").GetString() == "recording-plan")
                {
                    var plan = JsonSerializer.Deserialize<ObservationRecordingHeader>(line)!;
                    if (plan.SessionId != header.SessionId || plan.TickFrequency != header.TickFrequency || !plans.TryAdd(plan.Generation, plan)) throw new InvalidDataException("记录计划身份或代次无效。");
                    continue;
                }
                var sample = JsonSerializer.Deserialize<ObservationEvent>(line) ?? throw new InvalidDataException("采集记录损坏。");
                if (sample.Kind != "sample" || sample.SessionId != header.SessionId || !plans.ContainsKey(sample.Generation)) throw new InvalidDataException("记录样本身份或计划无效。");
                return sample;
            }
            if (!ended) throw new InvalidDataException("记录未完整结束，可能发生写入失败或异常退出；原始记录已保留。");
            return null;
        }
        var next = Next() ?? throw new InvalidDataException("本次采集没有收到数据，无法导出。");
        var origin = next.ReadStartTicks;
        var temporary = destination + "." + Guid.NewGuid().ToString("N") + ".tmp";
        long total = 0, previous = 0;
        try
        {
            using (var archive = ZipFile.Open(temporary, ZipArchiveMode.Create))
            {
                var sheets = 0;
                do
                {
                    var active = plans[next!.Generation];
                    sheets++;
                    using var xml = XmlWriter.Create(archive.CreateEntry($"xl/worksheets/sheet{sheets}.xml").Open(), new XmlWriterSettings { Encoding = new UTF8Encoding(false), CloseOutput = true });
                    xml.WriteStartElement("worksheet", SheetNs); xml.WriteStartElement("sheetData", SheetNs);
                    Row(xml, new[] { "序号", "采样时间（UTC）", "此前缺失批次", "会话", "读计划代次", "设置频率（Hz）", "读取开始（秒）", "读取结束（秒）" }.Concat(active.Variables.Select(v => v.Name)), false);
                    for (var count = 0; next != null && next.Generation == active.Generation && count < rowsPerSheet; next = Next())
                    {
                        var sample = next;
                        if (sample.Sequence <= previous) throw new InvalidDataException("记录批次序号未递增。");
                        var gap = Math.Max(0, sample.Sequence - previous - 1);
                        Row(xml, new[] { sample.Sequence.ToString(CultureInfo.InvariantCulture), sample.ObservedAt?.UtcDateTime.ToString("O", CultureInfo.InvariantCulture) ?? "", gap.ToString(CultureInfo.InvariantCulture), sample.SessionId, sample.Generation.ToString(CultureInfo.InvariantCulture), active.SampleRate.ToString(CultureInfo.InvariantCulture),
                            ((sample.ReadStartTicks - origin) / (double)header.TickFrequency).ToString("R", CultureInfo.InvariantCulture), ((sample.ReadEndTicks - origin) / (double)header.TickFrequency).ToString("R", CultureInfo.InvariantCulture) }
                            .Concat(active.Variables.Select(v => sample.Values?.GetValueOrDefault(v.Identity) ?? "未返回")), true);
                        previous = sample.Sequence; count++; total++;
                    }
                    xml.WriteEndElement(); xml.WriteEndElement();
                } while (next != null);
                Write(archive, "[Content_Types].xml", "<Types xmlns=\"http://schemas.openxmlformats.org/package/2006/content-types\"><Default Extension=\"rels\" ContentType=\"application/vnd.openxmlformats-package.relationships+xml\"/><Default Extension=\"xml\" ContentType=\"application/xml\"/><Override PartName=\"/xl/workbook.xml\" ContentType=\"application/vnd.openxmlformats-officedocument.spreadsheetml.sheet.main+xml\"/>" + string.Concat(Enumerable.Range(1, sheets).Select(i => $"<Override PartName=\"/xl/worksheets/sheet{i}.xml\" ContentType=\"application/vnd.openxmlformats-officedocument.spreadsheetml.worksheet+xml\"/>")) + "</Types>");
                Write(archive, "_rels/.rels", "<Relationships xmlns=\"http://schemas.openxmlformats.org/package/2006/relationships\"><Relationship Id=\"rId1\" Type=\"http://schemas.openxmlformats.org/officeDocument/2006/relationships/officeDocument\" Target=\"xl/workbook.xml\"/></Relationships>");
                Write(archive, "xl/workbook.xml", $"<workbook xmlns=\"{SheetNs}\" xmlns:r=\"http://schemas.openxmlformats.org/officeDocument/2006/relationships\"><sheets>" + string.Concat(Enumerable.Range(1, sheets).Select(i => $"<sheet name=\"采集数据{i}\" sheetId=\"{i}\" r:id=\"rId{i}\"/>")) + "</sheets></workbook>");
                Write(archive, "xl/_rels/workbook.xml.rels", "<Relationships xmlns=\"http://schemas.openxmlformats.org/package/2006/relationships\">" + string.Concat(Enumerable.Range(1, sheets).Select(i => $"<Relationship Id=\"rId{i}\" Type=\"http://schemas.openxmlformats.org/officeDocument/2006/relationships/worksheet\" Target=\"worksheets/sheet{i}.xml\"/>")) + "</Relationships>");
            }
            File.Move(temporary, destination, true);
            return total;
        }
        finally { if (File.Exists(temporary)) File.Delete(temporary); }
    }
    private static void Row(XmlWriter xml, IEnumerable<string> values, bool numeric)
    {
        xml.WriteStartElement("row", SheetNs);
        var column = 0;
        foreach (var value in values)
        {
            xml.WriteStartElement("c", SheetNs);
            // Keep wide integers as text to avoid Excel's 15-digit precision loss.
            if (numeric && column is not (1 or 3) && double.TryParse(value, NumberStyles.Float, CultureInfo.InvariantCulture, out var number) && double.IsFinite(number) && value.Count(char.IsDigit) <= 15)
                xml.WriteElementString("v", SheetNs, number.ToString("R", CultureInfo.InvariantCulture));
            else
            {
                xml.WriteAttributeString("t", "inlineStr"); xml.WriteStartElement("is", SheetNs);
                xml.WriteStartElement("t", SheetNs); xml.WriteAttributeString("xml", "space", null, "preserve"); xml.WriteString(value); xml.WriteEndElement(); xml.WriteEndElement();
            }
            xml.WriteEndElement();
            column++;
        }
        xml.WriteEndElement();
    }
    private static void Write(ZipArchive archive, string name, string text)
    {
        using var writer = new StreamWriter(archive.CreateEntry(name).Open(), new UTF8Encoding(false));
        writer.Write(text);
    }
}

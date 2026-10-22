using EmbeddedHelper.App;
using EmbeddedHelper.Core;
using LibObjectFile.Elf;
using Xunit;

namespace EmbeddedHelper.Tests;

[Collection("Offline client")]
public sealed class ElfFeedbackTests : TestWorkspace
{
    [Theory]
    [InlineData("readelf 未找到固定地址变量。", "未找到固定地址变量", "重新构建")]
    [InlineData("ARM readelf 解析失败。", "解析工具", "设置与诊断")]
    [InlineData("ELF 调试信息长度无效。", "调试信息损坏", "ELF32")]
    [InlineData("ELF 超过 256 MiB 解析上限。", "超过解析上限", "256 MiB")]
    public void FailureClassificationAndActionAgree(string reason, string summary, string action)
    {
        var error = new InvalidDataException(reason);
        var feedback = ElfParseFeedback.Failure(error);
        Assert.Contains(summary, feedback.Message); Assert.Contains(action, feedback.Message);
        Assert.Equal(error.ToString(), feedback.Details);
        if (reason.Contains("固定地址变量")) Assert.DoesNotContain("设置与诊断", feedback.Message);
        Assert.Contains("权限", ElfParseFeedback.Failure(new UnauthorizedAccessException("denied")).Message);
    }
    [Fact]
    public async Task RealFilesReportFailureAndCanRetryWithoutKeepingOldSuccess()
    {
        var valid = Path.Combine(fixtures, "structures.elf");
        var index = await ElfIndexService.ReadAsync(valid, "");
        Assert.Contains("解析成功", ElfParseFeedback.Success(index).Message);
        Assert.Contains(index, v => v.Name == "chassis.feedback.speed" && v.Supported);
        var corrupt = Path.Combine(root, "broken.elf"); File.WriteAllText(corrupt, "broken");
        var error = await Assert.ThrowsAnyAsync<Exception>(() => ElfIndexService.ReadAsync(corrupt, ""));
        var failure = ElfParseFeedback.Failure(error);
        Assert.StartsWith("解析失败", failure.Message); Assert.Contains("重试", failure.Message); Assert.NotEmpty(failure.Details);
        File.Copy(valid, corrupt, true);
        Assert.Equal(index.Length, (await ElfIndexService.ReadAsync(corrupt, "")).Length);
        Assert.Empty(ElfParseFeedback.Success(index).Details);
    }
    [Fact]
    public async Task MissingEmptyAndOversizedFilesHaveActionableFeedbackBeforeHashing()
    {
        var path = Path.Combine(root, "file.elf");
        var missing = await Assert.ThrowsAsync<FileNotFoundException>(() => ElfIndexService.ReadAsync(path, ""));
        Assert.Contains("重新选择", ElfParseFeedback.Failure(missing).Message);
        File.WriteAllBytes(path, []);
        var empty = await Assert.ThrowsAsync<InvalidDataException>(() => ElfIndexService.ReadAsync(path, ""));
        Assert.StartsWith("解析失败", ElfParseFeedback.Failure(empty).Message);
        using (var file = File.OpenWrite(path)) file.SetLength(256L * 1024 * 1024 + 1);
        var huge = await Assert.ThrowsAsync<InvalidDataException>(() => ElfIndexService.ReadAsync(path, ""));
        Assert.Contains("256 MiB", ElfParseFeedback.Failure(huge).Message);
    }
    [Fact]
    public async Task RealDwarf5MissingToolAndStrippedDebugInformationFailClearly()
    {
        var missingTool = await Assert.ThrowsAsync<InvalidDataException>(() => ElfIndexService.ReadAsync(
            Path.Combine(fixtures, "structures5.elf"), Path.Combine(root, "missing-readelf.exe")));
        Assert.Contains("设置与诊断", ElfParseFeedback.Failure(missingTool).Message);
        var bytes = File.ReadAllBytes(Path.Combine(fixtures, "scalars.elf"));
        var offset = bytes.AsSpan().IndexOf(".debug_info\0"u8);
        Assert.True(offset >= 0);
        bytes[offset + 1] = (byte)'x';
        var stripped = Path.Combine(root, "no-debug.elf"); File.WriteAllBytes(stripped, bytes);
        var missingDebug = await Assert.ThrowsAnyAsync<Exception>(() => ElfIndexService.ReadAsync(stripped, ""));
        Assert.Contains("调试信息", ElfParseFeedback.Failure(missingDebug).Message);
    }
    [Fact]
    public void ZeroReadableItemsAndMissingToolsAreNotSuccess()
    {
        Assert.StartsWith("解析完成：0", ElfParseFeedback.Success([]).Message);
        Assert.StartsWith("解析完成：0", ElfParseFeedback.Success([new("x", "x", 0, "pointer", 4, "不支持")]).Message);
        var tools = ElfParseFeedback.Failure(new InvalidDataException("该 ELF 的 DWARF 格式需要 ARM readelf。"));
        Assert.Contains("设置与诊断", tools.Message); Assert.DoesNotContain("解析成功", tools.Message);
        Assert.Contains("-g -gdwarf-4", ElfParseFeedback.Failure(new InvalidDataException("ELF 缺少可用调试信息。")).Message);
    }
    [Fact]
    public async Task AxfUsesTheSameReaderAndDoesNotModifyInput()
    {
        var source = Path.Combine(fixtures, "structures.elf");
        var axf = Path.Combine(root, "固件 AXF.AXF"); File.Copy(source, axf);
        var before = File.ReadAllBytes(axf);
        Assert.Equal(await ElfIndexService.ReadAsync(source, ""), await ElfIndexService.ReadAsync(axf, ""));
        Assert.Equal(before, File.ReadAllBytes(axf));
        var badDebug = ElfParseFeedback.Failure(new InvalidDataException("ELF 调试信息长度无效。"));
        Assert.Contains("损坏", badDebug.Message);
        Assert.DoesNotContain("请启用", badDebug.Message);
    }
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void DwarfCompatibilityFailureFallsBackAndRetainsBothErrors(bool trailingDie)
    {
        var bytes = File.ReadAllBytes(Path.Combine(fixtures, "structures.elf"));
        using (var stream = new MemoryStream(bytes))
        {
            var elf = ElfFile.Read(stream);
            var section = elf.Sections.Single(s => s.Name.ToString() == (trailingDie ? ".debug_info" : ".debug_line"));
            // DWARF3 opcode 9 uses one operand; reproduce the armasm header variation without changing variable DIEs.
            var index = checked((int)section.Position + (trailingDie ? (int)section.Size - 1 : 23));
            Assert.Equal(trailingDie ? 0 : 1, bytes[index]); bytes[index] = trailingDie ? (byte)0x80 : (byte)0;
        }
        var path = Path.Combine(root, "line-compat.axf"); File.WriteAllBytes(path, bytes);
        var missing = Assert.Throws<InvalidDataException>(() => ElfIndex.Read(path, Path.Combine(root, "missing-readelf.exe")));
        Assert.Contains("内置 DWARF 解析失败", missing.Message);
        Assert.NotNull(missing.InnerException);
        var failed = Assert.Throws<InvalidDataException>(() => ElfIndex.Read(path, Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.System), "where.exe")));
        Assert.Contains("均失败", failed.Message);
        Assert.Equal(2, Assert.IsType<AggregateException>(failed.InnerException).InnerExceptions.Count);
        Assert.Equal(bytes, File.ReadAllBytes(path));
    }
    [Fact]
    public void InvalidContainerAndMissingDebugDoNotRequestFallback()
    {
        var bytes = File.ReadAllBytes(Path.Combine(fixtures, "structures.elf"));
        var path = Path.Combine(root, "invalid.axf");
        bytes[18] = 62; File.WriteAllBytes(path, bytes);
        var error = Assert.Throws<InvalidDataException>(() => ElfIndex.Read(path, "missing-readelf.exe"));
        Assert.DoesNotContain("readelf", error.Message);
        Assert.Contains("ARM", error.Message);
        Assert.Throws<FileNotFoundException>(() => ElfIndex.Read(path + ".missing", "missing-readelf.exe"));
    }
}

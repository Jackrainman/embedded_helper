using EmbeddedHelper.App;
using EmbeddedHelper.Core;
using Xunit;

namespace EmbeddedHelper.Tests;

[Collection("Offline client")]
public sealed class ElfFeedbackTests : TestWorkspace
{
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
}

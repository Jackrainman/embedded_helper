using EmbeddedHelper.Core;
using Xunit;

namespace EmbeddedHelper.Tests;

public sealed class EidePreservationTests
{
    [Theory]
    [InlineData("\r\n", true)]
    [InlineData("\r\n", false)]
    [InlineData("\n", true)]
    [InlineData("\n", false)]
    public void RemovalPreservesCommentLineEnding(string newline, bool final)
    {
        var input = "unknown: yes" + newline + "field: value # keep" + (final ? newline : "");
        Assert.Equal("unknown: yes" + newline + "# keep" + (final ? newline : ""), EideUpload.Set(input, ["field"], null));
        var empty = "unknown: yes" + newline + "parent: # preserve" + (final ? newline : "");
        var inserted = EideUpload.Set(empty, ["parent", "child"], "42");
        Assert.Equal("42", EideUpload.Get(inserted, ["parent", "child"]));
        Assert.Contains("unknown: yes" + newline, inserted);
        Assert.Contains("parent: # preserve", inserted);
    }
}

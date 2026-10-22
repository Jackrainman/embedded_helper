using EmbeddedHelper.Core;
using Xunit;

namespace EmbeddedHelper.Tests;

public sealed class ObservationControlTests
{
    [Theory]
    [InlineData("", 0)]
    [InlineData("{}", 1)]
    [InlineData("not-json", 1)]
    public void EofAndProtocolErrorsHaveDifferentExitCodes(string text, int expected)
    {
        var errors = new StringWriter();
        var mailbox = new ReadPlanMailbox(new(1, 200, []));
        Assert.Equal(expected, ObservationControl.Read(new StringReader(text), mailbox, errors));
        Assert.Equal(expected != 0, errors.ToString().Contains("控制通道失败"));
        Assert.Equal(1, ObservationControl.Read(new StringReader(new string('x', 1048577)), mailbox, errors));
        Assert.Contains("过长", errors.ToString());
    }
}

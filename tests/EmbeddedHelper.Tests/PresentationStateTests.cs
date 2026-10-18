using EmbeddedHelper.App;
using EmbeddedHelper.Core;
using Xunit;

namespace EmbeddedHelper.Tests;

public sealed class PresentationStateTests
{
    [Fact]
    public void ObservationButtonsFollowPreparationAndSessionStateWithoutPollingNotifications()
    {
        var view = new ObservationViewModel();
        Assert.False(view.CanStart); Assert.True(view.NeedsConfiguration);
        view.Ready = true; Assert.True(view.CanStart);
        view.Busy = true; Assert.False(view.CanStart); Assert.False(view.CanEdit);
        view.HasSession = true; Assert.True(view.CanStop);
        view.Busy = false; Assert.False(view.CanStart); Assert.False(view.CanEdit);
        view.CanRetry = true; Assert.True(view.CanStart); Assert.Equal("清理并重试", view.StartText);
        var notifications = 0; view.PropertyChanged += (_, _) => notifications++;
        view.Busy = false; view.HasSession = true; view.CanRetry = true;
        Assert.Equal(0, notifications);
    }
    [Fact]
    public void VariableValueDoesNotNotifyWhenItsDisplayHasNotChanged()
    {
        var row = new VariableRow(new("global:x", "x", 0x20000000, "int32", 4, "可读取"));
        var notifications = 0; row.PropertyChanged += (_, _) => notifications++;
        row.Value = "1"; row.Value = "1"; row.Value = "1 / 过期";
        Assert.Equal(2, notifications);
    }
    [Fact]
    public void RemoteInputChangeInvalidatesPreviewAndBusyPreventsApply()
    {
        var view = new RemoteViewModel();
        var plan = new ConfigPlan("root", [new FileChange("file", null, "after")]);
        view.ShowPlan(plan); Assert.True(view.CanApply);
        view.Host = "edited.invalid"; Assert.Null(view.Plan); Assert.False(view.CanApply);
        view.ShowPlan(plan); view.Busy = true; Assert.False(view.CanApply); Assert.False(view.CanEdit);
        view.Busy = false; Assert.True(view.CanApply);
        view.Port = "not a port"; Assert.Null(view.Plan);
        view.ShowPlan(plan); view.EideUpload = true; Assert.Null(view.Plan);
        view.ShowPlan(plan); view.Invalidate(); Assert.False(view.CanApply);
    }
}

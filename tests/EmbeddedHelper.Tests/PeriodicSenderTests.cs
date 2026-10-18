using EmbeddedHelper.Core;
using Xunit;

namespace EmbeddedHelper.Tests;

public sealed class PeriodicSenderTests
{
    [Fact]
    public async Task StopCancelsAndWaitsForInFlightSendWithoutStartingAnother()
    {
        var sender = new PeriodicSender();
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var cancelled = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var finish = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var count = 0;
        var running = sender.StartAsync(async token =>
        {
            count++; entered.SetResult();
            using var registration = token.Register(() => cancelled.TrySetResult());
            await finish.Task;
            token.ThrowIfCancellationRequested();
        }, 10);
        await entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Equal(PeriodicSendState.Running, sender.Status.State);
        Assert.Throws<InvalidOperationException>(() => { _ = sender.StartAsync(_ => Task.CompletedTask, 10); });
        var stop = sender.StopAsync();
        await cancelled.Task.WaitAsync(TimeSpan.FromSeconds(5)); Assert.False(stop.IsCompleted);
        Assert.Equal(PeriodicSendState.Stopping, sender.Status.State);
        finish.SetResult(); await stop; await running;
        Assert.Equal(1, count); Assert.False(sender.IsRunning);
        Assert.Equal(PeriodicSendState.Stopped, sender.Status.State);
    }
    [Fact]
    public async Task SendFailureClearsRunningStateAndDoesNotBecomeSuccess()
    {
        var sender = new PeriodicSender();
        await Assert.ThrowsAsync<IOException>(() => sender.StartAsync(_ => throw new IOException("driver rejected"), 10));
        Assert.False(sender.IsRunning);
        Assert.Equal(PeriodicSendState.Faulted, sender.Status.State);
        Assert.Equal("driver rejected", sender.Status.Error);
        Assert.Equal(0, sender.Status.Count);
        await Assert.ThrowsAsync<IOException>(sender.StopAsync);
        Assert.Throws<ArgumentException>(() => { _ = sender.StartAsync(_ => Task.CompletedTask, 9); });
    }
    [Fact]
    public async Task IndependentSerialControllerClearsFailedOpenAndCloseIsIdempotent()
    {
        var serial = new SerialController();
        await Assert.ThrowsAsync<ArgumentException>(() => serial.OpenAsync(new SerialSettings("", -1), null));
        Assert.Null(serial.Current); Assert.False(serial.IsOpening);
        await serial.CloseAsync(); await serial.CloseAsync();
        Assert.False(serial.IsClosing); Assert.False(serial.IsPeriodic);
        await Assert.ThrowsAsync<InvalidOperationException>(() => serial.StartPeriodicAsync(_ => Task.CompletedTask, 10));
    }
}

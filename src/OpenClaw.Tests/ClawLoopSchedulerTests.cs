using Microsoft.Extensions.Logging;
using NSubstitute;
using OpenClaw.Core.Loops;
using Xunit;

namespace OpenClaw.Tests;

public sealed class ClawLoopSchedulerTests
{
    private readonly ILogger<ClawLoopScheduler> _mockLogger = Substitute.For<ILogger<ClawLoopScheduler>>();

    [Fact]
    public async Task ScheduleLoop_AddsEntry()
    {
        var scheduler = new ClawLoopScheduler(_mockLogger);
        await scheduler.ScheduleLoopAsync("s1", "*/5 * * * *", "check status", CancellationToken.None);

        var status = await scheduler.GetLoopStatusAsync("s1", CancellationToken.None);
        Assert.NotNull(status);
        Assert.Contains("*/5 * * * *", status);
        Assert.Contains("check status", status);
    }

    [Fact]
    public async Task CancelLoop_RemovesEntry()
    {
        var scheduler = new ClawLoopScheduler(_mockLogger);
        await scheduler.ScheduleLoopAsync("s1", "*/5 * * * *", "check status", CancellationToken.None);
        await scheduler.CancelLoopAsync("s1", CancellationToken.None);

        var status = await scheduler.GetLoopStatusAsync("s1", CancellationToken.None);
        Assert.Null(status);
    }

    [Fact]
    public async Task SignalComplete_CancelsLoop()
    {
        var scheduler = new ClawLoopScheduler(_mockLogger);
        await scheduler.ScheduleLoopAsync("s1", "*/5 * * * *", "check status", CancellationToken.None);

        var control = (ILoopControlService)scheduler;
        await control.SignalCompleteAsync("s1", CancellationToken.None);

        var status = await scheduler.GetLoopStatusAsync("s1", CancellationToken.None);
        Assert.Null(status);
    }

    [Fact]
    public async Task GetLoopStatus_NoEntry_ReturnsNull()
    {
        var scheduler = new ClawLoopScheduler(_mockLogger);
        var status = await scheduler.GetLoopStatusAsync("nonexistent", CancellationToken.None);
        Assert.Null(status);
    }

    [Fact]
    public async Task ScheduleLoop_OverwritesExisting()
    {
        var scheduler = new ClawLoopScheduler(_mockLogger);
        await scheduler.ScheduleLoopAsync("s1", "*/5 * * * *", "first prompt", CancellationToken.None);
        await scheduler.ScheduleLoopAsync("s1", "*/10 * * * *", "second prompt", CancellationToken.None);

        var status = await scheduler.GetLoopStatusAsync("s1", CancellationToken.None);
        Assert.NotNull(status);
        Assert.Contains("second prompt", status);
    }

    [Theory]
    [InlineData("5m", "*/5 * * * *")]
    [InlineData("30s", "*/30 * * * * *")]
    [InlineData("120s", "*/2 * * * *")]
    [InlineData("1h", "0 */1 * * *")]
    public void IntervalToCron_ConvertsCorrectly(string interval, string expectedCron)
    {
        var cron = ClawLoopScheduler.IntervalToCron(interval);
        Assert.Equal(expectedCron, cron);
    }

    [Theory]
    [InlineData("")]
    [InlineData("5x")]
    [InlineData("abc")]
    public void IntervalToCron_Invalid_Throws(string interval)
    {
        Assert.Throws<ArgumentException>(() => ClawLoopScheduler.IntervalToCron(interval));
    }

    [Fact]
    public async Task ScheduleLoop_SecondsCronExpression_DoesNotThrow()
    {
        var scheduler = new ClawLoopScheduler(_mockLogger);

        await scheduler.ScheduleLoopAsync("s1", "*/30 * * * * *", "check status", TestContext.Current.CancellationToken);

        var status = await scheduler.GetLoopStatusAsync("s1", TestContext.Current.CancellationToken);
        Assert.NotNull(status);
        Assert.Contains("*/30 * * * * *", status);
    }

    [Fact]
    public async Task ScheduleLoop_InvalidCron_ThrowsArgumentException()
    {
        var scheduler = new ClawLoopScheduler(_mockLogger);

        var ex = await Assert.ThrowsAsync<ArgumentException>(() =>
            scheduler.ScheduleLoopAsync("s1", "not a cron", "check status", TestContext.Current.CancellationToken));
        Assert.Contains("Invalid cron expression", ex.Message);
    }

    [Fact]
    public async Task GetDueEntries_ReturnsDueLoops()
    {
        var scheduler = new ClawLoopScheduler(_mockLogger);
        await scheduler.ScheduleLoopAsync("s1", "*/1 * * * * *", "check status", TestContext.Current.CancellationToken);

        await Task.Delay(TimeSpan.FromMilliseconds(1100), TestContext.Current.CancellationToken);

        var due = scheduler.GetDueEntries(DateTimeOffset.UtcNow);
        var entry = Assert.Single(due);
        Assert.Equal("s1", entry.SessionId);
        Assert.Equal("check status", entry.Prompt);
    }

    [Fact]
    public async Task IsDue_ConcurrentAccess_NoRaceCondition()
    {
        var scheduler = new ClawLoopScheduler(_mockLogger);
        await scheduler.ScheduleLoopAsync("s1", "* * * * *", "check status", TestContext.Current.CancellationToken);
        var now = DateTimeOffset.UtcNow.AddYears(1);
        const int workerCount = 32;
        using var start = new Barrier(workerCount);

        var tasks = Enumerable.Range(0, workerCount)
            .Select(_ => Task.Factory.StartNew(() =>
            {
                start.SignalAndWait(TestContext.Current.CancellationToken);
                return scheduler.GetDueEntries(now).Count;
            }, TestContext.Current.CancellationToken, TaskCreationOptions.LongRunning, TaskScheduler.Default))
            .ToArray();

        var returnedEntries = await Task.WhenAll(tasks);
        Assert.Equal(1, returnedEntries.Sum());
    }
}

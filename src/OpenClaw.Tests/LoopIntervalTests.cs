using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;
using OpenClaw.Core.Loops;
using Xunit;

namespace OpenClaw.Tests;

public sealed class LoopIntervalTests
{
    [Theory]
    [InlineData("60s", 60)]
    [InlineData("90s", 90)]
    [InlineData("45m", 2700)]
    [InlineData("90m", 5400)]
    [InlineData("25h", 90000)]
    [InlineData("2H", 7200)]
    public async Task ScheduleIntervalLoopAsync_WhenDurationCrossesClockBoundary_ShouldPreserveExactCadence(string interval, int seconds)
    {
        var clock = new Clock();
        var origin = clock.Now;
        var scheduler = new ClawLoopScheduler(NullLogger<ClawLoopScheduler>.Instance, clock);
        await scheduler.ScheduleIntervalLoopAsync("session", interval, "check", TestContext.Current.CancellationToken);
        for (var run = 1; run <= 4; run++)
        {
            clock.Now = origin.AddSeconds((double)seconds * run).AddTicks(-1);
            Assert.Empty(scheduler.GetDueEntries());
            clock.Now = clock.Now.AddTicks(1);
            Assert.Single(scheduler.GetDueEntries());
            Assert.Empty(scheduler.GetDueEntries());
        }
    }

    [Fact]
    public async Task GetDueEntries_WhenTickIsLate_ShouldSkipMissedRunsWithoutDriftOrBurst()
    {
        var clock = new Clock();
        var origin = clock.Now;
        var scheduler = new ClawLoopScheduler(NullLogger<ClawLoopScheduler>.Instance, clock);
        await scheduler.ScheduleIntervalLoopAsync("session", "90s", "check", TestContext.Current.CancellationToken);
        clock.Now = origin.AddSeconds(271);
        Assert.Single(scheduler.GetDueEntries());
        Assert.Empty(scheduler.GetDueEntries());
        clock.Now = origin.AddSeconds(359);
        Assert.Empty(scheduler.GetDueEntries());
        clock.Now = origin.AddSeconds(360);
        Assert.Single(scheduler.GetDueEntries());
    }

    [Fact]
    public async Task DispatchDueAsync_WhenIntervalDue_ShouldDispatchAndHonorCancellation()
    {
        var clock = new Clock();
        var scheduler = new ClawLoopScheduler(NullLogger<ClawLoopScheduler>.Instance, clock);
        var dispatcher = Substitute.For<IAgentLoopDispatcher>();
        var job = new AgentLoopJob(scheduler, dispatcher, NullLogger<AgentLoopJob>.Instance);
        await scheduler.ScheduleIntervalLoopAsync("session", "60s", "check", TestContext.Current.CancellationToken);
        await job.DispatchDueAsync(TestContext.Current.CancellationToken);
        clock.Now = clock.Now.AddSeconds(60);
        await job.DispatchDueAsync(TestContext.Current.CancellationToken);
        await dispatcher.Received(1).DispatchAsync("session", "check", Arg.Any<CancellationToken>());
        await scheduler.CancelLoopAsync("session", TestContext.Current.CancellationToken);
        clock.Now = clock.Now.AddSeconds(60);
        await job.DispatchDueAsync(TestContext.Current.CancellationToken);
        await dispatcher.Received(1).DispatchAsync("session", "check", Arg.Any<CancellationToken>());
    }

    [Theory]
    [InlineData("0s")]
    [InlineData("59s")]
    [InlineData("-1m")]
    [InlineData("2147483648s")]
    [InlineData("8761h")]
    [InlineData("1x")]
    public void ParseInterval_WhenInvalidOrTooLarge_ShouldReject(string interval)
        => Assert.Throws<ArgumentException>(() => ClawLoopScheduler.ParseInterval(interval));

    [Theory]
    [InlineData("90s")]
    [InlineData("45m")]
    [InlineData("25h")]
    public void IntervalToCron_WhenConversionWouldChangeDuration_ShouldReject(string interval)
        => Assert.Throws<ArgumentException>(() => ClawLoopScheduler.IntervalToCron(interval));

    private sealed class Clock : TimeProvider
    {
        public DateTimeOffset Now { get; set; } = new(2026, 11, 13, 23, 59, 17, TimeSpan.Zero);
        public override DateTimeOffset GetUtcNow() => Now;
    }
}

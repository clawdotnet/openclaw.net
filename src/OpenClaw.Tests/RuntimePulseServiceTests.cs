using OpenClaw.Core.Models;
using OpenClaw.Gateway;
using Xunit;

namespace OpenClaw.Tests;

public sealed class RuntimePulseServiceTests
{
    [Theory]
    [InlineData("0m", 0)]
    [InlineData("30m", 30)]
    [InlineData("2h", 120)]
    public void TryParseEvery_SupportsPulseIntervals(string value, int expectedMinutes)
    {
        Assert.True(RuntimePulseService.TryParseEvery(value, out var interval));
        Assert.Equal(TimeSpan.FromMinutes(expectedMinutes), interval);
    }

    [Fact]
    public void TryParseEvery_RejectsUnknownSuffix()
    {
        Assert.False(RuntimePulseService.TryParseEvery("30x", out _));
    }

    [Fact]
    public void IsAck_SuppressesOnlyBoundedEdgeAck()
    {
        Assert.True(RuntimePulseService.IsAck("HEARTBEAT_OK", "HEARTBEAT_OK", 300));
        Assert.True(RuntimePulseService.IsAck("HEARTBEAT_OK\nNo changes.", "HEARTBEAT_OK", 300));
        Assert.True(RuntimePulseService.IsAck("No changes.\nHEARTBEAT_OK", "HEARTBEAT_OK", 300));
        Assert.False(RuntimePulseService.IsAck("No HEARTBEAT_OK changes in the middle.", "HEARTBEAT_OK", 300));
        Assert.False(RuntimePulseService.IsAck("HEARTBEAT_OKAY", "HEARTBEAT_OK", 300));
        Assert.False(RuntimePulseService.IsAck("ALERT_HEARTBEAT_OK", "HEARTBEAT_OK", 300));
    }

    [Fact]
    public void IsEffectivelyEmpty_TreatsHeadersOnlyAsEmpty()
    {
        Assert.True(RuntimePulseService.IsEffectivelyEmpty("# Heartbeat\n\n## Notes\n"));
        Assert.False(RuntimePulseService.IsEffectivelyEmpty("# Heartbeat\n\n- Check pending proposals.\n"));
    }

    [Fact]
    public void ParseTasks_ConservativelyReadsSimpleTasksBlock()
    {
        var tasks = RuntimePulseService.ParseTasks(
            """
            # Heartbeat

            tasks:
            - name: inbox-triage
              interval: 30m
              prompt: "Check urgent unread messages."
            - name: runtime-health
              interval: 2h
              prompt: "Check recent runtime warnings."

            # Additional instructions
            - Keep alerts short.
            """);

        Assert.Collection(
            tasks,
            first =>
            {
                Assert.Equal("inbox-triage", first.Name);
                Assert.Equal("30m", first.Interval);
                Assert.Equal("Check urgent unread messages.", first.Prompt);
            },
            second =>
            {
                Assert.Equal("runtime-health", second.Name);
                Assert.Equal("2h", second.Interval);
                Assert.Equal("Check recent runtime warnings.", second.Prompt);
            });
    }

    [Fact]
    public void TasksBlock_EndsAtFreeformContentWithoutHeading()
    {
        var markdown =
            """
            # Heartbeat

            tasks:
            - name: proposal-review
              interval: 2h
              prompt: "Check pending proposals."
            - Keep alerts short.
            If nothing needs attention, reply HEARTBEAT_OK.
            """;

        var tasks = RuntimePulseService.ParseTasks(markdown);
        var freeform = RuntimePulseService.RemoveTasksBlock(markdown);

        Assert.Collection(
            tasks,
            task =>
            {
                Assert.Equal("proposal-review", task.Name);
                Assert.Equal("2h", task.Interval);
                Assert.Equal("Check pending proposals.", task.Prompt);
            });
        Assert.Contains("- Keep alerts short.", freeform, StringComparison.Ordinal);
        Assert.Contains("If nothing needs attention", freeform, StringComparison.Ordinal);
        Assert.DoesNotContain("tasks:", freeform, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("Check pending proposals.", freeform, StringComparison.Ordinal);
    }

    [Fact]
    public void SelectDueTasks_UsesPulseStateWithoutCreatingAutomations()
    {
        var now = DateTimeOffset.Parse("2026-05-08T12:00:00Z");
        var state = new PulseState();
        state.TaskLastRunUtc["runtime-health"] = now.AddMinutes(-30);
        var tasks = new[]
        {
            new PulseTaskDefinition { Name = "proposal-review", Interval = "2h", Prompt = "Check proposals." },
            new PulseTaskDefinition { Name = "runtime-health", Interval = "1h", Prompt = "Check warnings." }
        };

        var due = RuntimePulseService.SelectDueTasks(tasks, state, now);

        Assert.Collection(due, task => Assert.Equal("proposal-review", task.Name));
    }

    [Fact]
    public void TruncatePulsePrompt_ReservesBudgetForFractalContext()
    {
        var config = RuntimePulseService.Normalize(new PulseConfig { Prompt = "Pulse prompt." });
        var heartbeat = new string('h', 12_000);
        var fractalContext = "<fractal_memory_context>\nSource: test\n" + new string('f', 3_000) + "\n</fractal_memory_context>";
        var dueTasks = Enumerable.Range(0, 80)
            .Select(i => new PulseTaskDefinition
            {
                Name = i == 0 ? "runtime-health" : $"overflow-{i}",
                Prompt = i == 0 ? "Check warnings." : new string('x', 100)
            })
            .ToArray();

        var prompt = RuntimePulseService.BuildPrompt(config, heartbeat, dueTasks, manualText: null, fractalContext);
        var truncated = RuntimePulseService.TruncatePulsePrompt(prompt, config, fractalContext);

        Assert.Contains("Due heartbeat tasks:", truncated, StringComparison.Ordinal);
        Assert.Contains("runtime-health: Check warnings.", truncated, StringComparison.Ordinal);
        Assert.Contains("<fractal_memory_context>", truncated, StringComparison.Ordinal);
        Assert.Contains("Source: test", truncated, StringComparison.Ordinal);
        Assert.Contains("</fractal_memory_context>", truncated, StringComparison.Ordinal);
        Assert.True(prompt.Length > truncated.Length);
    }
}

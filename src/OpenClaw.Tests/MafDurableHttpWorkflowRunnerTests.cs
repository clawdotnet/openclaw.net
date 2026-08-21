using Xunit;

namespace OpenClaw.Tests;

public class MafDurableHttpWorkflowRunnerTests
{
    // The end-to-end path (step events -> runtime events with stepName metadata)
    // requires standing up an HTTP backend for the runner; that is covered by the
    // integration test in Task 11. The helper that classifies step event types is
    // asserted directly below.
    [Fact(Skip = "covered by integration test in Task 11")]
    public async Task Step_Completed_Events_Become_Run_Completed_Runtime_Events_With_StepName()
    {
        await Task.CompletedTask;
        Assert.True(true);
    }

    [Fact]
    public void ResolveStepAction_Classifies_Completed_And_Failed_Suffixes()
    {
        // Use reflection to invoke the private static helper for test coverage.
        var asm = typeof(OpenClaw.Gateway.Workflows.MafDurableHttpWorkflowRunner).Assembly;
        var method = asm.GetType("OpenClaw.Gateway.Workflows.MafDurableHttpWorkflowRunner")!
            .GetMethod("ResolveStepAction", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Static)!;

        Assert.Equal("run_completed", method.Invoke(null, new object[] { "SecurityReviewerCompleted" }));
        Assert.Equal("run_failed", method.Invoke(null, new object[] { "PlanExecutorFailed" }));
        Assert.Equal("run_failed", method.Invoke(null, new object[] { "AggregateReviewsFaulted" }));
        Assert.Null(method.Invoke(null, new object[] { "status" }));
        Assert.Null(method.Invoke(null, new object[] { "UnknownEvent" }));
    }

    [Fact]
    public void StripStepSuffix_Removes_Completed_Failed_Faulted()
    {
        var asm = typeof(OpenClaw.Gateway.Workflows.MafDurableHttpWorkflowRunner).Assembly;
        var method = asm.GetType("OpenClaw.Gateway.Workflows.MafDurableHttpWorkflowRunner")!
            .GetMethod("StripStepSuffix", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Static)!;

        Assert.Equal("SecurityReviewer", method.Invoke(null, new object[] { "SecurityReviewerCompleted" }));
        Assert.Equal("PlanExecutor", method.Invoke(null, new object[] { "PlanExecutorFailed" }));
        Assert.Equal("AggregateReviews", method.Invoke(null, new object[] { "AggregateReviewsFaulted" }));
        Assert.Equal("status", method.Invoke(null, new object[] { "status" })); // no suffix to strip
    }
}

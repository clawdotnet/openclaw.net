using OpenClaw.Core.Models;
using OpenClaw.StrategosWorkflowHost.Adapters;
using Xunit;

namespace OpenClaw.StrategosWorkflowHost.Tests;

public class PhaseStatusMapTests
{
    [Theory]
    [InlineData("NotStarted", AgentWorkflowStatuses.Queued)]
    [InlineData("AwaitingApproval", AgentWorkflowStatuses.WaitingForInput)]
    [InlineData("Completed", AgentWorkflowStatuses.Completed)]
    [InlineData("Failed", AgentWorkflowStatuses.Failed)]
    [InlineData("Compensated", AgentWorkflowStatuses.Failed)]
    [InlineData("Cancelled", AgentWorkflowStatuses.Cancelled)]
    [InlineData("ExecutingPlan", AgentWorkflowStatuses.Running)]
    [InlineData("ExecutingReview", AgentWorkflowStatuses.Running)]
    [InlineData("WhateverUnknown", AgentWorkflowStatuses.Running)]
    public void ToOpenClawStatus_MapsEachPhase(string phase, string expected)
        => Assert.Equal(expected, PhaseStatusMap.ToOpenClawStatus(phase));
}

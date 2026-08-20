using OpenClaw.Core.Models;
using OpenClaw.StrategosWorkflowHost.Adapters;
using OpenClaw.StrategosWorkflowHost.Workflows;
using Strategos.Agents.Abstractions;
using Xunit;

namespace OpenClaw.StrategosWorkflowHost.Tests;

public class DurableHttpAdapterEvidenceTests
{
    [Fact]
    public void BuildOutputPayload_Includes_Audit_When_ExecutionResult_Has_Marker()
    {
        var state = new ReviewState
        {
            Id = Guid.NewGuid(),
            WorkflowId = Guid.NewGuid(),
            Plan = "p",
            AggregateConfidence = 0.8,
            CurrentPhase = "Completed",
            ExecutionResult = "AuditTrace:{\"plan\":\"p\",\"reviews\":3,\"approved\":true}"
        };

        var payload = DurableHttpAdapter.BuildOutputPayloadForTest(state);

        Assert.True(payload.HasValue);
        Assert.True(payload.Value.TryGetProperty("audit", out var audit));
        Assert.Equal("p", audit.GetProperty("plan").GetString());
        Assert.Equal(3, audit.GetProperty("reviews").GetInt32());
        Assert.True(audit.GetProperty("approved").GetBoolean());
        // Existing keys stay.
        Assert.Equal("p", payload.Value.GetProperty("plan").GetString());
        Assert.Equal("Completed", payload.Value.GetProperty("phase").GetString());
    }

    [Fact]
    public void BuildOutputPayload_Omits_Audit_When_Marker_Missing()
    {
        var state = new ReviewState
        {
            Id = Guid.NewGuid(),
            WorkflowId = Guid.NewGuid(),
            Plan = "p",
            CurrentPhase = "Running",
            ExecutionResult = "Executed something; no audit yet"
        };

        var payload = DurableHttpAdapter.BuildOutputPayloadForTest(state);

        Assert.True(payload.HasValue);
        Assert.False(payload.Value.TryGetProperty("audit", out _));
    }

    [Fact]
    public void AppendAuditTraceEvent_Adds_Event_For_EmitAuditTraceCompleted()
    {
        var now = DateTimeOffset.UtcNow;
        var evt = new EmitAuditTraceCompleted(
            WorkflowId: Guid.NewGuid(),
            StepExecutionId: Guid.NewGuid(),
            UpdatedState: new ReviewState { Id = Guid.NewGuid(), WorkflowId = Guid.NewGuid() },
            Confidence: null,
            Timestamp: now);

        var mapped = DurableHttpAdapter.MapEventForTest(evt);

        Assert.Equal("audit_trace_emitted", mapped.Type);
        Assert.Equal(now, mapped.TimestampUtc);
        Assert.Equal(AgentWorkflowStatuses.Completed, mapped.Status);
    }

    [Fact]
    public void AppendAuditTraceEvent_Only_Fires_For_EmitAuditTraceCompleted()
    {
        // Sanity: a non-audit step does NOT produce audit_trace_emitted.
        var evt = new SecurityReviewerCompleted(
            WorkflowId: Guid.NewGuid(),
            StepExecutionId: Guid.NewGuid(),
            UpdatedState: new ReviewState { Id = Guid.NewGuid(), WorkflowId = Guid.NewGuid() },
            Confidence: 0.8,
            Timestamp: DateTimeOffset.UtcNow);

        var mapped = DurableHttpAdapter.MapEventForTest(evt);

        Assert.NotEqual("audit_trace_emitted", mapped.Type);
    }
}

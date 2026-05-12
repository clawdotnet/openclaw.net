using System.Text.Json;

namespace OpenClaw.Core.Models;

public static class AgentWorkflowStatuses
{
    public const string Queued = "queued";
    public const string Running = "running";
    public const string WaitingForInput = "waiting_for_input";
    public const string Completed = "completed";
    public const string Failed = "failed";
    public const string Cancelled = "cancelled";
}

public static class AgentWorkflowBackendKinds
{
    public const string MafDurableHttp = "maf-durable-http";
}

public sealed class WorkflowsConfig
{
    public bool Enabled { get; set; }
    public Dictionary<string, WorkflowBackendConfig> Backends { get; set; } = new(StringComparer.OrdinalIgnoreCase);
}

public sealed class WorkflowBackendConfig
{
    public bool Enabled { get; set; } = true;
    public string Kind { get; set; } = AgentWorkflowBackendKinds.MafDurableHttp;
    public string? DisplayName { get; set; }
    public string? BaseUrl { get; set; }
    public string? WorkflowName { get; set; }
    public string? ApiTokenSecret { get; set; }
    public int PollIntervalSeconds { get; set; } = 2;
    public int TimeoutSeconds { get; set; } = 120;
}

public sealed class AgentWorkflowBackendSummary
{
    public required string Id { get; init; }
    public required string Kind { get; init; }
    public required string WorkflowName { get; init; }
    public string? DisplayName { get; init; }
    public bool Enabled { get; init; }
}

public sealed class AgentWorkflowRequest
{
    public string Input { get; init; } = "";
    public JsonElement? Payload { get; init; }
    public string? ChannelId { get; init; }
    public string? SenderId { get; init; }
    public string? SessionId { get; init; }
    public Dictionary<string, string>? Metadata { get; init; }
}

public sealed class AgentWorkflowResponse
{
    public required string PortId { get; init; }
    public JsonElement? Payload { get; init; }
    public bool? Approved { get; init; }
    public string? Comment { get; init; }
    public string? ActorId { get; init; }
    public Dictionary<string, string>? Metadata { get; init; }
}

public sealed class AgentWorkflowRunResult
{
    public required string WorkflowId { get; init; }
    public required string RunId { get; init; }
    public required string Status { get; init; }
    public string? BackendId { get; init; }
    public string? Output { get; init; }
    public JsonElement? OutputPayload { get; init; }
    public string? Error { get; init; }
    public IReadOnlyList<AgentWorkflowEvent> Events { get; init; } = [];
    public Dictionary<string, string>? Metadata { get; init; }
}

public sealed class AgentWorkflowRunSnapshot
{
    public required string WorkflowId { get; init; }
    public required string RunId { get; init; }
    public required string Status { get; init; }
    public string? BackendId { get; init; }
    public string? Output { get; init; }
    public JsonElement? OutputPayload { get; init; }
    public string? Error { get; init; }
    public IReadOnlyList<AgentWorkflowPendingInput> PendingInputs { get; init; } = [];
    public IReadOnlyList<AgentWorkflowEvent> Events { get; init; } = [];
    public Dictionary<string, string>? Metadata { get; init; }
}

public sealed class AgentWorkflowPendingInput
{
    public required string PortId { get; init; }
    public string? Summary { get; init; }
    public JsonElement? Payload { get; init; }
    public Dictionary<string, string>? Metadata { get; init; }
}

public sealed class AgentWorkflowEvent
{
    public required string Id { get; init; }
    public DateTimeOffset TimestampUtc { get; init; } = DateTimeOffset.UtcNow;
    public required string Type { get; init; }
    public string? WorkflowId { get; init; }
    public string? RunId { get; init; }
    public string? Status { get; init; }
    public string? PortId { get; init; }
    public string Summary { get; init; } = "";
    public JsonElement? Payload { get; init; }
    public Dictionary<string, string>? Metadata { get; init; }
}

public sealed class IntegrationWorkflowsResponse
{
    public IReadOnlyList<AgentWorkflowBackendSummary> Items { get; init; } = [];
}

using System.Collections.Concurrent;
using System.Text.Json;
using System.Text.Json.Nodes;
using OpenClaw.Core.Models;

var builder = WebApplication.CreateSlimBuilder(args);
builder.Services.ConfigureHttpJsonOptions(options =>
    options.SerializerOptions.TypeInfoResolverChain.Add(CoreJsonContext.Default));

var runs = new ConcurrentDictionary<string, ReviewRun>(StringComparer.Ordinal);
var app = builder.Build();

app.MapPost("/api/workflows/{workflow}/run", async (string workflow, HttpContext ctx) =>
{
    AgentWorkflowRequest? request;
    try
    {
        request = await JsonSerializer.DeserializeAsync(
            ctx.Request.Body,
            CoreJsonContext.Default.AgentWorkflowRequest,
            ctx.RequestAborted);
    }
    catch (JsonException)
    {
        return Results.BadRequest(new OperationStatusResponse { Success = false, Error = "Invalid JSON request body." });
    }

    if (request is null)
        return Results.BadRequest(new OperationStatusResponse { Success = false, Error = "request body is required." });

    var workflowId = string.IsNullOrWhiteSpace(workflow) ? "DurableAgentReview" : workflow.Trim();
    var backendId = ResolveBackendId(request);
    var runId = $"run_{Guid.NewGuid():N}";
    var reviewPayload = BuildReviewPayload(request.Input);
    var events = new List<AgentWorkflowEvent>
    {
        ReviewEvent(workflowId, runId, "plan", AgentWorkflowStatuses.Running, "Plan executor created a review plan."),
        ReviewEvent(workflowId, runId, "security_review", AgentWorkflowStatuses.Running, "Security reviewer completed."),
        ReviewEvent(workflowId, runId, "architecture_review", AgentWorkflowStatuses.Running, "Architecture reviewer completed."),
        ReviewEvent(workflowId, runId, "cost_review", AgentWorkflowStatuses.Running, "Cost reviewer completed."),
        ReviewEvent(workflowId, runId, "waiting_for_input", AgentWorkflowStatuses.WaitingForInput, "Human approval is required before executing the approved action.", "human-approval")
    };

    var run = new ReviewRun(
        BackendId: backendId,
        WorkflowId: workflowId,
        RunId: runId,
        Input: request.Input,
        ChannelId: request.ChannelId,
        SenderId: request.SenderId,
        SessionId: request.SessionId,
        Status: AgentWorkflowStatuses.WaitingForInput,
        Events: events,
        PendingInputs:
        [
            new AgentWorkflowPendingInput
            {
                PortId = "human-approval",
                Summary = "Approve or reject the aggregated agent review.",
                Payload = reviewPayload,
                Metadata = new Dictionary<string, string>
                {
                    ["requestPort"] = "HumanApproval",
                    ["sample"] = "DurableAgentReview"
                }
            }
        ]);

    runs[runId] = run;

    return Results.Json(
        new AgentWorkflowRunResult
        {
            BackendId = run.BackendId,
            WorkflowId = workflowId,
            RunId = runId,
            Status = run.Status,
            Events = run.Events,
            Metadata = ReviewPayloads.BuildMetadata(run)
        },
        CoreJsonContext.Default.AgentWorkflowRunResult,
        statusCode: StatusCodes.Status202Accepted);
});

app.MapGet("/api/workflows/{workflow}/status/{runId}", (string workflow, string runId) =>
{
    return runs.TryGetValue(runId, out var run)
        ? Results.Json(run.ToSnapshot(), CoreJsonContext.Default.AgentWorkflowRunSnapshot)
        : Results.NotFound(new OperationStatusResponse { Success = false, Error = "Workflow run not found." });
});

app.MapPost("/api/workflows/{workflow}/respond/{runId}", async (string workflow, string runId, HttpContext ctx) =>
{
    if (!runs.TryGetValue(runId, out var run))
        return Results.NotFound(new OperationStatusResponse { Success = false, Error = "Workflow run not found." });

    AgentWorkflowResponse? response;
    try
    {
        response = await JsonSerializer.DeserializeAsync(
            ctx.Request.Body,
            CoreJsonContext.Default.AgentWorkflowResponse,
            ctx.RequestAborted);
    }
    catch (JsonException)
    {
        return Results.BadRequest(new OperationStatusResponse { Success = false, Error = "Invalid JSON request body." });
    }

    if (response is null || !string.Equals(response.PortId, "human-approval", StringComparison.OrdinalIgnoreCase))
        return Results.BadRequest(new OperationStatusResponse { Success = false, Error = "portId must be 'human-approval'." });

    var approved = response.Approved == true;
    lock (run.Sync)
    {
        run.PendingInputs = [];
        run.Approved = approved;
        run.Approver = string.IsNullOrWhiteSpace(response.ActorId) ? "sample-operator" : response.ActorId;
        run.ApprovalComment = response.Comment;
        run.Status = approved ? AgentWorkflowStatuses.Completed : AgentWorkflowStatuses.Cancelled;
        run.Events.Add(ReviewEvent(run.WorkflowId, run.RunId, "approval_response", run.Status, approved
            ? "Human approver accepted the review plan."
            : "Human approver rejected the review plan.", "human-approval"));

        if (approved)
        {
            run.Output = "Approved action executed. Audit trace is available in outputPayload.";
            run.OutputPayload = BuildAuditPayload(run);
            run.Events.Add(ReviewEvent(run.WorkflowId, run.RunId, "execute_approved_action", AgentWorkflowStatuses.Completed, "Approved action executed."));
        }
        else
        {
            run.Output = "Workflow cancelled by human approval response.";
            run.OutputPayload = BuildAuditPayload(run);
        }
    }

    return Results.Json(run.ToSnapshot(), CoreJsonContext.Default.AgentWorkflowRunSnapshot);
});

app.MapGet("/", () => Results.Text("""
OpenClaw DurableAgentReview sample

POST /api/workflows/DurableAgentReview/run
GET  /api/workflows/DurableAgentReview/status/{runId}
POST /api/workflows/DurableAgentReview/respond/{runId}
"""));

app.Run();

static AgentWorkflowEvent ReviewEvent(
    string workflowId,
    string runId,
    string type,
    string status,
    string summary,
    string? portId = null)
    => new()
    {
        Id = $"evt_{Guid.NewGuid():N}"[..20],
        WorkflowId = workflowId,
        RunId = runId,
        Type = type,
        Status = status,
        PortId = portId,
        Summary = summary,
        Metadata = type.EndsWith("review", StringComparison.Ordinal)
            ? new Dictionary<string, string> { ["agentRole"] = type.Replace('_', '-') }
            : null
    };

static JsonElement BuildReviewPayload(string input)
{
    var payload = new JsonObject
    {
        ["input"] = input,
        ["reviewers"] = new JsonArray
        {
            new JsonObject
            {
                ["role"] = "security",
                ["verdict"] = "review-required",
                ["summary"] = "No critical risk detected in sample review."
            },
            new JsonObject
            {
                ["role"] = "architecture",
                ["verdict"] = "review-required",
                ["summary"] = "Plan is feasible with explicit approval boundary."
            },
            new JsonObject
            {
                ["role"] = "cost",
                ["verdict"] = "review-required",
                ["summary"] = "Execution cost is acceptable for the sample action."
            }
        }
    };
    return ToJsonElement(payload);
}

static JsonElement BuildAuditPayload(ReviewRun run)
{
    var payload = new JsonObject
    {
        ["workflowId"] = run.WorkflowId,
        ["runId"] = run.RunId,
        ["input"] = run.Input,
        ["approved"] = run.Approved,
        ["approver"] = run.Approver,
        ["approvalComment"] = run.ApprovalComment,
        ["eventCount"] = run.Events.Count,
        ["completedAtUtc"] = DateTimeOffset.UtcNow
    };
    return ToJsonElement(payload);
}

static JsonElement ToJsonElement(JsonNode node)
{
    using var document = JsonDocument.Parse(node.ToJsonString());
    return document.RootElement.Clone();
}

static string ResolveBackendId(AgentWorkflowRequest request)
    => request.Metadata is not null &&
       request.Metadata.TryGetValue("backendId", out var backendId) &&
       !string.IsNullOrWhiteSpace(backendId)
        ? backendId.Trim()
        : "durable-review";

file sealed class ReviewRun(
    string BackendId,
    string WorkflowId,
    string RunId,
    string Input,
    string? ChannelId,
    string? SenderId,
    string? SessionId,
    string Status,
    List<AgentWorkflowEvent> Events,
    IReadOnlyList<AgentWorkflowPendingInput> PendingInputs)
{
    public object Sync { get; } = new();
    public string BackendId { get; } = BackendId;
    public string WorkflowId { get; } = WorkflowId;
    public string RunId { get; } = RunId;
    public string Input { get; } = Input;
    public string? ChannelId { get; } = ChannelId;
    public string? SenderId { get; } = SenderId;
    public string? SessionId { get; } = SessionId;
    public string Status { get; set; } = Status;
    public List<AgentWorkflowEvent> Events { get; } = Events;
    public IReadOnlyList<AgentWorkflowPendingInput> PendingInputs { get; set; } = PendingInputs;
    public bool? Approved { get; set; }
    public string? Approver { get; set; }
    public string? ApprovalComment { get; set; }
    public string? Output { get; set; }
    public JsonElement? OutputPayload { get; set; }

    public AgentWorkflowRunSnapshot ToSnapshot()
    {
        lock (Sync)
        {
            return new AgentWorkflowRunSnapshot
            {
                BackendId = BackendId,
                WorkflowId = WorkflowId,
                RunId = RunId,
                Status = Status,
                Output = Output,
                OutputPayload = OutputPayload,
                PendingInputs = PendingInputs,
                Events = Events.ToArray(),
                Metadata = ReviewPayloads.BuildMetadata(this)
            };
        }
    }
}

file static class ReviewPayloads
{
    public static Dictionary<string, string> BuildMetadata(ReviewRun run)
    {
        var metadata = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["sample"] = "DurableAgentReview",
            ["workflowPattern"] = "fan-out-fan-in-human-approval"
        };

        if (!string.IsNullOrWhiteSpace(run.ChannelId))
            metadata["channelId"] = run.ChannelId;
        if (!string.IsNullOrWhiteSpace(run.SenderId))
            metadata["senderId"] = run.SenderId;
        if (!string.IsNullOrWhiteSpace(run.SessionId))
            metadata["sessionId"] = run.SessionId;

        return metadata;
    }
}

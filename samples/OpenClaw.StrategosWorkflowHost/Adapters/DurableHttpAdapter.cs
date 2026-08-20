using System.Text.Json;
using Marten;
using OpenClaw.Core.Models;
using OpenClaw.StrategosWorkflowHost.Workflows;
using Strategos.Agents.Abstractions;
using Strategos.Models;
using Wolverine;

namespace OpenClaw.StrategosWorkflowHost.Adapters;

// Adapts Strategos' event-sourced saga to OpenClaw's maf-durable-http contract.
//
// Pause detection (hybrid strategy, committed in the plan):
//   1. state.CurrentPhase stamped by Apply({Step}Completed) tracks step transitions.
//   2. AwaitApproval<Operator> does NOT stamp CurrentPhase; it emits a
//      RequestOperatorApprovalEvent. The adapter scans the event stream tail for the
//      request event and reports waiting_for_input until a decision is observed.
//
// Run/Respond use Wolverine's IMessageBus to fire the generator-produced start / resume
// commands. The actual saga work is durably stored in PostgreSQL via Marten.
public sealed class DurableHttpAdapter
{
    public const string BackendId = "durable-agent-review";
    public const string WorkflowName = "durable-agent-review";

    private readonly IDocumentStore _store;
    private readonly IMessageBus _bus;
    private readonly ILogger<DurableHttpAdapter> _logger;

    public DurableHttpAdapter(
        IDocumentStore store,
        IMessageBus bus,
        ILogger<DurableHttpAdapter> logger)
    {
        _store = store;
        _bus = bus;
        _logger = logger;
    }

    public AgentWorkflowBackendSummary GetSummary() => new()
    {
        Id = BackendId,
        Kind = AgentWorkflowBackendKinds.MafDurableHttp,
        WorkflowName = WorkflowName,
        DisplayName = "Strategos durable agent review",
        Enabled = true,
    };

    // POST /api/workflows/{workflowId}/run — kick off a new saga instance.
    public async Task<AgentWorkflowRunResult> StartRunAsync(
        AgentWorkflowRequest request,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request, nameof(request));

        var workflowId = Guid.NewGuid();
        var initial = new ReviewState
        {
            Id = workflowId,
            WorkflowId = workflowId,
            UserRequest = request.Input,
            CurrentPhase = "NotStarted",
        };

        var cmd = new StartDurableAgentReviewCommand(workflowId, initial);
        await _bus.SendAsync(cmd);

        _logger.LogInformation(
            "Started durable-agent-review workflow {WorkflowId} for input {Input}.",
            workflowId,
            request.Input);

        return new AgentWorkflowRunResult
        {
            WorkflowId = WorkflowName,
            BackendId = BackendId,
            RunId = workflowId.ToString(),
            Status = AgentWorkflowStatuses.Running,
            Events = [new AgentWorkflowEvent
            {
                Id = $"evt_{Guid.NewGuid():N}"[..20],
                WorkflowId = WorkflowName,
                RunId = workflowId.ToString(),
                Type = "run_started",
                Status = AgentWorkflowStatuses.Running,
                Summary = "Workflow started."
            }],
            Metadata = new Dictionary<string, string>
            {
                ["workflowId"] = workflowId.ToString(),
                ["backendId"] = BackendId,
            }
        };
    }

    // GET /api/workflows/{workflowId}/status/{runId} — read current state + event stream tail.
    public async Task<AgentWorkflowRunSnapshot> GetStatusAsync(
        string runId,
        CancellationToken cancellationToken)
    {
        if (!Guid.TryParse(runId, out var workflowId))
            throw new InvalidOperationException($"Invalid runId '{runId}'.");

        await using var session = _store.QuerySession();
        var state = await session.LoadAsync<ReviewState>(workflowId, cancellationToken);

        var events = await LoadEventsAsync(session, workflowId, cancellationToken);

        var status = ComputeStatus(state, events);
        var pending = status == AgentWorkflowStatuses.WaitingForInput && state is not null
            ? PendingInputBuilder.Build(state, "operator-approval")
            : (IReadOnlyList<AgentWorkflowPendingInput>)[];

        return new AgentWorkflowRunSnapshot
        {
            WorkflowId = WorkflowName,
            BackendId = BackendId,
            RunId = workflowId.ToString(),
            Status = status,
            Output = state?.ExecutionResult,
            PendingInputs = pending,
            Events = events.Select(MapEvent).ToArray(),
            Metadata = new Dictionary<string, string>
            {
                ["workflowId"] = workflowId.ToString(),
                ["backendId"] = BackendId,
                ["currentPhase"] = state?.CurrentPhase ?? "NotStarted",
            }
        };
    }

    // POST /api/workflows/{workflowId}/respond/{runId} — resume from AwaitApproval<Operator>.
    public async Task<AgentWorkflowRunSnapshot> RespondAsync(
        string runId,
        AgentWorkflowResponse response,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(response, nameof(response));
        if (!Guid.TryParse(runId, out var workflowId))
            throw new InvalidOperationException($"Invalid runId '{runId}'.");

        var decision = response.Approved switch
        {
            true => ApprovalDecision.Approved,
            false => ApprovalDecision.Rejected,
            null => ApprovalDecision.Deferred,
        };

        var cmd = new ResumeOperatorApprovalCommand(
            WorkflowId: workflowId,
            Decision: decision,
            SelectedOptionId: null,
            Instructions: response.Comment);
        await _bus.SendAsync(cmd);

        _logger.LogInformation(
            "Resumed workflow {WorkflowId} via port {PortId} with decision {Decision}.",
            workflowId,
            response.PortId,
            decision);

        return await GetStatusAsync(runId, cancellationToken);
    }

    private string ComputeStatus(ReviewState? state, IReadOnlyList<IProgressEvent> events)
    {
        if (state is null)
            return AgentWorkflowStatuses.Queued;

        // AwaitApproval<Operator> pause: latest event is a Request*ApprovalEvent with no
        // follow-up decision event yet. Once the resume command lands, the saga emits the
        // resume-acknowledgement (the saga handles internally) and the next event in the
        // stream is a {Step}Completed for ExecuteApprovedAction.
        var hasOpenApproval = events.Any(IsApprovalRequestEvent)
            && !events.SkipWhile(IsNotResume).Any(IsApprovalRequestEvent);
        // Simpler: if the latest event is a Request*ApprovalEvent, the saga is parked.
        if (events.Count > 0 && IsApprovalRequestEvent(events[^1]))
            return AgentWorkflowStatuses.WaitingForInput;

        return PhaseStatusMap.ToOpenClawStatus(state.CurrentPhase);
    }

    private static bool IsApprovalRequestEvent(IProgressEvent evt)
        => evt.GetType().Name.StartsWith("Request", StringComparison.Ordinal)
            && evt.GetType().Name.EndsWith("ApprovalEvent", StringComparison.Ordinal);

    private static bool IsNotResume(IProgressEvent evt)
        => !evt.GetType().Name.StartsWith("Resume", StringComparison.Ordinal);

    // Pulls the event stream from Marten and returns them as IProgressEvent list.
    private static async Task<IReadOnlyList<IProgressEvent>> LoadEventsAsync(
        IQuerySession session,
        Guid workflowId,
        CancellationToken cancellationToken)
    {
        try
        {
            var stream = await session.Events.FetchStreamAsync(workflowId);
            return stream.OfType<IProgressEvent>().ToArray();
        }
        catch
        {
            return [];
        }
    }

    private static AgentWorkflowEvent MapEvent(IProgressEvent evt)
    {
        var typeName = evt.GetType().Name;
        return new AgentWorkflowEvent
        {
            Id = $"evt_{Guid.NewGuid():N}"[..20],
            TimestampUtc = evt.Timestamp,
            Type = ToEventType(typeName),
            WorkflowId = WorkflowName,
            Status = PhaseStatusMap.ToOpenClawStatus(evt.GetType().Name switch
            {
                var n when n.EndsWith("ApprovalEvent", StringComparison.Ordinal) => "AwaitingApproval",
                var n when n.EndsWith("Completed", StringComparison.Ordinal) => "ExecutingReview",
                _ => "Running"
            }),
            Summary = $"{typeName} @ {evt.Timestamp:O}",
            Metadata = new Dictionary<string, string>
            {
                ["eventType"] = typeName,
            }
        };
    }

    private static string ToEventType(string typeName) => typeName switch
    {
        "DurableAgentReviewStarted" => "workflow_started",
        var n when n.EndsWith("ApprovalEvent", StringComparison.Ordinal) => "awaiting_approval",
        var n when n.EndsWith("Completed", StringComparison.Ordinal) => "step_completed",
        _ => "progress"
    };
}
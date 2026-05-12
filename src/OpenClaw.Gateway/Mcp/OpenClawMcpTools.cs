using System.ComponentModel;
using System.Text.Json;
using ModelContextProtocol.Server;
using OpenClaw.Core.Models;
using OpenClaw.Gateway.Composition;

namespace OpenClaw.Gateway.Mcp;

/// <summary>
/// MCP tool implementations backed by <see cref="IntegrationApiFacade"/>.
/// Each method is exposed as an MCP tool via the official C# SDK.
/// Tool names preserve the existing "openclaw.*" convention for client compatibility.
/// </summary>
[McpServerToolType]
internal sealed class OpenClawMcpTools
{
    private readonly IntegrationApiFacade _facade;

    public OpenClawMcpTools(IntegrationApiFacade facade) => _facade = facade;

    [McpServerTool(Name = "openclaw.get_dashboard", ReadOnly = true),
     Description("Get the aggregated operator dashboard snapshot.")]
    public async Task<string> GetDashboard(CancellationToken ct)
        => JsonSerializer.Serialize(
            await _facade.GetDashboardAsync(ct),
            CoreJsonContext.Default.IntegrationDashboardResponse);

    [McpServerTool(Name = "openclaw.get_status", ReadOnly = true),
     Description("Get the current OpenClaw gateway runtime status.")]
    public string GetStatus()
        => JsonSerializer.Serialize(
            _facade.BuildStatusResponse(),
            CoreJsonContext.Default.IntegrationStatusResponse);

    [McpServerTool(Name = "openclaw.list_approvals", ReadOnly = true),
     Description("List pending tool approvals with optional channel or sender filters.")]
    public string ListApprovals(
        [Description("Optional channel ID filter.")] string? channelId = null,
        [Description("Optional sender ID filter.")] string? senderId = null)
        => JsonSerializer.Serialize(
            _facade.GetApprovals(channelId, senderId),
            CoreJsonContext.Default.IntegrationApprovalsResponse);

    [McpServerTool(Name = "openclaw.get_approval_history", ReadOnly = true),
     Description("Get recent approval history entries.")]
    public string GetApprovalHistory(
        [Description("Maximum number of entries to return.")] int? limit = null,
        [Description("Optional channel ID filter.")] string? channelId = null,
        [Description("Optional sender ID filter.")] string? senderId = null,
        [Description("Optional tool name filter.")] string? toolName = null)
        => JsonSerializer.Serialize(
            _facade.GetApprovalHistory(new ApprovalHistoryQuery
            {
                Limit = limit ?? 50,
                ChannelId = channelId,
                SenderId = senderId,
                ToolName = toolName
            }),
            CoreJsonContext.Default.IntegrationApprovalHistoryResponse);

    [McpServerTool(Name = "openclaw.get_providers", ReadOnly = true),
     Description("Get provider routing, usage, policies, and recent turns.")]
    public string GetProviders(
        [Description("Maximum number of recent turns to include.")] int? recentTurnsLimit = null)
        => JsonSerializer.Serialize(
            _facade.GetProviders(recentTurnsLimit ?? 20),
            CoreJsonContext.Default.IntegrationProvidersResponse);

    [McpServerTool(Name = "openclaw.get_plugins", ReadOnly = true),
     Description("Get the current plugin health listing.")]
    public string GetPlugins()
        => JsonSerializer.Serialize(
            _facade.GetPlugins(),
            CoreJsonContext.Default.IntegrationPluginsResponse);

    [McpServerTool(Name = "openclaw.query_operator_audit", ReadOnly = true),
     Description("Query recent operator audit entries.")]
    public string QueryOperatorAudit(
        [Description("Maximum number of entries to return.")] int? limit = null,
        [Description("Optional actor ID filter.")] string? actorId = null,
        [Description("Optional action type filter.")] string? actionType = null,
        [Description("Optional target ID filter.")] string? targetId = null)
        => JsonSerializer.Serialize(
            _facade.GetOperatorAudit(new OperatorAuditQuery
            {
                Limit = limit ?? 50,
                ActorId = actorId,
                ActionType = actionType,
                TargetId = targetId
            }),
            CoreJsonContext.Default.IntegrationOperatorAuditResponse);

    [McpServerTool(Name = "openclaw.list_sessions", ReadOnly = true),
     Description("List OpenClaw sessions with optional filters.")]
    public async Task<string> ListSessions(
        [Description("Page number (1-based).")] int? page = null,
        [Description("Page size.")] int? pageSize = null,
        [Description("Full-text search term.")] string? search = null,
        [Description("Optional channel ID filter.")] string? channelId = null,
        [Description("Optional sender ID filter.")] string? senderId = null,
        [Description("Optional session state filter.")] string? state = null,
        [Description("Optional tag filter.")] string? tag = null,
        [Description("Optional UTC start time (ISO 8601).")] DateTimeOffset? fromUtc = null,
        [Description("Optional UTC end time (ISO 8601).")] DateTimeOffset? toUtc = null,
        [Description("Filter by starred status.")] bool? starred = null,
        CancellationToken ct = default)
        => JsonSerializer.Serialize(
            await _facade.ListSessionsAsync(
                page ?? 1,
                pageSize ?? 25,
                IntegrationApiFacade.BuildSessionQuery(search, channelId, senderId, fromUtc, toUtc, state, starred, tag),
                ct),
            CoreJsonContext.Default.IntegrationSessionsResponse);

    [McpServerTool(Name = "openclaw.get_session", ReadOnly = true),
     Description("Get a session by session ID.")]
    public async Task<string> GetSession(
        [Description("The session ID.")] string sessionId,
        CancellationToken ct)
    {
        var session = await _facade.GetSessionAsync(sessionId, ct);
        if (session is null)
            throw new KeyNotFoundException($"Session '{sessionId}' was not found.");

        return JsonSerializer.Serialize(session, CoreJsonContext.Default.IntegrationSessionDetailResponse);
    }

    [McpServerTool(Name = "openclaw.get_session_timeline", ReadOnly = true),
     Description("Get the runtime timeline for a session.")]
    public async Task<string> GetSessionTimeline(
        [Description("The session ID.")] string sessionId,
        [Description("Maximum number of timeline events to return.")] int? limit = null,
        CancellationToken ct = default)
    {
        var timeline = await _facade.GetSessionTimelineAsync(sessionId, limit ?? 100, ct);
        if (timeline is null)
            throw new KeyNotFoundException($"Session '{sessionId}' was not found.");

        return JsonSerializer.Serialize(timeline, CoreJsonContext.Default.IntegrationSessionTimelineResponse);
    }

    [McpServerTool(Name = "openclaw.search_sessions", ReadOnly = true),
     Description("Search session content across messages and tool results.")]
    public async Task<string> SearchSessions(
        [Description("Search text.")] string text,
        [Description("Maximum number of results.")] int? limit = null,
        [Description("Optional channel ID filter.")] string? channelId = null,
        [Description("Optional sender ID filter.")] string? senderId = null,
        CancellationToken ct = default)
        => JsonSerializer.Serialize(
            await _facade.SearchSessionsAsync(new SessionSearchQuery
            {
                Text = text,
                Limit = limit ?? 25,
                ChannelId = channelId,
                SenderId = senderId
            }, ct),
            CoreJsonContext.Default.IntegrationSessionSearchResponse);

    [McpServerTool(Name = "openclaw.get_profile", ReadOnly = true),
     Description("Get a user profile by actor ID.")]
    public async Task<string> GetProfile(
        [Description("Actor ID in the format channelId:senderId.")] string actorId,
        CancellationToken ct)
    {
        var response = await _facade.GetProfileAsync(actorId, ct);
        if (response.Profile is null)
            throw new KeyNotFoundException($"Profile '{actorId}' was not found.");

        return JsonSerializer.Serialize(response, CoreJsonContext.Default.IntegrationProfileResponse);
    }

    [McpServerTool(Name = "openclaw.list_automations", ReadOnly = true),
     Description("List automations and their definitions.")]
    public async Task<string> ListAutomations(CancellationToken ct)
        => JsonSerializer.Serialize(
            await _facade.ListAutomationsAsync(ct),
            CoreJsonContext.Default.IntegrationAutomationsResponse);

    [McpServerTool(Name = "openclaw.get_automation", ReadOnly = true),
     Description("Get an automation definition and latest run state.")]
    public async Task<string> GetAutomation(
        [Description("The automation ID.")] string automationId,
        CancellationToken ct)
    {
        var detail = await _facade.GetAutomationAsync(automationId, ct);
        if (detail.Automation is null)
            throw new KeyNotFoundException($"Automation '{automationId}' was not found.");

        return JsonSerializer.Serialize(detail, CoreJsonContext.Default.IntegrationAutomationDetailResponse);
    }

    [McpServerTool(Name = "openclaw.list_workflows", ReadOnly = true),
     Description("List configured durable workflow backends.")]
    public string ListWorkflows()
        => JsonSerializer.Serialize(
            _facade.ListWorkflows(),
            CoreJsonContext.Default.IntegrationWorkflowsResponse);

    [McpServerTool(Name = "openclaw.run_workflow"),
     Description("Start a configured durable workflow backend run.")]
    public async Task<string> RunWorkflow(
        [Description("Configured workflow backend ID.")] string workflowId,
        [Description("Primary workflow input text.")] string input,
        [Description("Optional JSON object or value passed as workflow payload.")] string? payloadJson = null,
        [Description("Optional channel ID.")] string? channelId = null,
        [Description("Optional sender ID.")] string? senderId = null,
        [Description("Optional session ID.")] string? sessionId = null,
        CancellationToken ct = default)
        => JsonSerializer.Serialize(
            await _facade.RunWorkflowAsync(
                workflowId,
                new AgentWorkflowRequest
                {
                    Input = input,
                    Payload = ParsePayloadJson(payloadJson),
                    ChannelId = channelId,
                    SenderId = senderId,
                    SessionId = sessionId
                },
                ct),
            CoreJsonContext.Default.AgentWorkflowRunResult);

    [McpServerTool(Name = "openclaw.get_workflow_run", ReadOnly = true),
     Description("Get the current status snapshot for a durable workflow run.")]
    public async Task<string> GetWorkflowRun(
        [Description("Configured workflow backend ID.")] string workflowId,
        [Description("Workflow run ID.")] string runId,
        CancellationToken ct)
        => JsonSerializer.Serialize(
            await _facade.GetWorkflowRunAsync(workflowId, runId, ct),
            CoreJsonContext.Default.AgentWorkflowRunSnapshot);

    [McpServerTool(Name = "openclaw.respond_workflow"),
     Description("Send a response to a pending durable workflow input port.")]
    public async Task<string> RespondWorkflow(
        [Description("Configured workflow backend ID.")] string workflowId,
        [Description("Workflow run ID.")] string runId,
        [Description("Pending input port ID.")] string portId,
        [Description("Optional approval decision.")] bool? approved = null,
        [Description("Optional response comment.")] string? comment = null,
        [Description("Optional actor ID for the responder.")] string? actorId = null,
        [Description("Optional JSON object or value passed as response payload.")] string? payloadJson = null,
        CancellationToken ct = default)
        => JsonSerializer.Serialize(
            await _facade.RespondWorkflowRunAsync(
                workflowId,
                runId,
                new AgentWorkflowResponse
                {
                    PortId = portId,
                    Approved = approved,
                    Comment = comment,
                    ActorId = actorId,
                    Payload = ParsePayloadJson(payloadJson)
                },
                ct),
            CoreJsonContext.Default.AgentWorkflowRunSnapshot);

    [McpServerTool(Name = "openclaw.query_runtime_events", ReadOnly = true),
     Description("Query recent runtime events.")]
    public string QueryRuntimeEvents(
        [Description("Maximum number of events to return.")] int? limit = null,
        [Description("Optional session ID filter.")] string? sessionId = null,
        [Description("Optional channel ID filter.")] string? channelId = null,
        [Description("Optional sender ID filter.")] string? senderId = null,
        [Description("Optional component filter.")] string? component = null,
        [Description("Optional action filter.")] string? action = null)
        => JsonSerializer.Serialize(
            _facade.QueryRuntimeEvents(new RuntimeEventQuery
            {
                Limit = limit ?? 100,
                SessionId = sessionId,
                ChannelId = channelId,
                SenderId = senderId,
                Component = component,
                Action = action
            }),
            CoreJsonContext.Default.IntegrationRuntimeEventsResponse);

    [McpServerTool(Name = "openclaw.send_message"),
     Description("Queue a message into the OpenClaw inbound pipeline.")]
    public async Task<string> SendMessage(
        [Description("Message text to send.")] string text,
        [Description("Optional channel ID.")] string? channelId = null,
        [Description("Optional sender ID.")] string? senderId = null,
        [Description("Optional session ID.")] string? sessionId = null,
        [Description("Optional idempotency message ID.")] string? messageId = null,
        [Description("Optional reply-to message ID.")] string? replyToMessageId = null,
        CancellationToken ct = default)
        => JsonSerializer.Serialize(
            await _facade.QueueMessageAsync(new IntegrationMessageRequest
            {
                Text = text,
                ChannelId = channelId,
                SenderId = senderId,
                SessionId = sessionId,
                MessageId = messageId,
                ReplyToMessageId = replyToMessageId
            }, ct),
            CoreJsonContext.Default.IntegrationMessageResponse);

    private static JsonElement? ParsePayloadJson(string? payloadJson)
    {
        if (string.IsNullOrWhiteSpace(payloadJson))
            return null;

        try
        {
            using var document = JsonDocument.Parse(payloadJson);
            return document.RootElement.Clone();
        }
        catch (JsonException ex)
        {
            throw new ArgumentException("payloadJson must be valid JSON.", nameof(payloadJson), ex);
        }
    }
}

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

    // ── Dashboard & Status ────────────────────────────────────────────────────

    [McpServerTool(Name = "openclaw.get_dashboard"),
     Description("Get the aggregated operator dashboard snapshot.")]
    public async Task<string> GetDashboard(CancellationToken ct)
        => JsonSerializer.Serialize(
            await _facade.GetDashboardAsync(ct),
            CoreJsonContext.Default.IntegrationDashboardResponse);

    [McpServerTool(Name = "openclaw.get_status"),
     Description("Get the current OpenClaw gateway runtime status.")]
    public string GetStatus()
        => JsonSerializer.Serialize(
            _facade.BuildStatusResponse(),
            CoreJsonContext.Default.IntegrationStatusResponse);

    // ── Approvals ─────────────────────────────────────────────────────────────

    [McpServerTool(Name = "openclaw.list_approvals"),
     Description("List pending tool approvals with optional channel or sender filters.")]
    public string ListApprovals(
        [Description("Optional channel ID filter.")] string? channelId = null,
        [Description("Optional sender ID filter.")] string? senderId = null)
        => JsonSerializer.Serialize(
            _facade.GetApprovals(channelId, senderId),
            CoreJsonContext.Default.IntegrationApprovalsResponse);

    [McpServerTool(Name = "openclaw.get_approval_history"),
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

    // ── Providers & Plugins ───────────────────────────────────────────────────

    [McpServerTool(Name = "openclaw.get_providers"),
     Description("Get provider routing, usage, policies, and recent turns.")]
    public string GetProviders(
        [Description("Maximum number of recent turns to include.")] int? recentTurnsLimit = null)
        => JsonSerializer.Serialize(
            _facade.GetProviders(recentTurnsLimit ?? 20),
            CoreJsonContext.Default.IntegrationProvidersResponse);

    [McpServerTool(Name = "openclaw.get_plugins"),
     Description("Get the current plugin health listing.")]
    public string GetPlugins()
        => JsonSerializer.Serialize(
            _facade.GetPlugins(),
            CoreJsonContext.Default.IntegrationPluginsResponse);

    // ── Operator Audit ────────────────────────────────────────────────────────

    [McpServerTool(Name = "openclaw.query_operator_audit"),
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

    // ── Sessions ──────────────────────────────────────────────────────────────

    [McpServerTool(Name = "openclaw.list_sessions"),
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

    [McpServerTool(Name = "openclaw.get_session"),
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

    [McpServerTool(Name = "openclaw.get_session_timeline"),
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

    // ── Runtime Events ────────────────────────────────────────────────────────

    [McpServerTool(Name = "openclaw.query_runtime_events"),
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

    // ── Messaging ─────────────────────────────────────────────────────────────

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
}

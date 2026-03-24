using System.ComponentModel;
using System.Text.Json;
using ModelContextProtocol.Server;
using OpenClaw.Core.Models;
using OpenClaw.Gateway.Composition;

namespace OpenClaw.Gateway.Mcp;

[McpServerResourceType]
internal sealed class OpenClawMcpResources
{
    private readonly IntegrationApiFacade _facade;

    public OpenClawMcpResources(IntegrationApiFacade facade) => _facade = facade;

    [McpServerResource(UriTemplate = "openclaw://status", Name = "Gateway Status", MimeType = "application/json")]
    [Description("Current gateway runtime status snapshot.")]
    public string GetStatus()
        => JsonSerializer.Serialize(
            _facade.BuildStatusResponse(),
            CoreJsonContext.Default.IntegrationStatusResponse);

    [McpServerResource(UriTemplate = "openclaw://dashboard", Name = "Dashboard Snapshot", MimeType = "application/json")]
    [Description("Aggregated operator dashboard snapshot.")]
    public async Task<string> GetDashboard(CancellationToken ct)
        => JsonSerializer.Serialize(
            await _facade.GetDashboardAsync(ct),
            CoreJsonContext.Default.IntegrationDashboardResponse);

    [McpServerResource(UriTemplate = "openclaw://approvals", Name = "Pending Approvals", MimeType = "application/json")]
    [Description("Current pending tool approvals.")]
    public string GetApprovals()
        => JsonSerializer.Serialize(
            _facade.GetApprovals(channelId: null, senderId: null),
            CoreJsonContext.Default.IntegrationApprovalsResponse);

    [McpServerResource(UriTemplate = "openclaw://approvals/history", Name = "Approval History", MimeType = "application/json")]
    [Description("Recent approval history entries.")]
    public string GetApprovalHistory()
        => JsonSerializer.Serialize(
            _facade.GetApprovalHistory(new ApprovalHistoryQuery { Limit = 20 }),
            CoreJsonContext.Default.IntegrationApprovalHistoryResponse);

    [McpServerResource(UriTemplate = "openclaw://providers", Name = "Provider Snapshot", MimeType = "application/json")]
    [Description("Provider routing, usage, and recent turn summary.")]
    public string GetProviders()
        => JsonSerializer.Serialize(
            _facade.GetProviders(recentTurnsLimit: 20),
            CoreJsonContext.Default.IntegrationProvidersResponse);

    [McpServerResource(UriTemplate = "openclaw://plugins", Name = "Plugin Snapshot", MimeType = "application/json")]
    [Description("Current plugin health listing.")]
    public string GetPlugins()
        => JsonSerializer.Serialize(
            _facade.GetPlugins(),
            CoreJsonContext.Default.IntegrationPluginsResponse);

    [McpServerResource(UriTemplate = "openclaw://operator-audit", Name = "Operator Audit", MimeType = "application/json")]
    [Description("Recent operator audit entries.")]
    public string GetOperatorAudit()
        => JsonSerializer.Serialize(
            _facade.GetOperatorAudit(new OperatorAuditQuery { Limit = 20 }),
            CoreJsonContext.Default.IntegrationOperatorAuditResponse);

    [McpServerResource(UriTemplate = "openclaw://sessions/{sessionId}", Name = "Session Detail", MimeType = "application/json")]
    [Description("Read a session detail snapshot by session ID.")]
    public async Task<string> GetSession(string sessionId, CancellationToken ct)
    {
        var session = await _facade.GetSessionAsync(Uri.UnescapeDataString(sessionId), ct);
        if (session is null)
            throw new KeyNotFoundException($"Session '{sessionId}' was not found.");

        return JsonSerializer.Serialize(session, CoreJsonContext.Default.IntegrationSessionDetailResponse);
    }

    [McpServerResource(UriTemplate = "openclaw://sessions/{sessionId}/timeline", Name = "Session Timeline", MimeType = "application/json")]
    [Description("Read the runtime timeline for a session.")]
    public async Task<string> GetSessionTimeline(string sessionId, CancellationToken ct)
    {
        var timeline = await _facade.GetSessionTimelineAsync(Uri.UnescapeDataString(sessionId), limit: 100, ct);
        if (timeline is null)
            throw new KeyNotFoundException($"Session '{sessionId}' was not found.");

        return JsonSerializer.Serialize(timeline, CoreJsonContext.Default.IntegrationSessionTimelineResponse);
    }
}

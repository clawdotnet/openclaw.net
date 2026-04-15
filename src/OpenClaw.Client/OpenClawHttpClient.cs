using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization.Metadata;
using OpenClaw.Core.Models;

namespace OpenClaw.Client;

public sealed class OpenClawHttpClient : IDisposable
{
    private readonly Uri _baseUri;
    private readonly HttpClient _http;
    private readonly bool _ownsHttpClient;
    private readonly Uri _chatCompletionsUri;
    private readonly Uri _mcpUri;
    private readonly Uri _integrationDashboardUri;
    private readonly Uri _integrationStatusUri;
    private readonly Uri _integrationApprovalsUri;
    private readonly Uri _integrationApprovalHistoryUri;
    private readonly Uri _integrationProvidersUri;
    private readonly Uri _integrationPluginsUri;
    private readonly Uri _integrationOperatorAuditUri;
    private readonly Uri _integrationAccountsUri;
    private readonly Uri _integrationBackendsUri;
    private readonly Uri _integrationSessionsUri;
    private readonly Uri _integrationSessionSearchUri;
    private readonly Uri _integrationProfilesUri;
    private readonly Uri _integrationToolPresetsUri;
    private readonly Uri _integrationAutomationsUri;
    private readonly Uri _integrationRuntimeEventsUri;
    private readonly Uri _integrationMessagesUri;
    private readonly Uri _adminAutomationsUri;
    private readonly Uri _adminLearningProposalsUri;
    private readonly Uri _adminMemoryNotesUri;
    private readonly Uri _adminMemorySearchUri;
    private readonly Uri _adminMemoryExportUri;
    private readonly Uri _adminMemoryImportUri;
    private readonly Uri _adminHeartbeatUri;
    private readonly Uri _adminHeartbeatPreviewUri;
    private readonly Uri _adminHeartbeatStatusUri;
    private readonly Uri _adminPostureUri;
    private readonly Uri _adminModelsUri;
    private readonly Uri _adminModelsDoctorUri;
    private readonly Uri _adminModelEvaluationsUri;
    private readonly Uri _adminApprovalSimulationUri;
    private readonly Uri _adminAccountResolutionUri;
    private readonly Uri _adminBackendsUri;
    private readonly Uri _adminIncidentExportUri;
    private readonly Uri _adminWhatsAppSetupUri;
    private readonly Uri _adminWhatsAppRestartUri;
    private long _mcpRequestId;

    public OpenClawHttpClient(string baseUrl, string? authToken, HttpClient? httpClient = null)
    {
        if (string.IsNullOrWhiteSpace(baseUrl))
            throw new ArgumentException("Base URL is required.", nameof(baseUrl));

        var normalized = baseUrl.TrimEnd('/');
        if (!Uri.TryCreate(normalized, UriKind.Absolute, out var baseUri))
            throw new ArgumentException($"Invalid base URL: {baseUrl}", nameof(baseUrl));

        _baseUri = baseUri;
        _chatCompletionsUri = new Uri(baseUri, "/v1/chat/completions");
        _mcpUri = new Uri(baseUri, "/mcp");
        _integrationDashboardUri = new Uri(baseUri, "/api/integration/dashboard");
        _integrationStatusUri = new Uri(baseUri, "/api/integration/status");
        _integrationApprovalsUri = new Uri(baseUri, "/api/integration/approvals");
        _integrationApprovalHistoryUri = new Uri(baseUri, "/api/integration/approval-history");
        _integrationProvidersUri = new Uri(baseUri, "/api/integration/providers");
        _integrationPluginsUri = new Uri(baseUri, "/api/integration/plugins");
        _integrationOperatorAuditUri = new Uri(baseUri, "/api/integration/operator-audit");
        _integrationAccountsUri = new Uri(baseUri, "/api/integration/accounts");
        _integrationBackendsUri = new Uri(baseUri, "/api/integration/backends");
        _integrationSessionsUri = new Uri(baseUri, "/api/integration/sessions");
        _integrationSessionSearchUri = new Uri(baseUri, "/api/integration/session-search");
        _integrationProfilesUri = new Uri(baseUri, "/api/integration/profiles");
        _integrationToolPresetsUri = new Uri(baseUri, "/api/integration/tool-presets");
        _integrationAutomationsUri = new Uri(baseUri, "/api/integration/automations");
        _integrationRuntimeEventsUri = new Uri(baseUri, "/api/integration/runtime-events");
        _integrationMessagesUri = new Uri(baseUri, "/api/integration/messages");
        _adminAutomationsUri = new Uri(baseUri, "/admin/automations");
        _adminLearningProposalsUri = new Uri(baseUri, "/admin/learning/proposals");
        _adminMemoryNotesUri = new Uri(baseUri, "/admin/memory/notes");
        _adminMemorySearchUri = new Uri(baseUri, "/admin/memory/search");
        _adminMemoryExportUri = new Uri(baseUri, "/admin/memory/export");
        _adminMemoryImportUri = new Uri(baseUri, "/admin/memory/import");
        _adminHeartbeatUri = new Uri(baseUri, "/admin/heartbeat");
        _adminHeartbeatPreviewUri = new Uri(baseUri, "/admin/heartbeat/preview");
        _adminHeartbeatStatusUri = new Uri(baseUri, "/admin/heartbeat/status");
        _adminPostureUri = new Uri(baseUri, "/admin/posture");
        _adminModelsUri = new Uri(baseUri, "/admin/models");
        _adminModelsDoctorUri = new Uri(baseUri, "/admin/models/doctor");
        _adminModelEvaluationsUri = new Uri(baseUri, "/admin/models/evaluations");
        _adminApprovalSimulationUri = new Uri(baseUri, "/admin/approvals/simulate");
        _adminAccountResolutionUri = new Uri(baseUri, "/admin/accounts/test-resolution");
        _adminBackendsUri = new Uri(baseUri, "/admin/backends");
        _adminIncidentExportUri = new Uri(baseUri, "/admin/incident/export");
        _adminWhatsAppSetupUri = new Uri(baseUri, "/admin/channels/whatsapp/setup");
        _adminWhatsAppRestartUri = new Uri(baseUri, "/admin/channels/whatsapp/restart");

        _http = httpClient ?? new HttpClient { Timeout = Timeout.InfiniteTimeSpan };
        _ownsHttpClient = httpClient is null;
        _http.DefaultRequestHeaders.UserAgent.ParseAdd("openclaw-client/1.0");

        if (!string.IsNullOrWhiteSpace(authToken))
            _http.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", authToken);
    }

    public Uri GetLiveWebSocketUri()
        => OpenClawLiveClient.BuildWebSocketUri(_baseUri);

    public async Task<OpenAiChatCompletionResponse> ChatCompletionAsync(
        OpenAiChatCompletionRequest request,
        CancellationToken cancellationToken,
        string? presetId = null)
    {
        using var req = new HttpRequestMessage(HttpMethod.Post, _chatCompletionsUri)
        {
            Content = BuildJsonContent(request, CoreJsonContext.Default.OpenAiChatCompletionRequest)
        };
        ApplyPresetHeader(req, presetId);

        return await SendAsync(req, CoreJsonContext.Default.OpenAiChatCompletionResponse, cancellationToken);
    }

    public async Task<string> StreamChatCompletionAsync(
        OpenAiChatCompletionRequest request,
        Action<string> onText,
        CancellationToken cancellationToken,
        string? presetId = null)
    {
        using var req = new HttpRequestMessage(HttpMethod.Post, _chatCompletionsUri)
        {
            Content = BuildJsonContent(request, CoreJsonContext.Default.OpenAiChatCompletionRequest)
        };
        req.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("text/event-stream"));
        ApplyPresetHeader(req, presetId);

        using var resp = await _http.SendAsync(req, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
        if (!resp.IsSuccessStatusCode)
            throw await CreateHttpErrorAsync(resp, cancellationToken);

        await using var stream = await resp.Content.ReadAsStreamAsync(cancellationToken);
        using var reader = new StreamReader(stream, Encoding.UTF8, detectEncodingFromByteOrderMarks: true, bufferSize: 16 * 1024, leaveOpen: false);

        var fullText = new StringBuilder();
        while (true)
        {
            var line = await reader.ReadLineAsync(cancellationToken);
            if (line is null)
                break;

            if (!line.StartsWith("data:", StringComparison.Ordinal))
                continue;

            var data = line["data:".Length..].TrimStart();
            if (data.Length == 0)
                continue;

            if (data == "[DONE]")
                break;

            OpenAiStreamChunk? chunk;
            try
            {
                chunk = JsonSerializer.Deserialize(data, CoreJsonContext.Default.OpenAiStreamChunk);
            }
            catch (Exception ex)
            {
                throw new InvalidOperationException($"Failed to parse SSE chunk: {data}", ex);
            }

            var delta = chunk?.Choices.Count > 0 ? chunk.Choices[0].Delta.Content : null;
            if (string.IsNullOrEmpty(delta))
                continue;

            fullText.Append(delta);
            onText(delta);
        }

        return fullText.ToString();
    }

    public Task<McpInitializeResult> InitializeMcpAsync(McpInitializeRequest request, CancellationToken cancellationToken)
        => SendMcpAsync("initialize", request, McpJsonContext.Default.McpInitializeRequest, McpJsonContext.Default.McpInitializeResult, cancellationToken);

    public Task<McpToolListResult> ListMcpToolsAsync(CancellationToken cancellationToken)
        => SendMcpWithoutParamsAsync("tools/list", McpJsonContext.Default.McpToolListResult, cancellationToken);

    public Task<McpResourceListResult> ListMcpResourcesAsync(CancellationToken cancellationToken)
        => SendMcpWithoutParamsAsync("resources/list", McpJsonContext.Default.McpResourceListResult, cancellationToken);

    public Task<McpResourceTemplateListResult> ListMcpResourceTemplatesAsync(CancellationToken cancellationToken)
        => SendMcpWithoutParamsAsync("resources/templates/list", McpJsonContext.Default.McpResourceTemplateListResult, cancellationToken);

    public Task<McpReadResourceResult> ReadMcpResourceAsync(string uri, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(uri))
            throw new ArgumentException("Resource uri is required.", nameof(uri));

        return SendMcpAsync(
            "resources/read",
            new McpReadResourceRequest { Uri = uri },
            McpJsonContext.Default.McpReadResourceRequest,
            McpJsonContext.Default.McpReadResourceResult,
            cancellationToken);
    }

    public Task<McpPromptListResult> ListMcpPromptsAsync(CancellationToken cancellationToken)
        => SendMcpWithoutParamsAsync("prompts/list", McpJsonContext.Default.McpPromptListResult, cancellationToken);

    public Task<McpGetPromptResult> GetMcpPromptAsync(string name, IReadOnlyDictionary<string, string>? arguments, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(name))
            throw new ArgumentException("Prompt name is required.", nameof(name));

        return SendMcpAsync(
            "prompts/get",
            new McpGetPromptRequest
            {
                Name = name,
                Arguments = arguments is null
                    ? []
                    : new Dictionary<string, string>(arguments, StringComparer.Ordinal)
            },
            McpJsonContext.Default.McpGetPromptRequest,
            McpJsonContext.Default.McpGetPromptResult,
            cancellationToken);
    }

    public Task<McpCallToolResult> CallMcpToolAsync(string name, JsonElement arguments, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(name))
            throw new ArgumentException("Tool name is required.", nameof(name));

        return SendMcpAsync(
            "tools/call",
            new McpCallToolRequest { Name = name, Arguments = arguments },
            McpJsonContext.Default.McpCallToolRequest,
            McpJsonContext.Default.McpCallToolResult,
            cancellationToken);
    }

    public Task<IntegrationDashboardResponse> GetIntegrationDashboardAsync(CancellationToken cancellationToken)
        => GetAsync(_integrationDashboardUri, CoreJsonContext.Default.IntegrationDashboardResponse, cancellationToken);

    public Task<IntegrationStatusResponse> GetIntegrationStatusAsync(CancellationToken cancellationToken)
        => GetAsync(_integrationStatusUri, CoreJsonContext.Default.IntegrationStatusResponse, cancellationToken);

    public Task<IntegrationApprovalsResponse> GetIntegrationApprovalsAsync(
        string? channelId,
        string? senderId,
        CancellationToken cancellationToken)
        => GetAsync(BuildApprovalsUri(channelId, senderId), CoreJsonContext.Default.IntegrationApprovalsResponse, cancellationToken);

    public Task<IntegrationApprovalHistoryResponse> GetIntegrationApprovalHistoryAsync(
        ApprovalHistoryQuery query,
        CancellationToken cancellationToken)
        => GetAsync(BuildApprovalHistoryUri(query), CoreJsonContext.Default.IntegrationApprovalHistoryResponse, cancellationToken);

    public Task<IntegrationProvidersResponse> GetIntegrationProvidersAsync(int recentTurnsLimit, CancellationToken cancellationToken)
        => GetAsync(new Uri($"{_integrationProvidersUri}?recentTurnsLimit={Math.Clamp(recentTurnsLimit, 1, 256)}", UriKind.RelativeOrAbsolute), CoreJsonContext.Default.IntegrationProvidersResponse, cancellationToken);

    public Task<IntegrationPluginsResponse> GetIntegrationPluginsAsync(CancellationToken cancellationToken)
        => GetAsync(_integrationPluginsUri, CoreJsonContext.Default.IntegrationPluginsResponse, cancellationToken);

    public Task<IntegrationAccountsResponse> GetIntegrationAccountsAsync(CancellationToken cancellationToken)
        => GetAsync(_integrationAccountsUri, CoreJsonContext.Default.IntegrationAccountsResponse, cancellationToken);

    public Task<IntegrationConnectedAccountResponse> GetIntegrationAccountAsync(string accountId, CancellationToken cancellationToken)
        => GetAsync(BuildIntegrationAccountUri(accountId), CoreJsonContext.Default.IntegrationConnectedAccountResponse, cancellationToken);

    public async Task<IntegrationConnectedAccountResponse> CreateIntegrationAccountAsync(ConnectedAccountCreateRequest request, CancellationToken cancellationToken)
    {
        using var httpRequest = new HttpRequestMessage(HttpMethod.Post, _integrationAccountsUri)
        {
            Content = BuildJsonContent(request, CoreJsonContext.Default.ConnectedAccountCreateRequest)
        };

        return await SendAsync(httpRequest, CoreJsonContext.Default.IntegrationConnectedAccountResponse, cancellationToken);
    }

    public async Task<OperationStatusResponse> DeleteIntegrationAccountAsync(string accountId, CancellationToken cancellationToken)
    {
        using var httpRequest = new HttpRequestMessage(HttpMethod.Delete, BuildIntegrationAccountUri(accountId));
        return await SendAsync(httpRequest, CoreJsonContext.Default.OperationStatusResponse, cancellationToken);
    }

    public Task<IntegrationBackendsResponse> GetIntegrationBackendsAsync(CancellationToken cancellationToken)
        => GetAsync(_integrationBackendsUri, CoreJsonContext.Default.IntegrationBackendsResponse, cancellationToken);

    public Task<IntegrationBackendResponse> GetIntegrationBackendAsync(string backendId, CancellationToken cancellationToken)
        => GetAsync(BuildIntegrationBackendUri(backendId), CoreJsonContext.Default.IntegrationBackendResponse, cancellationToken);

    public async Task<BackendProbeResult> ProbeIntegrationBackendAsync(string backendId, BackendProbeRequest request, CancellationToken cancellationToken)
    {
        using var httpRequest = new HttpRequestMessage(HttpMethod.Post, BuildIntegrationBackendProbeUri(backendId))
        {
            Content = BuildJsonContent(request, CoreJsonContext.Default.BackendProbeRequest)
        };

        return await SendAsync(httpRequest, CoreJsonContext.Default.BackendProbeResult, cancellationToken);
    }

    public async Task<IntegrationBackendSessionResponse> StartBackendSessionAsync(string backendId, StartBackendSessionRequest request, CancellationToken cancellationToken)
    {
        using var httpRequest = new HttpRequestMessage(HttpMethod.Post, BuildIntegrationBackendSessionsUri(backendId))
        {
            Content = BuildJsonContent(request, CoreJsonContext.Default.StartBackendSessionRequest)
        };

        return await SendAsync(httpRequest, CoreJsonContext.Default.IntegrationBackendSessionResponse, cancellationToken);
    }

    public async Task<IntegrationBackendSessionResponse> SendBackendInputAsync(string backendId, string sessionId, BackendInput input, CancellationToken cancellationToken)
    {
        using var httpRequest = new HttpRequestMessage(HttpMethod.Post, BuildIntegrationBackendInputUri(backendId, sessionId))
        {
            Content = BuildJsonContent(input, CoreJsonContext.Default.BackendInput)
        };

        return await SendAsync(httpRequest, CoreJsonContext.Default.IntegrationBackendSessionResponse, cancellationToken);
    }

    public async Task<OperationStatusResponse> StopBackendSessionAsync(string backendId, string sessionId, CancellationToken cancellationToken)
    {
        using var httpRequest = new HttpRequestMessage(HttpMethod.Delete, BuildIntegrationBackendSessionUri(backendId, sessionId));
        return await SendAsync(httpRequest, CoreJsonContext.Default.OperationStatusResponse, cancellationToken);
    }

    public Task<IntegrationBackendSessionResponse> GetBackendSessionAsync(string backendId, string sessionId, CancellationToken cancellationToken)
        => GetAsync(BuildIntegrationBackendSessionUri(backendId, sessionId), CoreJsonContext.Default.IntegrationBackendSessionResponse, cancellationToken);

    public Task<IntegrationBackendEventsResponse> GetBackendEventsAsync(string backendId, string sessionId, long afterSequence, int limit, CancellationToken cancellationToken)
        => GetAsync(BuildIntegrationBackendEventsUri(backendId, sessionId, afterSequence, limit), CoreJsonContext.Default.IntegrationBackendEventsResponse, cancellationToken);

    public async Task StreamBackendEventsAsync(
        string backendId,
        string sessionId,
        long afterSequence,
        int limit,
        Action<BackendEvent> onEvent,
        CancellationToken cancellationToken)
    {
        using var req = new HttpRequestMessage(HttpMethod.Get, BuildIntegrationBackendEventStreamUri(backendId, sessionId, afterSequence, limit));
        req.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("text/event-stream"));

        using var resp = await _http.SendAsync(req, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
        if (!resp.IsSuccessStatusCode)
            throw await CreateHttpErrorAsync(resp, cancellationToken);

        await using var stream = await resp.Content.ReadAsStreamAsync(cancellationToken);
        using var reader = new StreamReader(stream, Encoding.UTF8, detectEncodingFromByteOrderMarks: true, bufferSize: 16 * 1024, leaveOpen: false);

        while (true)
        {
            var line = await reader.ReadLineAsync(cancellationToken);
            if (line is null)
                break;
            if (!line.StartsWith("data:", StringComparison.Ordinal))
                continue;

            var data = line["data:".Length..].TrimStart();
            if (data.Length == 0)
                continue;

            var item = JsonSerializer.Deserialize(data, CoreJsonContext.Default.BackendEvent);
            if (item is not null)
                onEvent(item);
        }
    }

    public Task<IntegrationOperatorAuditResponse> GetIntegrationOperatorAuditAsync(
        OperatorAuditQuery query,
        CancellationToken cancellationToken)
        => GetAsync(BuildOperatorAuditUri(query), CoreJsonContext.Default.IntegrationOperatorAuditResponse, cancellationToken);

    public Task<IntegrationSessionsResponse> ListSessionsAsync(
        int page,
        int pageSize,
        SessionListQuery? query,
        CancellationToken cancellationToken)
        => GetAsync(BuildSessionsUri(page, pageSize, query), CoreJsonContext.Default.IntegrationSessionsResponse, cancellationToken);

    public Task<IntegrationSessionDetailResponse> GetSessionAsync(string sessionId, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(sessionId))
            throw new ArgumentException("Session id is required.", nameof(sessionId));

        var uri = new Uri(_integrationSessionsUri, $"{_integrationSessionsUri.AbsolutePath.TrimEnd('/')}/{Uri.EscapeDataString(sessionId)}");
        return GetAsync(uri, CoreJsonContext.Default.IntegrationSessionDetailResponse, cancellationToken);
    }

    public Task<IntegrationSessionTimelineResponse> GetSessionTimelineAsync(string sessionId, int limit, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(sessionId))
            throw new ArgumentException("Session id is required.", nameof(sessionId));

        var uri = new Uri($"{_integrationSessionsUri.AbsoluteUri}/{Uri.EscapeDataString(sessionId)}/timeline?limit={Math.Clamp(limit, 1, 500)}", UriKind.Absolute);
        return GetAsync(uri, CoreJsonContext.Default.IntegrationSessionTimelineResponse, cancellationToken);
    }

    public Task<IntegrationSessionSearchResponse> SearchSessionsAsync(SessionSearchQuery query, CancellationToken cancellationToken)
        => GetAsync(BuildSessionSearchUri(query), CoreJsonContext.Default.IntegrationSessionSearchResponse, cancellationToken);

    public Task<IntegrationProfilesResponse> ListProfilesAsync(CancellationToken cancellationToken)
        => GetAsync(_integrationProfilesUri, CoreJsonContext.Default.IntegrationProfilesResponse, cancellationToken);

    public Task<IntegrationToolPresetsResponse> ListToolPresetsAsync(CancellationToken cancellationToken)
        => GetAsync(_integrationToolPresetsUri, CoreJsonContext.Default.IntegrationToolPresetsResponse, cancellationToken);

    public Task<IntegrationProfileResponse> GetProfileAsync(string actorId, CancellationToken cancellationToken)
        => GetAsync(BuildProfileUri(actorId), CoreJsonContext.Default.IntegrationProfileResponse, cancellationToken);

    public async Task<IntegrationProfileResponse> SaveProfileAsync(string actorId, UserProfile profile, CancellationToken cancellationToken)
    {
        using var req = new HttpRequestMessage(HttpMethod.Put, BuildProfileUri(actorId))
        {
            Content = BuildJsonContent(new IntegrationProfileUpdateRequest { Profile = profile }, CoreJsonContext.Default.IntegrationProfileUpdateRequest)
        };

        return await SendAsync(req, CoreJsonContext.Default.IntegrationProfileResponse, cancellationToken);
    }

    public Task<MemoryNoteListResponse> ListMemoryNotesAsync(
        string? prefix,
        string? memoryClass,
        string? projectId,
        int limit,
        CancellationToken cancellationToken)
        => GetAsync(BuildMemoryNotesUri(prefix, memoryClass, projectId, limit), CoreJsonContext.Default.MemoryNoteListResponse, cancellationToken);

    public Task<MemoryNoteListResponse> SearchMemoryNotesAsync(
        string query,
        string? memoryClass,
        string? projectId,
        int limit,
        CancellationToken cancellationToken)
        => GetAsync(BuildMemorySearchUri(query, memoryClass, projectId, limit), CoreJsonContext.Default.MemoryNoteListResponse, cancellationToken);

    public Task<MemoryNoteDetailResponse> GetMemoryNoteAsync(string key, CancellationToken cancellationToken)
        => GetAsync(BuildMemoryNoteUri(key), CoreJsonContext.Default.MemoryNoteDetailResponse, cancellationToken);

    public async Task<MemoryNoteDetailResponse> SaveMemoryNoteAsync(MemoryNoteUpsertRequest request, CancellationToken cancellationToken)
    {
        using var req = new HttpRequestMessage(HttpMethod.Post, _adminMemoryNotesUri)
        {
            Content = BuildJsonContent(request, CoreJsonContext.Default.MemoryNoteUpsertRequest)
        };

        return await SendAsync(req, CoreJsonContext.Default.MemoryNoteDetailResponse, cancellationToken);
    }

    public async Task<MutationResponse> DeleteMemoryNoteAsync(string key, CancellationToken cancellationToken)
    {
        using var req = new HttpRequestMessage(HttpMethod.Delete, BuildMemoryNoteUri(key));
        return await SendAsync(req, CoreJsonContext.Default.MutationResponse, cancellationToken);
    }

    public Task<MemoryConsoleExportBundle> ExportMemoryConsoleAsync(
        string? actorId,
        string? projectId,
        bool includeProfiles,
        bool includeProposals,
        bool includeAutomations,
        bool includeNotes,
        CancellationToken cancellationToken)
        => GetAsync(
            BuildMemoryExportUri(actorId, projectId, includeProfiles, includeProposals, includeAutomations, includeNotes),
            CoreJsonContext.Default.MemoryConsoleExportBundle,
            cancellationToken);

    public async Task<MemoryConsoleImportResponse> ImportMemoryConsoleAsync(
        MemoryConsoleExportBundle bundle,
        CancellationToken cancellationToken)
    {
        using var req = new HttpRequestMessage(HttpMethod.Post, _adminMemoryImportUri)
        {
            Content = BuildJsonContent(bundle, CoreJsonContext.Default.MemoryConsoleExportBundle)
        };

        return await SendAsync(req, CoreJsonContext.Default.MemoryConsoleImportResponse, cancellationToken);
    }

    public async Task<SessionMetadataSnapshot> UpdateSessionMetadataAsync(
        string sessionId,
        SessionMetadataUpdateRequest request,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(sessionId))
            throw new ArgumentException("Session id is required.", nameof(sessionId));

        var uri = new Uri(_adminAutomationsUri, $"/admin/sessions/{Uri.EscapeDataString(sessionId)}/metadata");
        using var httpRequest = new HttpRequestMessage(HttpMethod.Post, uri)
        {
            Content = BuildJsonContent(request, CoreJsonContext.Default.SessionMetadataUpdateRequest)
        };

        return await SendAsync(httpRequest, CoreJsonContext.Default.SessionMetadataSnapshot, cancellationToken);
    }

    public Task<IntegrationAutomationsResponse> ListAutomationsAsync(CancellationToken cancellationToken)
        => GetAsync(_integrationAutomationsUri, CoreJsonContext.Default.IntegrationAutomationsResponse, cancellationToken);

    public Task<IntegrationAutomationDetailResponse> GetAutomationAsync(string automationId, CancellationToken cancellationToken)
        => GetAsync(BuildAutomationUri(automationId), CoreJsonContext.Default.IntegrationAutomationDetailResponse, cancellationToken);

    public async Task<MutationResponse> RunAutomationAsync(string automationId, bool dryRun, CancellationToken cancellationToken)
    {
        using var req = new HttpRequestMessage(HttpMethod.Post, BuildAutomationRunUri(automationId))
        {
            Content = BuildJsonContent(new AutomationRunRequest { DryRun = dryRun }, CoreJsonContext.Default.AutomationRunRequest)
        };

        return await SendAsync(req, CoreJsonContext.Default.MutationResponse, cancellationToken);
    }

    public Task<IntegrationRuntimeEventsResponse> QueryRuntimeEventsAsync(
        RuntimeEventQuery query,
        CancellationToken cancellationToken)
        => GetAsync(BuildRuntimeEventsUri(query), CoreJsonContext.Default.IntegrationRuntimeEventsResponse, cancellationToken);

    public async Task<IntegrationMessageResponse> EnqueueMessageAsync(
        IntegrationMessageRequest request,
        CancellationToken cancellationToken)
    {
        using var req = new HttpRequestMessage(HttpMethod.Post, _integrationMessagesUri)
        {
            Content = BuildJsonContent(request, CoreJsonContext.Default.IntegrationMessageRequest)
        };

        return await SendAsync(req, CoreJsonContext.Default.IntegrationMessageResponse, cancellationToken);
    }

    public Task<HeartbeatPreviewResponse> GetHeartbeatAsync(CancellationToken cancellationToken)
        => GetAsync(_adminHeartbeatUri, CoreJsonContext.Default.HeartbeatPreviewResponse, cancellationToken);

    public Task<IntegrationAutomationsResponse> GetAdminAutomationsAsync(CancellationToken cancellationToken)
        => GetAsync(_adminAutomationsUri, CoreJsonContext.Default.IntegrationAutomationsResponse, cancellationToken);

    public Task<IntegrationAutomationDetailResponse> GetAdminAutomationAsync(string automationId, CancellationToken cancellationToken)
        => GetAsync(BuildAdminAutomationUri(automationId), CoreJsonContext.Default.IntegrationAutomationDetailResponse, cancellationToken);

    public async Task<AutomationPreview> PreviewAutomationAsync(AutomationDefinition automation, CancellationToken cancellationToken)
    {
        using var req = new HttpRequestMessage(HttpMethod.Post, new Uri(_adminAutomationsUri, $"{_adminAutomationsUri.AbsolutePath.TrimEnd('/')}/preview"))
        {
            Content = BuildJsonContent(automation, CoreJsonContext.Default.AutomationDefinition)
        };

        return await SendAsync(req, CoreJsonContext.Default.AutomationPreview, cancellationToken);
    }

    public async Task<IntegrationAutomationDetailResponse> SaveAutomationAsync(string automationId, AutomationDefinition automation, CancellationToken cancellationToken)
    {
        using var req = new HttpRequestMessage(HttpMethod.Put, BuildAdminAutomationUri(automationId))
        {
            Content = BuildJsonContent(automation, CoreJsonContext.Default.AutomationDefinition)
        };

        return await SendAsync(req, CoreJsonContext.Default.IntegrationAutomationDetailResponse, cancellationToken);
    }

    public async Task<MutationResponse> RunAdminAutomationAsync(string automationId, bool dryRun, CancellationToken cancellationToken)
    {
        using var req = new HttpRequestMessage(HttpMethod.Post, BuildAdminAutomationRunUri(automationId))
        {
            Content = BuildJsonContent(new AutomationRunRequest { DryRun = dryRun }, CoreJsonContext.Default.AutomationRunRequest)
        };

        return await SendAsync(req, CoreJsonContext.Default.MutationResponse, cancellationToken);
    }

    public async Task<IntegrationAutomationsResponse> MigrateAutomationsAsync(bool apply, CancellationToken cancellationToken)
    {
        using var req = new HttpRequestMessage(HttpMethod.Post, new Uri($"{_adminAutomationsUri.AbsoluteUri.TrimEnd('/')}/migrate?apply={apply.ToString().ToLowerInvariant()}", UriKind.Absolute));
        return await SendAsync(req, CoreJsonContext.Default.IntegrationAutomationsResponse, cancellationToken);
    }

    public Task<LearningProposalListResponse> ListLearningProposalsAsync(string? status, string? kind, CancellationToken cancellationToken)
        => GetAsync(BuildLearningProposalsUri(status, kind), CoreJsonContext.Default.LearningProposalListResponse, cancellationToken);

    public async Task<LearningProposal> ApproveLearningProposalAsync(string proposalId, CancellationToken cancellationToken)
    {
        using var req = new HttpRequestMessage(HttpMethod.Post, BuildLearningProposalActionUri(proposalId, "approve"));
        return await SendAsync(req, CoreJsonContext.Default.LearningProposal, cancellationToken);
    }

    public async Task<LearningProposal> RejectLearningProposalAsync(string proposalId, string? reason, CancellationToken cancellationToken)
    {
        using var req = new HttpRequestMessage(HttpMethod.Post, BuildLearningProposalActionUri(proposalId, "reject"))
        {
            Content = BuildJsonContent(new LearningProposalReviewRequest { Reason = reason }, CoreJsonContext.Default.LearningProposalReviewRequest)
        };

        return await SendAsync(req, CoreJsonContext.Default.LearningProposal, cancellationToken);
    }

    public async Task<HeartbeatPreviewResponse> PreviewHeartbeatAsync(
        HeartbeatConfigDto request,
        CancellationToken cancellationToken)
    {
        using var req = new HttpRequestMessage(HttpMethod.Post, _adminHeartbeatPreviewUri)
        {
            Content = BuildJsonContent(request, CoreJsonContext.Default.HeartbeatConfigDto)
        };

        return await SendAsync(req, CoreJsonContext.Default.HeartbeatPreviewResponse, cancellationToken);
    }

    public async Task<HeartbeatPreviewResponse> SaveHeartbeatAsync(
        HeartbeatConfigDto request,
        CancellationToken cancellationToken)
    {
        using var req = new HttpRequestMessage(HttpMethod.Put, _adminHeartbeatUri)
        {
            Content = BuildJsonContent(request, CoreJsonContext.Default.HeartbeatConfigDto)
        };

        return await SendAsync(req, CoreJsonContext.Default.HeartbeatPreviewResponse, cancellationToken);
    }

    public Task<HeartbeatStatusResponse> GetHeartbeatStatusAsync(CancellationToken cancellationToken)
        => GetAsync(_adminHeartbeatStatusUri, CoreJsonContext.Default.HeartbeatStatusResponse, cancellationToken);

    public Task<SecurityPostureResponse> GetSecurityPostureAsync(CancellationToken cancellationToken)
        => GetAsync(_adminPostureUri, CoreJsonContext.Default.SecurityPostureResponse, cancellationToken);

    public Task<ModelProfilesStatusResponse> GetModelProfilesAsync(CancellationToken cancellationToken)
        => GetAsync(_adminModelsUri, CoreJsonContext.Default.ModelProfilesStatusResponse, cancellationToken);

    public Task<ModelSelectionDoctorResponse> GetModelSelectionDoctorAsync(CancellationToken cancellationToken)
        => GetAsync(_adminModelsDoctorUri, CoreJsonContext.Default.ModelSelectionDoctorResponse, cancellationToken);

    public async Task<ModelEvaluationReport> RunModelEvaluationAsync(ModelEvaluationRequest request, CancellationToken cancellationToken)
    {
        using var httpRequest = new HttpRequestMessage(HttpMethod.Post, _adminModelEvaluationsUri)
        {
            Content = BuildJsonContent(request, CoreJsonContext.Default.ModelEvaluationRequest)
        };

        return await SendAsync(httpRequest, CoreJsonContext.Default.ModelEvaluationReport, cancellationToken);
    }

    public async Task<ApprovalSimulationResponse> SimulateApprovalAsync(
        ApprovalSimulationRequest request,
        CancellationToken cancellationToken)
    {
        using var req = new HttpRequestMessage(HttpMethod.Post, _adminApprovalSimulationUri)
        {
            Content = BuildJsonContent(request, CoreJsonContext.Default.ApprovalSimulationRequest)
        };

        return await SendAsync(req, CoreJsonContext.Default.ApprovalSimulationResponse, cancellationToken);
    }

    public async Task<BackendCredentialResolutionResponse> TestAccountResolutionAsync(
        BackendCredentialResolutionRequest request,
        CancellationToken cancellationToken)
    {
        using var httpRequest = new HttpRequestMessage(HttpMethod.Post, _adminAccountResolutionUri)
        {
            Content = BuildJsonContent(request, CoreJsonContext.Default.BackendCredentialResolutionRequest)
        };

        return await SendAsync(httpRequest, CoreJsonContext.Default.BackendCredentialResolutionResponse, cancellationToken);
    }

    public Task<IncidentBundleResponse> ExportIncidentBundleAsync(
        int approvalLimit,
        int eventLimit,
        CancellationToken cancellationToken)
        => GetAsync(
            new Uri($"{_adminIncidentExportUri}?approvalLimit={Math.Clamp(approvalLimit, 1, 500)}&eventLimit={Math.Clamp(eventLimit, 1, 500)}", UriKind.RelativeOrAbsolute),
            CoreJsonContext.Default.IncidentBundleResponse,
            cancellationToken);

    public Task<WhatsAppSetupResponse> GetWhatsAppSetupAsync(CancellationToken cancellationToken)
        => GetAsync(_adminWhatsAppSetupUri, CoreJsonContext.Default.WhatsAppSetupResponse, cancellationToken);

    public async Task<WhatsAppSetupResponse> SaveWhatsAppSetupAsync(
        WhatsAppSetupRequest request,
        CancellationToken cancellationToken)
    {
        using var req = new HttpRequestMessage(HttpMethod.Put, _adminWhatsAppSetupUri)
        {
            Content = BuildJsonContent(request, CoreJsonContext.Default.WhatsAppSetupRequest)
        };

        return await SendAsync(req, CoreJsonContext.Default.WhatsAppSetupResponse, cancellationToken);
    }

    public async Task<WhatsAppSetupResponse> RestartWhatsAppAsync(CancellationToken cancellationToken)
    {
        using var req = new HttpRequestMessage(HttpMethod.Post, _adminWhatsAppRestartUri);
        return await SendAsync(req, CoreJsonContext.Default.WhatsAppSetupResponse, cancellationToken);
    }

    public Task<ChannelAuthStatusResponse> GetChannelAuthAsync(string channelId, string? accountId, CancellationToken cancellationToken)
        => GetAsync(BuildChannelAuthUri(channelId, accountId), CoreJsonContext.Default.ChannelAuthStatusResponse, cancellationToken);

    public async Task StreamChannelAuthAsync(
        string channelId,
        string? accountId,
        Action<ChannelAuthStatusItem> onEvent,
        CancellationToken cancellationToken)
    {
        using var req = new HttpRequestMessage(HttpMethod.Get, BuildChannelAuthStreamUri(channelId, accountId));
        req.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("text/event-stream"));

        using var resp = await _http.SendAsync(req, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
        if (!resp.IsSuccessStatusCode)
            throw await CreateHttpErrorAsync(resp, cancellationToken);

        await using var stream = await resp.Content.ReadAsStreamAsync(cancellationToken);
        using var reader = new StreamReader(stream, Encoding.UTF8, detectEncodingFromByteOrderMarks: true, bufferSize: 16 * 1024, leaveOpen: false);

        while (true)
        {
            var line = await reader.ReadLineAsync(cancellationToken);
            if (line is null)
                break;

            if (!line.StartsWith("data:", StringComparison.Ordinal))
                continue;

            var data = line["data:".Length..].TrimStart();
            if (data.Length == 0)
                continue;

            var item = JsonSerializer.Deserialize(data, CoreJsonContext.Default.ChannelAuthStatusItem);
            if (item is not null)
                onEvent(item);
        }
    }

    private async Task<T> GetAsync<T>(Uri uri, JsonTypeInfo<T> jsonTypeInfo, CancellationToken cancellationToken)
    {
        using var req = new HttpRequestMessage(HttpMethod.Get, uri);
        return await SendAsync(req, jsonTypeInfo, cancellationToken);
    }

    private Task<T> SendMcpWithoutParamsAsync<T>(string method, JsonTypeInfo<T> resultTypeInfo, CancellationToken cancellationToken)
        => SendMcpAsync<object?, T>(method, null, jsonTypeInfo: null, resultTypeInfo, cancellationToken);

    private async Task<TResult> SendMcpAsync<TParams, TResult>(
        string method,
        TParams? parameters,
        JsonTypeInfo<TParams>? jsonTypeInfo,
        JsonTypeInfo<TResult> resultTypeInfo,
        CancellationToken cancellationToken)
    {
        using var stream = new MemoryStream();
        using (var writer = new Utf8JsonWriter(stream))
        {
            writer.WriteStartObject();
            writer.WriteString("jsonrpc", "2.0");
            writer.WriteString("id", Interlocked.Increment(ref _mcpRequestId).ToString());
            writer.WriteString("method", method);
            writer.WritePropertyName("params");
            if (parameters is null || jsonTypeInfo is null)
            {
                writer.WriteStartObject();
                writer.WriteEndObject();
            }
            else
            {
                JsonSerializer.Serialize(writer, parameters, jsonTypeInfo);
            }
            writer.WriteEndObject();
        }

        stream.Position = 0;
        using var req = new HttpRequestMessage(HttpMethod.Post, _mcpUri)
        {
            Content = new StreamContent(stream)
        };
        req.Content.Headers.ContentType = new MediaTypeHeaderValue("application/json");
        req.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
        req.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("text/event-stream"));

        using var resp = await _http.SendAsync(req, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
        if (!resp.IsSuccessStatusCode)
            throw await CreateHttpErrorAsync(resp, cancellationToken);

        var jsonBody = await ExtractMcpResponseJsonAsync(resp, cancellationToken);

        var envelope = JsonSerializer.Deserialize(jsonBody, McpJsonContext.Default.McpJsonRpcResponse);
        if (envelope is null)
            throw new InvalidOperationException("Empty MCP response body.");
        if (envelope.Error is not null)
            throw new InvalidOperationException($"MCP {envelope.Error.Code}: {envelope.Error.Message}");

        var result = envelope.Result.Deserialize(resultTypeInfo);
        if (result is null)
            throw new InvalidOperationException("MCP response did not include a result payload.");

        return result;
    }

    private static async Task<string> ExtractMcpResponseJsonAsync(HttpResponseMessage resp, CancellationToken cancellationToken)
    {
        var contentType = resp.Content.Headers.ContentType?.MediaType;

        if (string.Equals(contentType, "text/event-stream", StringComparison.OrdinalIgnoreCase))
        {
            var body = await resp.Content.ReadAsStringAsync(cancellationToken);
            foreach (var line in body.Split('\n'))
            {
                if (line.StartsWith("data:", StringComparison.Ordinal))
                    return line["data:".Length..].TrimStart();
            }

            throw new InvalidOperationException("SSE response did not contain a data line.");
        }

        return await resp.Content.ReadAsStringAsync(cancellationToken);
    }

    private async Task<T> SendAsync<T>(HttpRequestMessage req, JsonTypeInfo<T> jsonTypeInfo, CancellationToken cancellationToken)
    {
        using var resp = await _http.SendAsync(req, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
        if (!resp.IsSuccessStatusCode)
            throw await CreateHttpErrorAsync(resp, cancellationToken);

        await using var stream = await resp.Content.ReadAsStreamAsync(cancellationToken);
        var parsed = await JsonSerializer.DeserializeAsync(stream, jsonTypeInfo, cancellationToken);
        if (parsed is null)
            throw new InvalidOperationException("Empty response body.");

        return parsed;
    }

    private Uri BuildSessionsUri(int page, int pageSize, SessionListQuery? query)
    {
        var pairs = new List<string>
        {
            $"page={Math.Max(1, page)}",
            $"pageSize={Math.Clamp(pageSize, 1, 200)}"
        };

        if (!string.IsNullOrWhiteSpace(query?.Search))
            pairs.Add($"search={Uri.EscapeDataString(query.Search)}");
        if (!string.IsNullOrWhiteSpace(query?.ChannelId))
            pairs.Add($"channelId={Uri.EscapeDataString(query.ChannelId)}");
        if (!string.IsNullOrWhiteSpace(query?.SenderId))
            pairs.Add($"senderId={Uri.EscapeDataString(query.SenderId)}");
        if (query?.FromUtc is { } fromUtc)
            pairs.Add($"fromUtc={Uri.EscapeDataString(fromUtc.ToString("O"))}");
        if (query?.ToUtc is { } toUtc)
            pairs.Add($"toUtc={Uri.EscapeDataString(toUtc.ToString("O"))}");
        if (query?.State is { } state)
            pairs.Add($"state={Uri.EscapeDataString(state.ToString())}");
        if (query?.Starred is { } starred)
            pairs.Add($"starred={starred.ToString().ToLowerInvariant()}");
        if (!string.IsNullOrWhiteSpace(query?.Tag))
            pairs.Add($"tag={Uri.EscapeDataString(query.Tag)}");

        return new Uri($"{_integrationSessionsUri}?{string.Join("&", pairs)}", UriKind.RelativeOrAbsolute);
    }

    private Uri BuildApprovalsUri(string? channelId, string? senderId)
    {
        var pairs = new List<string>();
        if (!string.IsNullOrWhiteSpace(channelId))
            pairs.Add($"channelId={Uri.EscapeDataString(channelId)}");
        if (!string.IsNullOrWhiteSpace(senderId))
            pairs.Add($"senderId={Uri.EscapeDataString(senderId)}");

        return pairs.Count == 0
            ? _integrationApprovalsUri
            : new Uri($"{_integrationApprovalsUri}?{string.Join("&", pairs)}", UriKind.RelativeOrAbsolute);
    }

    private Uri BuildApprovalHistoryUri(ApprovalHistoryQuery query)
    {
        var pairs = new List<string>
        {
            $"limit={Math.Clamp(query.Limit, 1, 500)}"
        };

        if (!string.IsNullOrWhiteSpace(query.ChannelId))
            pairs.Add($"channelId={Uri.EscapeDataString(query.ChannelId)}");
        if (!string.IsNullOrWhiteSpace(query.SenderId))
            pairs.Add($"senderId={Uri.EscapeDataString(query.SenderId)}");
        if (!string.IsNullOrWhiteSpace(query.ToolName))
            pairs.Add($"toolName={Uri.EscapeDataString(query.ToolName)}");

        return new Uri($"{_integrationApprovalHistoryUri}?{string.Join("&", pairs)}", UriKind.RelativeOrAbsolute);
    }

    private Uri BuildSessionSearchUri(SessionSearchQuery query)
    {
        var pairs = new List<string> { $"text={Uri.EscapeDataString(query.Text)}" };
        pairs.Add($"limit={Math.Clamp(query.Limit, 1, 200)}");
        pairs.Add($"snippetLength={Math.Clamp(query.SnippetLength, 40, 1000)}");
        if (!string.IsNullOrWhiteSpace(query.ChannelId))
            pairs.Add($"channelId={Uri.EscapeDataString(query.ChannelId)}");
        if (!string.IsNullOrWhiteSpace(query.SenderId))
            pairs.Add($"senderId={Uri.EscapeDataString(query.SenderId)}");
        if (query.FromUtc is { } fromUtc)
            pairs.Add($"fromUtc={Uri.EscapeDataString(fromUtc.ToString("O"))}");
        if (query.ToUtc is { } toUtc)
            pairs.Add($"toUtc={Uri.EscapeDataString(toUtc.ToString("O"))}");
        return new Uri($"{_integrationSessionSearchUri}?{string.Join("&", pairs)}", UriKind.RelativeOrAbsolute);
    }

    private Uri BuildProfileUri(string actorId)
    {
        if (string.IsNullOrWhiteSpace(actorId))
            throw new ArgumentException("Actor id is required.", nameof(actorId));

        return new Uri($"{_integrationProfilesUri.AbsoluteUri.TrimEnd('/')}/{Uri.EscapeDataString(actorId)}", UriKind.Absolute);
    }

    private Uri BuildIntegrationAccountUri(string accountId)
    {
        if (string.IsNullOrWhiteSpace(accountId))
            throw new ArgumentException("Account id is required.", nameof(accountId));

        return new Uri($"{_integrationAccountsUri.AbsoluteUri.TrimEnd('/')}/{Uri.EscapeDataString(accountId)}", UriKind.Absolute);
    }

    private Uri BuildIntegrationBackendUri(string backendId)
    {
        if (string.IsNullOrWhiteSpace(backendId))
            throw new ArgumentException("Backend id is required.", nameof(backendId));

        return new Uri($"{_integrationBackendsUri.AbsoluteUri.TrimEnd('/')}/{Uri.EscapeDataString(backendId)}", UriKind.Absolute);
    }

    private Uri BuildIntegrationBackendProbeUri(string backendId)
        => new($"{BuildIntegrationBackendUri(backendId).AbsoluteUri}/probe", UriKind.Absolute);

    private Uri BuildIntegrationBackendSessionsUri(string backendId)
        => new($"{BuildIntegrationBackendUri(backendId).AbsoluteUri}/sessions", UriKind.Absolute);

    private Uri BuildIntegrationBackendSessionUri(string backendId, string sessionId)
    {
        if (string.IsNullOrWhiteSpace(sessionId))
            throw new ArgumentException("Session id is required.", nameof(sessionId));

        return new Uri($"{BuildIntegrationBackendSessionsUri(backendId).AbsoluteUri}/{Uri.EscapeDataString(sessionId)}", UriKind.Absolute);
    }

    private Uri BuildIntegrationBackendInputUri(string backendId, string sessionId)
        => new($"{BuildIntegrationBackendSessionUri(backendId, sessionId).AbsoluteUri}/input", UriKind.Absolute);

    private Uri BuildIntegrationBackendEventsUri(string backendId, string sessionId, long afterSequence, int limit)
        => new($"{BuildIntegrationBackendSessionUri(backendId, sessionId).AbsoluteUri}/events?afterSequence={Math.Max(0, afterSequence)}&limit={Math.Clamp(limit, 1, 500)}", UriKind.Absolute);

    private Uri BuildIntegrationBackendEventStreamUri(string backendId, string sessionId, long afterSequence, int limit)
        => new($"{BuildIntegrationBackendSessionUri(backendId, sessionId).AbsoluteUri}/events/stream?afterSequence={Math.Max(0, afterSequence)}&limit={Math.Clamp(limit, 1, 500)}", UriKind.Absolute);

    private Uri BuildAutomationUri(string automationId)
    {
        if (string.IsNullOrWhiteSpace(automationId))
            throw new ArgumentException("Automation id is required.", nameof(automationId));

        return new Uri($"{_integrationAutomationsUri.AbsoluteUri.TrimEnd('/')}/{Uri.EscapeDataString(automationId)}", UriKind.Absolute);
    }

    private Uri BuildAutomationRunUri(string automationId)
        => new($"{BuildAutomationUri(automationId).AbsoluteUri}/run", UriKind.Absolute);

    private Uri BuildAdminAutomationUri(string automationId)
    {
        if (string.IsNullOrWhiteSpace(automationId))
            throw new ArgumentException("Automation id is required.", nameof(automationId));

        return new Uri($"{_adminAutomationsUri.AbsoluteUri.TrimEnd('/')}/{Uri.EscapeDataString(automationId)}", UriKind.Absolute);
    }

    private Uri BuildAdminAutomationRunUri(string automationId)
        => new($"{BuildAdminAutomationUri(automationId).AbsoluteUri}/run", UriKind.Absolute);

    private Uri BuildLearningProposalsUri(string? status, string? kind)
    {
        var pairs = new List<string>();
        if (!string.IsNullOrWhiteSpace(status))
            pairs.Add($"status={Uri.EscapeDataString(status)}");
        if (!string.IsNullOrWhiteSpace(kind))
            pairs.Add($"kind={Uri.EscapeDataString(kind)}");
        return pairs.Count == 0
            ? _adminLearningProposalsUri
            : new Uri($"{_adminLearningProposalsUri}?{string.Join("&", pairs)}", UriKind.RelativeOrAbsolute);
    }

    private Uri BuildLearningProposalActionUri(string proposalId, string action)
    {
        if (string.IsNullOrWhiteSpace(proposalId))
            throw new ArgumentException("Proposal id is required.", nameof(proposalId));

        return new Uri($"{_adminLearningProposalsUri.AbsoluteUri.TrimEnd('/')}/{Uri.EscapeDataString(proposalId)}/{action}", UriKind.Absolute);
    }

    private Uri BuildMemoryNotesUri(string? prefix, string? memoryClass, string? projectId, int limit)
    {
        var pairs = new List<string>
        {
            $"limit={Math.Clamp(limit, 1, 500)}"
        };

        if (!string.IsNullOrWhiteSpace(prefix))
            pairs.Add($"prefix={Uri.EscapeDataString(prefix)}");
        if (!string.IsNullOrWhiteSpace(memoryClass))
            pairs.Add($"memoryClass={Uri.EscapeDataString(memoryClass)}");
        if (!string.IsNullOrWhiteSpace(projectId))
            pairs.Add($"projectId={Uri.EscapeDataString(projectId)}");

        return new Uri($"{_adminMemoryNotesUri}?{string.Join("&", pairs)}", UriKind.RelativeOrAbsolute);
    }

    private Uri BuildMemorySearchUri(string query, string? memoryClass, string? projectId, int limit)
    {
        if (string.IsNullOrWhiteSpace(query))
            throw new ArgumentException("Query is required.", nameof(query));

        var pairs = new List<string>
        {
            $"query={Uri.EscapeDataString(query)}",
            $"limit={Math.Clamp(limit, 1, 50)}"
        };

        if (!string.IsNullOrWhiteSpace(memoryClass))
            pairs.Add($"memoryClass={Uri.EscapeDataString(memoryClass)}");
        if (!string.IsNullOrWhiteSpace(projectId))
            pairs.Add($"projectId={Uri.EscapeDataString(projectId)}");

        return new Uri($"{_adminMemorySearchUri}?{string.Join("&", pairs)}", UriKind.RelativeOrAbsolute);
    }

    private Uri BuildMemoryNoteUri(string key)
    {
        if (string.IsNullOrWhiteSpace(key))
            throw new ArgumentException("Memory note key is required.", nameof(key));

        return new Uri($"{_adminMemoryNotesUri.AbsoluteUri.TrimEnd('/')}/{Uri.EscapeDataString(key)}", UriKind.Absolute);
    }

    private Uri BuildMemoryExportUri(
        string? actorId,
        string? projectId,
        bool includeProfiles,
        bool includeProposals,
        bool includeAutomations,
        bool includeNotes)
    {
        var pairs = new List<string>
        {
            $"includeProfiles={includeProfiles.ToString().ToLowerInvariant()}",
            $"includeProposals={includeProposals.ToString().ToLowerInvariant()}",
            $"includeAutomations={includeAutomations.ToString().ToLowerInvariant()}",
            $"includeNotes={includeNotes.ToString().ToLowerInvariant()}"
        };

        if (!string.IsNullOrWhiteSpace(actorId))
            pairs.Add($"actorId={Uri.EscapeDataString(actorId)}");
        if (!string.IsNullOrWhiteSpace(projectId))
            pairs.Add($"projectId={Uri.EscapeDataString(projectId)}");

        return new Uri($"{_adminMemoryExportUri}?{string.Join("&", pairs)}", UriKind.RelativeOrAbsolute);
    }

    private Uri BuildOperatorAuditUri(OperatorAuditQuery query)
    {
        var pairs = new List<string>
        {
            $"limit={Math.Clamp(query.Limit, 1, 500)}"
        };

        if (!string.IsNullOrWhiteSpace(query.ActorId))
            pairs.Add($"actorId={Uri.EscapeDataString(query.ActorId)}");
        if (!string.IsNullOrWhiteSpace(query.ActionType))
            pairs.Add($"actionType={Uri.EscapeDataString(query.ActionType)}");
        if (!string.IsNullOrWhiteSpace(query.TargetId))
            pairs.Add($"targetId={Uri.EscapeDataString(query.TargetId)}");

        return new Uri($"{_integrationOperatorAuditUri}?{string.Join("&", pairs)}", UriKind.RelativeOrAbsolute);
    }

    private Uri BuildRuntimeEventsUri(RuntimeEventQuery query)
    {
        var pairs = new List<string>
        {
            $"limit={Math.Clamp(query.Limit, 1, 500)}"
        };

        if (!string.IsNullOrWhiteSpace(query.SessionId))
            pairs.Add($"sessionId={Uri.EscapeDataString(query.SessionId)}");
        if (!string.IsNullOrWhiteSpace(query.ChannelId))
            pairs.Add($"channelId={Uri.EscapeDataString(query.ChannelId)}");
        if (!string.IsNullOrWhiteSpace(query.SenderId))
            pairs.Add($"senderId={Uri.EscapeDataString(query.SenderId)}");
        if (!string.IsNullOrWhiteSpace(query.Component))
            pairs.Add($"component={Uri.EscapeDataString(query.Component)}");
        if (!string.IsNullOrWhiteSpace(query.Action))
            pairs.Add($"action={Uri.EscapeDataString(query.Action)}");

        return new Uri($"{_integrationRuntimeEventsUri}?{string.Join("&", pairs)}", UriKind.RelativeOrAbsolute);
    }

    private Uri BuildChannelAuthUri(string channelId, string? accountId)
    {
        if (string.IsNullOrWhiteSpace(channelId))
            throw new ArgumentException("Channel id is required.", nameof(channelId));

        var baseUri = new Uri(_adminWhatsAppSetupUri, $"/admin/channels/{Uri.EscapeDataString(channelId)}/auth");
        if (string.IsNullOrWhiteSpace(accountId))
            return baseUri;

        return new Uri($"{baseUri}?accountId={Uri.EscapeDataString(accountId)}", UriKind.Absolute);
    }

    private Uri BuildChannelAuthStreamUri(string channelId, string? accountId)
    {
        var baseUri = new Uri(_adminWhatsAppSetupUri, $"/admin/channels/{Uri.EscapeDataString(channelId)}/auth/stream");
        if (string.IsNullOrWhiteSpace(accountId))
            return baseUri;

        return new Uri($"{baseUri}?accountId={Uri.EscapeDataString(accountId)}", UriKind.Absolute);
    }

    private static HttpContent BuildJsonContent<T>(T request, JsonTypeInfo<T> jsonTypeInfo)
    {
        var json = JsonSerializer.Serialize(request, jsonTypeInfo);
        return new StringContent(json, Encoding.UTF8, "application/json");
    }

    private static void ApplyPresetHeader(HttpRequestMessage request, string? presetId)
    {
        if (!string.IsNullOrWhiteSpace(presetId))
            request.Headers.TryAddWithoutValidation("X-OpenClaw-Preset", presetId.Trim());
    }

    private static async Task<Exception> CreateHttpErrorAsync(HttpResponseMessage resp, CancellationToken cancellationToken)
    {
        string? body = null;
        try
        {
            body = await resp.Content.ReadAsStringAsync(cancellationToken);
        }
        catch
        {
        }

        var status = $"{(int)resp.StatusCode} {resp.ReasonPhrase}".Trim();
        if (string.IsNullOrWhiteSpace(body))
            return new HttpRequestException($"HTTP {status}");

        body = body.Trim();
        if (body.Length > 8000)
            body = body[..8000] + "…";

        return new HttpRequestException($"HTTP {status}\n{body}");
    }

    public void Dispose()
    {
        if (_ownsHttpClient)
            _http.Dispose();
    }
}

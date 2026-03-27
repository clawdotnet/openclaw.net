using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization.Metadata;
using OpenClaw.Core.Models;

namespace OpenClaw.Client;

public sealed class OpenClawHttpClient : IDisposable
{
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
    private readonly Uri _integrationSessionsUri;
    private readonly Uri _integrationRuntimeEventsUri;
    private readonly Uri _integrationMessagesUri;
    private readonly Uri _adminHeartbeatUri;
    private readonly Uri _adminHeartbeatPreviewUri;
    private readonly Uri _adminHeartbeatStatusUri;
    private readonly Uri _adminPostureUri;
    private readonly Uri _adminApprovalSimulationUri;
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

        _chatCompletionsUri = new Uri(baseUri, "/v1/chat/completions");
        _mcpUri = new Uri(baseUri, "/mcp");
        _integrationDashboardUri = new Uri(baseUri, "/api/integration/dashboard");
        _integrationStatusUri = new Uri(baseUri, "/api/integration/status");
        _integrationApprovalsUri = new Uri(baseUri, "/api/integration/approvals");
        _integrationApprovalHistoryUri = new Uri(baseUri, "/api/integration/approval-history");
        _integrationProvidersUri = new Uri(baseUri, "/api/integration/providers");
        _integrationPluginsUri = new Uri(baseUri, "/api/integration/plugins");
        _integrationOperatorAuditUri = new Uri(baseUri, "/api/integration/operator-audit");
        _integrationSessionsUri = new Uri(baseUri, "/api/integration/sessions");
        _integrationRuntimeEventsUri = new Uri(baseUri, "/api/integration/runtime-events");
        _integrationMessagesUri = new Uri(baseUri, "/api/integration/messages");
        _adminHeartbeatUri = new Uri(baseUri, "/admin/heartbeat");
        _adminHeartbeatPreviewUri = new Uri(baseUri, "/admin/heartbeat/preview");
        _adminHeartbeatStatusUri = new Uri(baseUri, "/admin/heartbeat/status");
        _adminPostureUri = new Uri(baseUri, "/admin/posture");
        _adminApprovalSimulationUri = new Uri(baseUri, "/admin/approvals/simulate");
        _adminIncidentExportUri = new Uri(baseUri, "/admin/incident/export");
        _adminWhatsAppSetupUri = new Uri(baseUri, "/admin/channels/whatsapp/setup");
        _adminWhatsAppRestartUri = new Uri(baseUri, "/admin/channels/whatsapp/restart");

        _http = httpClient ?? new HttpClient { Timeout = Timeout.InfiniteTimeSpan };
        _ownsHttpClient = httpClient is null;
        _http.DefaultRequestHeaders.UserAgent.ParseAdd("openclaw-client/1.0");

        if (!string.IsNullOrWhiteSpace(authToken))
            _http.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", authToken);
    }

    public async Task<OpenAiChatCompletionResponse> ChatCompletionAsync(
        OpenAiChatCompletionRequest request,
        CancellationToken cancellationToken)
    {
        using var req = new HttpRequestMessage(HttpMethod.Post, _chatCompletionsUri)
        {
            Content = BuildJsonContent(request, CoreJsonContext.Default.OpenAiChatCompletionRequest)
        };

        return await SendAsync(req, CoreJsonContext.Default.OpenAiChatCompletionResponse, cancellationToken);
    }

    public async Task<string> StreamChatCompletionAsync(
        OpenAiChatCompletionRequest request,
        Action<string> onText,
        CancellationToken cancellationToken)
    {
        using var req = new HttpRequestMessage(HttpMethod.Post, _chatCompletionsUri)
        {
            Content = BuildJsonContent(request, CoreJsonContext.Default.OpenAiChatCompletionRequest)
        };
        req.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("text/event-stream"));

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

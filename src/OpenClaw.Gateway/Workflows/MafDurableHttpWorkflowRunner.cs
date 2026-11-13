using System.Collections.Concurrent;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization.Metadata;
using OpenClaw.Core.Abstractions;
using OpenClaw.Core.Http;
using OpenClaw.Core.Models;
using OpenClaw.Core.Security;

namespace OpenClaw.Gateway.Workflows;

internal sealed class MafDurableHttpWorkflowRunner : IAgentWorkflowRunner, IDisposable
{
    private readonly WorkflowBackendConfig _config;
    private readonly RuntimeEventStore _events;
    private readonly ILogger<MafDurableHttpWorkflowRunner> _logger;
    private readonly HttpClient _http;
    private readonly string? _apiToken;
    private readonly IWorkflowAccessTokenProvider? _tokenProvider;
    private readonly TimeProvider _timeProvider;
    private bool IsDelegated => !string.Equals(_config.Authentication.Mode, "static", StringComparison.OrdinalIgnoreCase);
    private readonly ConcurrentDictionary<string, string> _lastRecordedStatuses = new(StringComparer.Ordinal);
    private bool _disposed;

    public MafDurableHttpWorkflowRunner(
        string backendId,
        WorkflowBackendConfig config,
        RuntimeEventStore events,
        ILogger<MafDurableHttpWorkflowRunner> logger)
        : this(backendId, config, events, logger, null,
            HttpClientFactory.Create(allowAutoRedirect: false), TimeProvider.System)
    {
    }

    public MafDurableHttpWorkflowRunner(
        string backendId,
        WorkflowBackendConfig config,
        RuntimeEventStore events,
        ILogger<MafDurableHttpWorkflowRunner> logger,
        IWorkflowAccessTokenProvider? tokenProvider,
        HttpClient httpClient,
        TimeProvider timeProvider)
    {
        BackendId = backendId;
        WorkflowId = string.IsNullOrWhiteSpace(config.WorkflowName) ? backendId : config.WorkflowName.Trim();
        _config = config;
        _events = events;
        _logger = logger;
        _tokenProvider = tokenProvider;
        _timeProvider = timeProvider;
        _apiToken = IsDelegated ? null : SecretResolver.Resolve(config.ApiTokenSecret, logger);
        if (IsDelegated && tokenProvider is null)
            throw new InvalidOperationException("A delegated workflow backend requires a token provider.");
        _http = httpClient;
        _http.Timeout = TimeSpan.FromSeconds(Math.Clamp(config.TimeoutSeconds, 5, 3600));
        _http.BaseAddress = BuildBaseAddress(config.BaseUrl);
    }

    public string BackendId { get; }

    public string WorkflowId { get; }

    public AgentWorkflowBackendSummary GetSummary()
        => new()
        {
            Id = BackendId,
            Kind = AgentWorkflowBackendKinds.MafDurableHttp,
            WorkflowName = WorkflowId,
            DisplayName = _config.DisplayName,
            Enabled = _config.Enabled
        };

    public Task<AgentWorkflowRunResult> RunAsync(
        AgentWorkflowRequest request,
        CancellationToken cancellationToken = default)
        => RunAsync(request, null, cancellationToken);

    public async Task<AgentWorkflowRunResult> RunAsync(
        AgentWorkflowRequest request,
        WorkflowExecutionContext? executionContext,
        CancellationToken cancellationToken)
    {
        var backendRequest = WithBackendMetadata(request, executionContext);
        var result = NormalizeRunResult(
            await SendAsync(
                HttpMethod.Post,
                $"api/workflows/{Uri.EscapeDataString(WorkflowId)}/run",
                backendRequest,
                CoreJsonContext.Default.AgentWorkflowRequest,
                CoreJsonContext.Default.AgentWorkflowRunResult,
                executionContext,
                cancellationToken));

        RecordEvent(result.RunId, "run_started", result.Status, $"Workflow '{WorkflowId}' started.");
        RecordStatus(result.RunId, result.Status, result.Events);
        return result;
    }

    public Task<AgentWorkflowRunSnapshot> GetAsync(
        string runId,
        CancellationToken cancellationToken = default)
        => GetAsync(runId, null, cancellationToken);

    public async Task<AgentWorkflowRunSnapshot> GetAsync(
        string runId,
        WorkflowExecutionContext? executionContext,
        CancellationToken cancellationToken)
    {
        var snapshot = NormalizeSnapshot(
            await SendAsync(
                HttpMethod.Get,
                $"api/workflows/{Uri.EscapeDataString(WorkflowId)}/status/{Uri.EscapeDataString(runId)}",
                CoreJsonContext.Default.AgentWorkflowRunSnapshot,
                executionContext,
                cancellationToken));

        RecordStatus(snapshot.RunId, snapshot.Status, snapshot.Events);
        return snapshot;
    }

    public Task<AgentWorkflowRunSnapshot> RespondAsync(
        string runId,
        AgentWorkflowResponse response,
        CancellationToken cancellationToken = default)
        => RespondAsync(runId, response, null, cancellationToken);

    public async Task<AgentWorkflowRunSnapshot> RespondAsync(
        string runId,
        AgentWorkflowResponse response,
        WorkflowExecutionContext? executionContext,
        CancellationToken cancellationToken)
    {
        var backendResponse = IsDelegated && executionContext is not null
            ? new AgentWorkflowResponse
            {
                PortId = response.PortId, Payload = response.Payload, Approved = response.Approved,
                Comment = response.Comment, ActorId = executionContext.Subject, Metadata = response.Metadata
            }
            : response;
        var snapshot = NormalizeSnapshot(
            await SendAsync(
                HttpMethod.Post,
                $"api/workflows/{Uri.EscapeDataString(WorkflowId)}/respond/{Uri.EscapeDataString(runId)}",
                backendResponse,
                CoreJsonContext.Default.AgentWorkflowResponse,
                CoreJsonContext.Default.AgentWorkflowRunSnapshot,
                executionContext,
                cancellationToken));

        RecordEvent(runId, "response_sent", snapshot.Status, $"Response sent to workflow '{WorkflowId}' port '{response.PortId}'.");
        RecordStatus(snapshot.RunId, snapshot.Status, snapshot.Events);
        return snapshot;
    }

    public IAsyncEnumerable<AgentWorkflowEvent> StreamAsync(
        string runId,
        CancellationToken cancellationToken = default)
        => StreamAsync(runId, null, cancellationToken);

    public async IAsyncEnumerable<AgentWorkflowEvent> StreamAsync(
        string runId,
        WorkflowExecutionContext? executionContext,
        [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken)
    {
        var seen = new HashSet<string>(StringComparer.Ordinal);
        string? lastStatus = null;
        var delay = TimeSpan.FromSeconds(Math.Clamp(_config.PollIntervalSeconds, 1, 60));

        while (true)
        {
            var snapshot = await GetAsync(runId, executionContext, cancellationToken);
            foreach (var evt in snapshot.Events.Where(evt => seen.Add(evt.Id)))
                yield return evt;

            if (!string.Equals(lastStatus, snapshot.Status, StringComparison.Ordinal))
            {
                lastStatus = snapshot.Status;
                yield return new AgentWorkflowEvent
                {
                    Id = $"evt_{Guid.NewGuid():N}"[..20],
                    WorkflowId = WorkflowId,
                    RunId = runId,
                    Type = "status",
                    Status = snapshot.Status,
                    Summary = $"Workflow '{WorkflowId}' status: {snapshot.Status}."
                };
            }

            if (IsTerminal(snapshot.Status))
                yield break;

            await Task.Delay(delay, _timeProvider, cancellationToken);
        }
    }

    public void Dispose()
    {
        if (_disposed)
            return;

        _http.Dispose();
        _disposed = true;
    }

    private Task<TResponse> SendAsync<TResponse>(
        HttpMethod method,
        string path,
        JsonTypeInfo<TResponse> responseTypeInfo,
        WorkflowExecutionContext? executionContext,
        CancellationToken cancellationToken)
        => SendAsync<object, TResponse>(
            method,
            path,
            payload: null,
            requestTypeInfo: null,
            responseTypeInfo,
            executionContext,
            cancellationToken);

    private async Task<TResponse> SendAsync<TRequest, TResponse>(
        HttpMethod method,
        string path,
        TRequest? payload,
        JsonTypeInfo<TRequest>? requestTypeInfo,
        JsonTypeInfo<TResponse> responseTypeInfo,
        WorkflowExecutionContext? executionContext,
        CancellationToken cancellationToken)
    {
        using var request = new HttpRequestMessage(method, path);
        if (IsDelegated)
        {
            ValidateCaller(executionContext);
            var token = await _tokenProvider!.GetAsync(BackendId, _config.Authentication, executionContext!, cancellationToken);
            ValidateCaller(executionContext);
            if (_timeProvider.GetUtcNow() >= token.ExpiresAtUtc)
                throw new WorkflowDelegationException("workflow_token_exchange_failed", StatusCodes.Status502BadGateway);
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token.AccessToken);
        }
        else if (!string.IsNullOrWhiteSpace(_apiToken))
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", _apiToken);

        if (payload is not null && requestTypeInfo is not null)
        {
            var json = JsonSerializer.Serialize(payload, requestTypeInfo);
            request.Content = new StringContent(json, Encoding.UTF8, "application/json");
        }

        using var response = await _http.SendAsync(
            request,
            HttpCompletionOption.ResponseHeadersRead,
            cancellationToken);
        if (!response.IsSuccessStatusCode)
        {
            _logger.LogWarning(
                "Workflow backend {BackendId} returned HTTP {StatusCode}.",
                BackendId,
                (int)response.StatusCode);
            if (IsDelegated && response.StatusCode is System.Net.HttpStatusCode.Unauthorized or System.Net.HttpStatusCode.Forbidden)
                throw new WorkflowDelegationException("workflow_backend_auth_denied", (int)response.StatusCode);
            throw new InvalidOperationException(
                $"Workflow backend '{BackendId}' returned HTTP {(int)response.StatusCode}.");
        }

        await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken);
        var parsed = await JsonSerializer.DeserializeAsync(stream, responseTypeInfo, cancellationToken);
        return parsed ?? throw new InvalidOperationException($"Workflow backend '{BackendId}' returned an empty response.");
    }

    private void ValidateCaller(WorkflowExecutionContext? caller)
    {
        if (caller is null || !string.Equals(caller.BackendId, BackendId, StringComparison.OrdinalIgnoreCase) ||
            caller.ExpiresAtUtc <= _timeProvider.GetUtcNow())
        {
            _logger.LogWarning("Workflow backend {BackendId} requires a valid delegated caller.", BackendId);
            throw new WorkflowDelegationException("workflow_user_auth_required", StatusCodes.Status401Unauthorized);
        }
    }

    private AgentWorkflowRequest WithBackendMetadata(AgentWorkflowRequest request, WorkflowExecutionContext? executionContext)
    {
        var metadata = request.Metadata is null
            ? new Dictionary<string, string>(StringComparer.Ordinal)
            : new Dictionary<string, string>(request.Metadata, StringComparer.Ordinal);
        metadata["backendId"] = BackendId;

        return new AgentWorkflowRequest
        {
            Input = request.Input,
            Payload = request.Payload,
            ChannelId = request.ChannelId,
            SenderId = IsDelegated && executionContext is not null ? executionContext.Subject : request.SenderId,
            SessionId = request.SessionId,
            Metadata = metadata
        };
    }

    private AgentWorkflowRunResult NormalizeRunResult(AgentWorkflowRunResult result)
        => new()
        {
            WorkflowId = string.IsNullOrWhiteSpace(result.WorkflowId) ? WorkflowId : result.WorkflowId,
            BackendId = string.IsNullOrWhiteSpace(result.BackendId) ? BackendId : result.BackendId,
            RunId = result.RunId,
            Status = NormalizeStatus(result.Status),
            Output = result.Output,
            OutputPayload = result.OutputPayload,
            Error = result.Error,
            Events = result.Events,
            Metadata = result.Metadata
        };

    private AgentWorkflowRunSnapshot NormalizeSnapshot(AgentWorkflowRunSnapshot snapshot)
        => new()
        {
            WorkflowId = string.IsNullOrWhiteSpace(snapshot.WorkflowId) ? WorkflowId : snapshot.WorkflowId,
            BackendId = string.IsNullOrWhiteSpace(snapshot.BackendId) ? BackendId : snapshot.BackendId,
            RunId = snapshot.RunId,
            Status = NormalizeStatus(snapshot.Status),
            Output = snapshot.Output,
            OutputPayload = snapshot.OutputPayload,
            Error = snapshot.Error,
            PendingInputs = snapshot.PendingInputs,
            Events = snapshot.Events,
            Metadata = snapshot.Metadata
        };

    private void RecordStatus(string runId, string status, IReadOnlyList<AgentWorkflowEvent> workflowEvents)
    {
        if (!_lastRecordedStatuses.TryAdd(runId, status) &&
            string.Equals(_lastRecordedStatuses[runId], status, StringComparison.Ordinal))
            return;

        _lastRecordedStatuses[runId] = status;
        var action = status switch
        {
            AgentWorkflowStatuses.WaitingForInput => "waiting_for_input",
            AgentWorkflowStatuses.Completed => "completed",
            AgentWorkflowStatuses.Failed => "failed",
            AgentWorkflowStatuses.Cancelled => "cancelled",
            _ => null
        };

        if (action is null)
            return;

        var matchingEvent = workflowEvents.LastOrDefault(evt =>
            string.Equals(evt.Status, status, StringComparison.OrdinalIgnoreCase) ||
            string.Equals(evt.Type, action, StringComparison.OrdinalIgnoreCase));
        RecordEvent(
            runId,
            action,
            status,
            string.IsNullOrWhiteSpace(matchingEvent?.Summary)
                ? $"Workflow '{WorkflowId}' status changed to {status}."
                : matchingEvent.Summary);
    }

    private void RecordEvent(string runId, string action, string status, string summary)
    {
        _events.Append(new RuntimeEventEntry
        {
            Id = $"evt_{Guid.NewGuid():N}"[..20],
            Component = "workflow",
            Action = action,
            Severity = status is AgentWorkflowStatuses.Failed or AgentWorkflowStatuses.Cancelled ? "warning" : "info",
            Summary = summary,
            Metadata = new Dictionary<string, string>
            {
                ["backendId"] = BackendId,
                ["workflowId"] = WorkflowId,
                ["runId"] = runId,
                ["status"] = status
            }
        });
    }

    private static Uri BuildBaseAddress(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
            throw new InvalidOperationException("Workflow backend BaseUrl is required.");

        var baseUrl = value.Trim();
        if (!baseUrl.EndsWith('/'))
            baseUrl += "/";

        return new Uri(baseUrl, UriKind.Absolute);
    }

    private static string NormalizeStatus(string? status)
        => string.IsNullOrWhiteSpace(status)
            ? AgentWorkflowStatuses.Running
            : status.Trim().ToLowerInvariant();

    private static bool IsTerminal(string status)
        => status is AgentWorkflowStatuses.Completed or AgentWorkflowStatuses.Failed or AgentWorkflowStatuses.Cancelled;
}

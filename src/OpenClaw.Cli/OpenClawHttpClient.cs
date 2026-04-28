using OpenClaw.Core.Models;

namespace OpenClaw.Cli;

internal sealed class OpenClawHttpClient : IDisposable
{
    private readonly OpenClaw.Client.OpenClawHttpClient _inner;

    public OpenClawHttpClient(string baseUrl, string? authToken)
        => _inner = new OpenClaw.Client.OpenClawHttpClient(baseUrl, authToken);

    public async Task<OpenAiChatCompletionResponse> ChatCompletionAsync(
        OpenAiChatCompletionRequest request,
        CancellationToken cancellationToken,
        string? presetId = null)
        => await _inner.ChatCompletionAsync(request, cancellationToken, presetId);

    public async Task<string> StreamChatCompletionAsync(
        OpenAiChatCompletionRequest request,
        Action<string> onText,
        CancellationToken cancellationToken,
        string? presetId = null)
        => await _inner.StreamChatCompletionAsync(request, onText, cancellationToken, presetId);

    public Task<HeartbeatPreviewResponse> GetHeartbeatAsync(CancellationToken cancellationToken)
        => _inner.GetHeartbeatAsync(cancellationToken);

    public Task<HeartbeatPreviewResponse> PreviewHeartbeatAsync(HeartbeatConfigDto request, CancellationToken cancellationToken)
        => _inner.PreviewHeartbeatAsync(request, cancellationToken);

    public Task<HeartbeatPreviewResponse> SaveHeartbeatAsync(HeartbeatConfigDto request, CancellationToken cancellationToken)
        => _inner.SaveHeartbeatAsync(request, cancellationToken);

    public Task<HeartbeatStatusResponse> GetHeartbeatStatusAsync(CancellationToken cancellationToken)
        => _inner.GetHeartbeatStatusAsync(cancellationToken);

    public Task<SecurityPostureResponse> GetSecurityPostureAsync(CancellationToken cancellationToken)
        => _inner.GetSecurityPostureAsync(cancellationToken);

    public Task<OperatorInsightsResponse> GetOperatorInsightsAsync(DateTimeOffset? fromUtc, DateTimeOffset? toUtc, CancellationToken cancellationToken)
        => _inner.GetOperatorInsightsAsync(fromUtc, toUtc, cancellationToken);

    public Task<ModelProfilesStatusResponse> GetModelProfilesAsync(CancellationToken cancellationToken)
        => _inner.GetModelProfilesAsync(cancellationToken);

    public Task<ModelSelectionDoctorResponse> GetModelSelectionDoctorAsync(CancellationToken cancellationToken)
        => _inner.GetModelSelectionDoctorAsync(cancellationToken);

    public Task<ModelEvaluationReport> RunModelEvaluationAsync(ModelEvaluationRequest request, CancellationToken cancellationToken)
        => _inner.RunModelEvaluationAsync(request, cancellationToken);

    public Task<IntegrationAccountsResponse> GetIntegrationAccountsAsync(CancellationToken cancellationToken)
        => _inner.GetIntegrationAccountsAsync(cancellationToken);

    public Task<IntegrationConnectedAccountResponse> GetIntegrationAccountAsync(string accountId, CancellationToken cancellationToken)
        => _inner.GetIntegrationAccountAsync(accountId, cancellationToken);

    public Task<IntegrationConnectedAccountResponse> CreateIntegrationAccountAsync(ConnectedAccountCreateRequest request, CancellationToken cancellationToken)
        => _inner.CreateIntegrationAccountAsync(request, cancellationToken);

    public Task<OperationStatusResponse> DeleteIntegrationAccountAsync(string accountId, CancellationToken cancellationToken)
        => _inner.DeleteIntegrationAccountAsync(accountId, cancellationToken);

    public Task<BackendCredentialResolutionResponse> TestAccountResolutionAsync(BackendCredentialResolutionRequest request, CancellationToken cancellationToken)
        => _inner.TestAccountResolutionAsync(request, cancellationToken);

    public Task<IntegrationBackendsResponse> GetIntegrationBackendsAsync(CancellationToken cancellationToken)
        => _inner.GetIntegrationBackendsAsync(cancellationToken);

    public Task<BackendProbeResult> ProbeIntegrationBackendAsync(string backendId, BackendProbeRequest request, CancellationToken cancellationToken)
        => _inner.ProbeIntegrationBackendAsync(backendId, request, cancellationToken);

    public Task<IntegrationBackendSessionResponse> StartBackendSessionAsync(string backendId, StartBackendSessionRequest request, CancellationToken cancellationToken)
        => _inner.StartBackendSessionAsync(backendId, request, cancellationToken);

    public Task<IntegrationBackendSessionResponse> SendBackendInputAsync(string backendId, string sessionId, BackendInput input, CancellationToken cancellationToken)
        => _inner.SendBackendInputAsync(backendId, sessionId, input, cancellationToken);

    public Task<OperationStatusResponse> StopBackendSessionAsync(string backendId, string sessionId, CancellationToken cancellationToken)
        => _inner.StopBackendSessionAsync(backendId, sessionId, cancellationToken);

    public Task<IntegrationBackendSessionResponse> GetBackendSessionAsync(string backendId, string sessionId, CancellationToken cancellationToken)
        => _inner.GetBackendSessionAsync(backendId, sessionId, cancellationToken);

    public Task<IntegrationBackendEventsResponse> GetBackendEventsAsync(string backendId, string sessionId, long afterSequence, int limit, CancellationToken cancellationToken)
        => _inner.GetBackendEventsAsync(backendId, sessionId, afterSequence, limit, cancellationToken);

    public Task StreamBackendEventsAsync(string backendId, string sessionId, long afterSequence, int limit, Action<BackendEvent> onEvent, CancellationToken cancellationToken)
        => _inner.StreamBackendEventsAsync(backendId, sessionId, afterSequence, limit, onEvent, cancellationToken);

    public Task<ApprovalSimulationResponse> SimulateApprovalAsync(ApprovalSimulationRequest request, CancellationToken cancellationToken)
        => _inner.SimulateApprovalAsync(request, cancellationToken);

    public Task<IncidentBundleResponse> ExportIncidentBundleAsync(int approvalLimit, int eventLimit, CancellationToken cancellationToken)
        => _inner.ExportIncidentBundleAsync(approvalLimit, eventLimit, cancellationToken);

    public Task<string> ExportTrajectoryJsonlAsync(DateTimeOffset? fromUtc, DateTimeOffset? toUtc, string? sessionId, bool anonymize, CancellationToken cancellationToken)
        => _inner.ExportTrajectoryJsonlAsync(fromUtc, toUtc, sessionId, anonymize, cancellationToken);

    public void Dispose() => _inner.Dispose();
}

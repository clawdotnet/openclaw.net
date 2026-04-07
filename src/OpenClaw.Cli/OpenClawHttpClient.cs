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

    public Task<ModelProfilesStatusResponse> GetModelProfilesAsync(CancellationToken cancellationToken)
        => _inner.GetModelProfilesAsync(cancellationToken);

    public Task<ModelSelectionDoctorResponse> GetModelSelectionDoctorAsync(CancellationToken cancellationToken)
        => _inner.GetModelSelectionDoctorAsync(cancellationToken);

    public Task<ModelEvaluationReport> RunModelEvaluationAsync(ModelEvaluationRequest request, CancellationToken cancellationToken)
        => _inner.RunModelEvaluationAsync(request, cancellationToken);

    public Task<ApprovalSimulationResponse> SimulateApprovalAsync(ApprovalSimulationRequest request, CancellationToken cancellationToken)
        => _inner.SimulateApprovalAsync(request, cancellationToken);

    public Task<IncidentBundleResponse> ExportIncidentBundleAsync(int approvalLimit, int eventLimit, CancellationToken cancellationToken)
        => _inner.ExportIncidentBundleAsync(approvalLimit, eventLimit, cancellationToken);

    public void Dispose() => _inner.Dispose();
}

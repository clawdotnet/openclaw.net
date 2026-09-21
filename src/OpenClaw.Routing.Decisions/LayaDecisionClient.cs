using System.Net.Http.Headers;
using System.Text.Json;
using OpenClaw.Core.Models;
using OpenClaw.Core.Validation;

namespace OpenClaw.Routing.Decisions;

/// <summary>Local-only Laya transport. No credentials or hosted fallback.</summary>
public sealed class LayaDecisionClient : IDecisionClient, IDisposable
{
    private readonly HttpClient _http;
    private readonly LayaRoutingConfig _config;
    private readonly Uri _endpoint;

    public LayaDecisionClient(HttpClient http, LayaRoutingConfig config)
    {
        _endpoint = DecisionRoutingConfiguration.Validate(config);
        _http = http;
        _config = config;
    }

    public async Task<DecisionResponse> EvaluateAsync(DecisionRequest request, CancellationToken cancellationToken)
    {
        using var message = new HttpRequestMessage(HttpMethod.Post, _endpoint);
        // Materialize the bounded payload so Content-Length is known; the local
        // service deliberately rejects chunked bodies before reading request data.
        message.Content = new ByteArrayContent(JsonSerializer.SerializeToUtf8Bytes(new LayaWireRequest(request.Model, request.State, request.Questions,
            request.RubricVersion, string.IsNullOrWhiteSpace(_config.Language) ? null : _config.Language),
            DecisionJsonContext.Default.LayaWireRequest));
        message.Content.Headers.ContentType = new MediaTypeHeaderValue("application/json");
        var result = await DecisionHttpTransport.SendAsync(_http, message, request, cancellationToken);
        var metadata = result.Metadata;
        if (result.Model != _config.Model || metadata is null || metadata.Revision != _config.Model[5..] ||
            metadata.Checkpoint is not ("english" or "multilingual" or "typed-decisions") ||
            metadata.RubricVersion != request.RubricVersion || !DecisionRoutingConfiguration.IsHex(metadata.SchemaHash, 64) ||
            metadata.SdkVersion != "0.3.4" || metadata.Device is not ("cpu" or "cuda" or "mps"))
            throw new DecisionException("laya_metadata_mismatch");
        if (metadata.Truncated)
            throw new DecisionException("laya_truncated_input");
        if (metadata.CalibrationId != "uncalibrated" && !DecisionRoutingConfiguration.IsHex(metadata.CalibrationId, 64))
            throw new DecisionException("laya_calibration_mismatch");
        if (!string.IsNullOrWhiteSpace(_config.CalibrationId) && metadata.CalibrationId != _config.CalibrationId)
            throw new DecisionException("laya_calibration_mismatch");
        return result;
    }

    public void Dispose() => _http.Dispose();
}

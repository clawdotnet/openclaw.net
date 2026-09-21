using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;

namespace OpenClaw.Routing.Decisions;

/// <summary>AOT-safe HTTP transport. The caller owns deadlines and fallback, so requests are not retried.</summary>
public sealed class TypeSafeDecisionClient(
    HttpClient httpClient,
    Uri endpoint,
    Func<CancellationToken, ValueTask<string?>> resolveApiKey) : IDecisionClient, IDisposable
{
    public async Task<DecisionResponse> EvaluateAsync(DecisionRequest request, CancellationToken cancellationToken)
    {
        var apiKey = await resolveApiKey(cancellationToken);
        if (string.IsNullOrWhiteSpace(apiKey))
            throw new DecisionException("missing_api_key");

        using var message = new HttpRequestMessage(HttpMethod.Post, endpoint);
        message.Headers.Authorization = new AuthenticationHeaderValue("Bearer", apiKey);
        message.Content = JsonContent.Create(request, DecisionJsonContext.Default.DecisionRequest);
        return await DecisionHttpTransport.SendAsync(httpClient, message, request, cancellationToken);
    }

    public void Dispose() => httpClient.Dispose();
}

using System.Net.Http.Headers;
using ModelContextProtocol;
using ModelContextProtocol.Client;

namespace OpenClaw.Gateway.Mcp;

public sealed class McpDelegatedHttpClientFactory
{
    public async Task<McpClient> CreateAsync(
        string endpointId,
        Uri endpoint,
        IReadOnlyDictionary<string, string> headers,
        int requestTimeoutSeconds,
        string delegatedAccessToken,
        CancellationToken ct)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(endpointId);
        ArgumentNullException.ThrowIfNull(endpoint);
        ArgumentNullException.ThrowIfNull(headers);
        ArgumentException.ThrowIfNullOrWhiteSpace(delegatedAccessToken);
        if (requestTimeoutSeconds <= 0)
            throw new ArgumentOutOfRangeException(nameof(requestTimeoutSeconds), "Request timeout must be positive.");
        if (endpoint.Scheme != Uri.UriSchemeHttps &&
            (endpoint.Scheme != Uri.UriSchemeHttp || !endpoint.IsLoopback))
        {
            throw new ArgumentException("MCP delegated endpoints must use HTTPS unless they use loopback HTTP.", nameof(endpoint));
        }

        var staticHeaders = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var (name, value) in headers)
        {
            if (!string.Equals(name, "Authorization", StringComparison.OrdinalIgnoreCase))
                staticHeaders[name] = value;
        }

        var httpClient = new HttpClient(new HttpClientHandler { AllowAutoRedirect = false })
        {
            Timeout = TimeSpan.FromSeconds(requestTimeoutSeconds)
        };
        httpClient.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", delegatedAccessToken);

        var transport = new HttpClientTransport(
            new HttpClientTransportOptions
            {
                Endpoint = endpoint,
                AdditionalHeaders = staticHeaders,
                TransportMode = HttpTransportMode.StreamableHttp,
                Name = endpointId
            },
            httpClient,
            ownsHttpClient: true);

        try
        {
            return await McpClient.CreateAsync(transport, cancellationToken: ct).ConfigureAwait(false);
        }
        catch
        {
            await transport.DisposeAsync().ConfigureAwait(false);
            throw;
        }
    }
}
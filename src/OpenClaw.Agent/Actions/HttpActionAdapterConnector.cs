using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using OpenClaw.Core.Models;

namespace OpenClaw.Agent.Actions;

internal sealed class HttpActionAdapterConnector : IActionAdapterConnector
{
    private readonly ActionAdapterConfig _config;
    private readonly HttpClient _httpClient;
    private readonly ILogger _logger;

    public HttpActionAdapterConnector(
        IOptions<ActionAdapterConfig> config,
        HttpClient httpClient,
        ILogger<HttpActionAdapterConnector> logger)
    {
        _config = config?.Value ?? new ActionAdapterConfig();
        _httpClient = httpClient ?? throw new ArgumentNullException(nameof(httpClient));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
    }

    public async ValueTask<ActionAdapterStepResult> InvokeAsync(
        ActionCall step, CancellationToken cancellationToken)
    {
        if (step is null)
            return ActionAdapterStepResult.Failure("invalid_step");

        var call = step.Call;
        if (string.IsNullOrWhiteSpace(call))
            return ActionAdapterStepResult.Failure("invalid_step");

        var dotIndex = call.IndexOf('.');
        if (dotIndex <= 0 || dotIndex >= call.Length - 1)
            return ActionAdapterStepResult.Failure("invalid_call_format");

        var system = call[..dotIndex];
        var operation = call[(dotIndex + 1)..];

        if (!_config.Connectors.TryGetValue(system, out var connectorDef))
            return ActionAdapterStepResult.Failure("connector_not_found");

        if (!IsAllowedCall(connectorDef, operation))
        {
            _logger.LogWarning("Connector call '{Call}' blocked — not in AllowedCalls whitelist", call);
            return ActionAdapterStepResult.Failure("connector_error");
        }

        if (HasBlockedKeywords(call))
        {
            _logger.LogWarning("Connector call '{Call}' blocked — contains blocked keyword", call);
            return ActionAdapterStepResult.Failure("connector_error");
        }

        try
        {
            var url = BuildUrl(connectorDef.BaseUrl, operation);
            using var request = new HttpRequestMessage(HttpMethod.Post, url);

            var contentJson = SerializeArgs(step.Args);
            request.Content = new StringContent(contentJson, Encoding.UTF8, "application/json");

            ApplyAuth(request, connectorDef.Auth);
            ApplyCustomHeaders(request, connectorDef.Headers);

            using var cts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            cts.CancelAfter(TimeSpan.FromSeconds(connectorDef.TimeoutSeconds));

            using var response = await _httpClient.SendAsync(request, cts.Token).ConfigureAwait(false);

            if (response.IsSuccessStatusCode)
                return ActionAdapterStepResult.Succeeded();

            var bodySummary = await ReadBodySummaryAsync(response, cts.Token).ConfigureAwait(false);
            _logger.LogWarning(
                "Connector call '{Call}' returned HTTP {StatusCode}: {BodySummary}",
                call, (int)response.StatusCode, bodySummary);

            return ActionAdapterStepResult.Failure("connector_error");
        }
        catch (TaskCanceledException)
        {
            _logger.LogWarning("Connector call '{Call}' timed out", call);
            return ActionAdapterStepResult.Failure("connector_unavailable");
        }
        catch (HttpRequestException ex)
        {
            _logger.LogWarning(ex, "Connector call '{Call}' failed with HTTP error", call);
            return ActionAdapterStepResult.Failure("connector_unavailable");
        }
    }

    private static bool IsAllowedCall(ConnectorDefinition def, string operation)
    {
        if (def.AllowedCalls is { Length: 0 })
            return false;

        foreach (var allowed in def.AllowedCalls)
        {
            if (string.Equals(allowed, operation, StringComparison.OrdinalIgnoreCase))
                return true;
        }

        return false;
    }

    private static bool HasBlockedKeywords(string call)
    {
        var lower = call.ToLowerInvariant();
        return lower.Contains("sql", StringComparison.Ordinal)
               && (lower.Contains(".sql", StringComparison.Ordinal)
                   || lower.Contains("sql.", StringComparison.Ordinal)
                   || lower.Contains("db.", StringComparison.Ordinal)
                   || lower.Contains("database.", StringComparison.Ordinal));
    }

    private static string BuildUrl(string baseUrl, string operation)
    {
        var baseUri = baseUrl.EndsWith('/') ? baseUrl[..^1] : baseUrl;
        var op = operation.StartsWith('/') ? operation : "/" + operation;
        return baseUri + op;
    }

    private static void ApplyAuth(HttpRequestMessage request, ConnectorAuthConfig auth)
    {
        if (string.IsNullOrWhiteSpace(auth.TokenEnv))
            return;

        var token = Environment.GetEnvironmentVariable(auth.TokenEnv)?.Trim();
        if (string.IsNullOrWhiteSpace(token))
            return;

        var authType = (auth.Type ?? "None").Trim();
        if (string.Equals(authType, "Bearer", StringComparison.OrdinalIgnoreCase))
        {
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        }
        else if (string.Equals(authType, "ApiKey", StringComparison.OrdinalIgnoreCase))
        {
            var headerName = string.IsNullOrWhiteSpace(auth.HeaderName) ? "X-API-Key" : auth.HeaderName.Trim();
            request.Headers.TryAddWithoutValidation(headerName, token);
        }
    }

    private static void ApplyCustomHeaders(HttpRequestMessage request, Dictionary<string, string>? headers)
    {
        if (headers is null)
            return;

        foreach (var pair in headers)
        {
            if (!string.IsNullOrWhiteSpace(pair.Key))
                request.Headers.TryAddWithoutValidation(pair.Key.Trim(), pair.Value);
        }
    }

    private static async Task<string> ReadBodySummaryAsync(
        HttpResponseMessage response, CancellationToken ct)
    {
        try
        {
            var body = await response.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
            return body.Length <= 256 ? body : body[..256] + "…";
        }
        catch
        {
            return "<unreadable>";
        }
    }

    private static string SerializeArgs(IReadOnlyDictionary<string, JsonElement> args)
    {
        if (args is not { Count: > 0 })
            return "{}";

        using var stream = new MemoryStream();
        using var writer = new Utf8JsonWriter(stream);
        writer.WriteStartObject();
        foreach (var pair in args)
        {
            writer.WritePropertyName(pair.Key);
            pair.Value.WriteTo(writer);
        }
        writer.WriteEndObject();
        writer.Flush();
        return Encoding.UTF8.GetString(stream.ToArray());
    }
}
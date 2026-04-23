using System.Net;
using System.Net.Http.Headers;
using System.Text;
using OpenClaw.Core.Http;
using OpenClaw.Core.Models;
using OpenClaw.Core.Security;

namespace OpenClaw.Core.Validation;

public sealed class ProviderSmokeProbeResult
{
    public string Status { get; init; } = SetupCheckStates.Skip;
    public string Summary { get; init; } = "";
    public string? Detail { get; init; }
}

public static class ProviderSmokeProbe
{
    public static async Task<ProviderSmokeProbeResult> ProbeAsync(
        LlmProviderConfig config,
        ProviderSmokeRegistry? registry,
        CancellationToken ct)
    {
        var provider = NormalizeProvider(config.Provider);
        if (string.IsNullOrWhiteSpace(provider))
        {
            return new ProviderSmokeProbeResult
            {
                Status = SetupCheckStates.Skip,
                Summary = "Provider smoke skipped because no provider is configured."
            };
        }

        if (registry is not null && registry.TryGet(provider, out var registration) && registration is not null)
        {
            if (registration.ProbeAsync is not null)
                return await registration.ProbeAsync(config, ct);

            return new ProviderSmokeProbeResult
            {
                Status = SetupCheckStates.Skip,
                Summary = $"Provider smoke skipped for '{provider}'.",
                Detail = string.IsNullOrWhiteSpace(registration.SkipReason)
                    ? $"Provider '{provider}' does not expose a smoke probe."
                    : registration.SkipReason
            };
        }

        var apiKey = SecretResolver.Resolve(config.ApiKey);
        if (!HasRequiredCredentials(provider, apiKey))
        {
            return new ProviderSmokeProbeResult
            {
                Status = SetupCheckStates.Skip,
                Summary = $"Provider smoke skipped because credentials for '{provider}' are not resolved.",
                Detail = "Set the configured env: secret or provide a valid API key reference."
            };
        }

        HttpRequestMessage request;
        try
        {
            request = BuildRequest(provider, config, apiKey);
        }
        catch (InvalidOperationException ex)
        {
            return new ProviderSmokeProbeResult
            {
                Status = SetupCheckStates.Skip,
                Summary = $"Provider smoke skipped for '{provider}'.",
                Detail = ex.Message
            };
        }

        using var http = HttpClientFactory.Create();
        using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeoutCts.CancelAfter(GetProbeTimeout(config.TimeoutSeconds));

        try
        {
            using var response = await http.SendAsync(request, timeoutCts.Token);
            if (response.IsSuccessStatusCode)
            {
                return new ProviderSmokeProbeResult
                {
                    Status = SetupCheckStates.Pass,
                    Summary = $"Provider smoke passed for '{provider}/{config.Model}'."
                };
            }

            var detail = await SafeReadBodyAsync(response, timeoutCts.Token);
            var summary = $"Provider smoke failed for '{provider}/{config.Model}' with HTTP {(int)response.StatusCode}.";

            if (response.StatusCode is HttpStatusCode.TooManyRequests or HttpStatusCode.ServiceUnavailable or HttpStatusCode.BadGateway or HttpStatusCode.GatewayTimeout)
            {
                return new ProviderSmokeProbeResult
                {
                    Status = SetupCheckStates.Skip,
                    Summary = $"Provider smoke skipped for '{provider}/{config.Model}' because the upstream is temporarily unavailable.",
                    Detail = string.IsNullOrWhiteSpace(detail) ? summary : $"{summary} {detail}"
                };
            }

            return new ProviderSmokeProbeResult
            {
                Status = SetupCheckStates.Fail,
                Summary = summary,
                Detail = detail
            };
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            return new ProviderSmokeProbeResult
            {
                Status = SetupCheckStates.Skip,
                Summary = $"Provider smoke skipped for '{provider}/{config.Model}' because the probe timed out.",
                Detail = "The gateway can still work if the provider is reachable later."
            };
        }
        catch (HttpRequestException ex)
        {
            return new ProviderSmokeProbeResult
            {
                Status = SetupCheckStates.Skip,
                Summary = $"Provider smoke skipped for '{provider}/{config.Model}' because the upstream endpoint is unreachable.",
                Detail = ex.Message
            };
        }
    }

    public static Task<ProviderSmokeProbeResult> ProbeAsync(LlmProviderConfig config, CancellationToken ct)
        => ProbeAsync(config, registry: null, ct);

    public static bool IsProviderConfigured(LlmProviderConfig config, ProviderSmokeRegistry? registry = null)
    {
        var provider = NormalizeProvider(config.Provider);
        if (string.IsNullOrWhiteSpace(provider) || string.IsNullOrWhiteSpace(config.Model))
            return false;

        if (registry is not null && registry.TryGet(provider, out var registration) && registration is not null)
            return registration.TreatAsConfigured;

        var apiKey = SecretResolver.Resolve(config.ApiKey);
        return HasRequiredCredentials(provider, apiKey);
    }

    private static HttpRequestMessage BuildRequest(string provider, LlmProviderConfig config, string? apiKey)
    {
        return provider switch
        {
            "openai" or "openai-compatible" or "groq" or "together" or "lmstudio" or "azure-openai"
                => BuildOpenAiStyleRequest(provider, config, apiKey),
            "ollama" => BuildOllamaRequest(config),
            "anthropic" or "claude" or "anthropic-vertex" or "amazon-bedrock"
                => BuildAnthropicStyleRequest(provider, config, apiKey),
            "gemini" or "google"
                => BuildGeminiRequest(config, apiKey),
            _ => throw new InvalidOperationException($"Provider '{provider}' does not have a built-in smoke probe."),
        };
    }

    private static HttpRequestMessage BuildOpenAiStyleRequest(string provider, LlmProviderConfig config, string? apiKey)
    {
        var endpoint = provider switch
        {
            "openai" => AppendPath(config.Endpoint, "https://api.openai.com/v1", "chat/completions"),
            "groq" => AppendPath(config.Endpoint, "https://api.groq.com/openai/v1", "chat/completions"),
            "together" => AppendPath(config.Endpoint, "https://api.together.xyz/v1", "chat/completions"),
            "lmstudio" => AppendPath(config.Endpoint, "http://127.0.0.1:1234/v1", "chat/completions"),
            "azure-openai" => AppendPath(config.Endpoint, null, "chat/completions"),
            _ => AppendPath(config.Endpoint, null, "chat/completions")
        };

        if (endpoint is null)
            throw new InvalidOperationException($"Provider '{provider}' requires OpenClaw:Llm:Endpoint to run a smoke probe.");

        var request = new HttpRequestMessage(HttpMethod.Post, endpoint);
        if (!string.IsNullOrWhiteSpace(apiKey))
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", apiKey);
        request.Content = BuildJsonContent(
            $$"""
            {"model":"{{EscapeJson(config.Model)}}","messages":[{"role":"user","content":"Reply with READY."}],"temperature":0,"max_tokens":8}
            """);
        return request;
    }

    private static HttpRequestMessage BuildOllamaRequest(LlmProviderConfig config)
    {
        var endpoint = $"{OllamaEndpointNormalizer.NormalizeBaseUrl(config.Endpoint).TrimEnd('/')}/api/chat";
        return new HttpRequestMessage(HttpMethod.Post, endpoint)
        {
            Content = BuildJsonContent(
                $"{{\"model\":\"{EscapeJson(config.Model)}\",\"stream\":false,\"messages\":[{{\"role\":\"user\",\"content\":\"Reply with READY.\"}}],\"options\":{{\"temperature\":0,\"num_predict\":8}}}}")
        };
    }

    private static HttpRequestMessage BuildAnthropicStyleRequest(string provider, LlmProviderConfig config, string? apiKey)
    {
        var endpoint = provider switch
        {
            "anthropic" or "claude" => AppendPath(config.Endpoint, "https://api.anthropic.com/v1", "messages"),
            _ => AppendPath(config.Endpoint, null, "messages")
        };

        if (endpoint is null)
            throw new InvalidOperationException($"Provider '{provider}' requires OpenClaw:Llm:Endpoint to run a smoke probe.");

        var request = new HttpRequestMessage(HttpMethod.Post, endpoint);
        request.Headers.TryAddWithoutValidation("x-api-key", apiKey);
        request.Headers.TryAddWithoutValidation("anthropic-version", "2023-06-01");
        request.Content = BuildJsonContent(
            $$"""
            {"model":"{{EscapeJson(config.Model)}}","max_tokens":8,"messages":[{"role":"user","content":"Reply with READY."}]}
            """);
        return request;
    }

    private static HttpRequestMessage BuildGeminiRequest(LlmProviderConfig config, string? apiKey)
    {
        if (string.IsNullOrWhiteSpace(apiKey))
            throw new InvalidOperationException("Gemini smoke probes require a resolved API key.");

        var endpoint = BuildGeminiEndpoint(config.Endpoint, config.Model, apiKey);
        var request = new HttpRequestMessage(HttpMethod.Post, endpoint)
        {
            Content = BuildJsonContent(
                """
                {"contents":[{"role":"user","parts":[{"text":"Reply with READY."}]}],"generationConfig":{"temperature":0,"maxOutputTokens":8}}
                """)
        };
        return request;
    }

    private static HttpContent BuildJsonContent(string payload)
        => new StringContent(payload, Encoding.UTF8, "application/json");

    private static string? AppendPath(string? configuredBase, string? defaultBase, string relativePath)
    {
        var baseValue = string.IsNullOrWhiteSpace(configuredBase) ? defaultBase : configuredBase;
        if (string.IsNullOrWhiteSpace(baseValue))
            return null;

        if (baseValue.Contains(relativePath, StringComparison.OrdinalIgnoreCase))
            return baseValue;

        return $"{baseValue.TrimEnd('/')}/{relativePath}";
    }

    private static string BuildGeminiEndpoint(string? configuredEndpoint, string model, string apiKey)
    {
        var endpoint = string.IsNullOrWhiteSpace(configuredEndpoint)
            ? $"https://generativelanguage.googleapis.com/v1beta/models/{model}:generateContent"
            : configuredEndpoint!;

        if (endpoint.Contains(":generateContent", StringComparison.OrdinalIgnoreCase))
            return AppendQueryParameter(endpoint, "key", apiKey);

        return AppendQueryParameter($"{endpoint.TrimEnd('/')}/models/{model}:generateContent", "key", apiKey);
    }

    private static string AppendQueryParameter(string uri, string key, string value)
    {
        var separator = uri.Contains('?', StringComparison.Ordinal) ? "&" : "?";
        return $"{uri}{separator}{Uri.EscapeDataString(key)}={Uri.EscapeDataString(value)}";
    }

    private static bool HasRequiredCredentials(string provider, string? apiKey)
        => provider switch
        {
            "ollama" or "lmstudio" => true,
            _ => !string.IsNullOrWhiteSpace(apiKey)
        };

    private static string NormalizeProvider(string? provider)
        => string.IsNullOrWhiteSpace(provider) ? string.Empty : provider.Trim().ToLowerInvariant();

    private static TimeSpan GetProbeTimeout(int configuredTimeoutSeconds)
    {
        var seconds = configuredTimeoutSeconds <= 0
            ? 12
            : Math.Clamp(configuredTimeoutSeconds, 4, 15);
        return TimeSpan.FromSeconds(seconds);
    }

    private static async Task<string?> SafeReadBodyAsync(HttpResponseMessage response, CancellationToken ct)
    {
        try
        {
            var payload = await response.Content.ReadAsStringAsync(ct);
            if (string.IsNullOrWhiteSpace(payload))
                return null;

            return payload.Length <= 400 ? payload : payload[..400];
        }
        catch
        {
            return null;
        }
    }

    private static string EscapeJson(string value)
        => value
            .Replace("\\", "\\\\", StringComparison.Ordinal)
            .Replace("\"", "\\\"", StringComparison.Ordinal);
}

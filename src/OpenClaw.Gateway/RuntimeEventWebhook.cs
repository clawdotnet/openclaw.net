using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;

using Microsoft.Extensions.Logging;

using OpenClaw.Core.Models;

namespace OpenClaw.Gateway;

/// <summary>
/// Configuration for <see cref="RuntimeEventWebhook"/>. <see cref="Url"/> empty
/// disables the webhook entirely (no HTTP traffic, no log spam beyond debug).
/// </summary>
public sealed class RuntimeEventWebhookOptions
{
    public string Url { get; set; } = "";
    public string? BearerToken { get; set; }
    public int RetryDelayMs { get; set; } = 1000;
}

/// <summary>
/// Mirrors <see cref="RuntimeEventEntry"/> writes out to a sidecar that closes
/// the Thompson Sampling feedback loop. The webhook fires *after* the
/// durable JSONL append, so a webhook failure never loses the durable record.
///
/// Failure handling:
/// <list type="bullet">
///   <item>5xx → log warning, retry once after <see cref="RuntimeEventWebhookOptions.RetryDelayMs"/>.</item>
///   <item>Connection refused / HttpRequestException → log warning, retry once.</item>
///   <item>401 / 403 → log warning, stop sending (configuration error).</item>
///   <item>Other 4xx → log debug, drop (the sidecar is misinterpreting the entry; further retries won't help).</item>
///   <item>2xx → done.</item>
/// </list>
/// </summary>
public sealed class RuntimeEventWebhook
{
    private readonly HttpClient _http;
    private readonly RuntimeEventWebhookOptions _options;
    private readonly ILogger<RuntimeEventWebhook> _logger;

    public RuntimeEventWebhook(HttpClient http, RuntimeEventWebhookOptions options, ILogger<RuntimeEventWebhook> logger)
    {
        ArgumentNullException.ThrowIfNull(http);
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(logger);
        _http = http;
        _options = options;
        _logger = logger;
    }

    public async Task SendAsync(RuntimeEventEntry entry, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(entry);

        if (string.IsNullOrWhiteSpace(_options.Url))
        {
            _logger.LogDebug("RuntimeEventWebhook URL not configured; skipping entry {EventId}.", entry.Id);
            return;
        }

        var attempts = 0;
        while (true)
        {
            attempts++;
            try
            {
                using var request = new HttpRequestMessage(HttpMethod.Post, _options.Url);
                if (!string.IsNullOrWhiteSpace(_options.BearerToken))
                {
                    request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", _options.BearerToken);
                }
                var json = JsonSerializer.Serialize(entry, CoreJsonContext.Default.RuntimeEventEntry);
                request.Content = new StringContent(json, Encoding.UTF8, "application/json");

                using var response = await _http.SendAsync(request, cancellationToken).ConfigureAwait(false);
                if (response.IsSuccessStatusCode)
                {
                    return;
                }

                var status = (int)response.StatusCode;
                if (status is 401 or 403)
                {
                    _logger.LogWarning(
                        "RuntimeEventWebhook returned {StatusCode}; webhook will not retry (configuration error).",
                        status);
                    return;
                }

                if (status is >= 500 || status is 429)
                {
                    if (attempts >= 2)
                    {
                        _logger.LogWarning(
                            "RuntimeEventWebhook returned {StatusCode} after retry; dropping event {EventId}.",
                            status, entry.Id);
                        return;
                    }
                    _logger.LogWarning(
                        "RuntimeEventWebhook returned {StatusCode}; retrying in {DelayMs}ms.",
                        status, _options.RetryDelayMs);
                    await Task.Delay(_options.RetryDelayMs, cancellationToken).ConfigureAwait(false);
                    continue;
                }

                // Other 4xx — the sidecar rejected the payload. Don't retry.
                _logger.LogDebug(
                    "RuntimeEventWebhook returned {StatusCode} for entry {EventId}; dropping.",
                    status, entry.Id);
                return;
            }
            catch (HttpRequestException ex)
            {
                if (attempts >= 2)
                {
                    _logger.LogWarning(ex,
                        "RuntimeEventWebhook connection failed twice; dropping event {EventId}.",
                        entry.Id);
                    return;
                }
                _logger.LogWarning(ex,
                    "RuntimeEventWebhook connection failed; retrying in {DelayMs}ms.",
                    _options.RetryDelayMs);
                await Task.Delay(_options.RetryDelayMs, cancellationToken).ConfigureAwait(false);
            }
            catch (TaskCanceledException ex) when (ex.InnerException is TimeoutException || cancellationToken.IsCancellationRequested)
            {
                // Caller cancelled — propagate by returning.
                _logger.LogDebug(ex, "RuntimeEventWebhook send cancelled for entry {EventId}.", entry.Id);
                return;
            }
        }
    }
}
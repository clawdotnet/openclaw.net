using System.Net;
using System.Text.Json;
using OpenClaw.Core.Abstractions;

namespace OpenClaw.Channels;

internal static class ChannelDeliveryHttp
{
    public static void EnsureAccepted(HttpResponseMessage response, TimeSpan? retryAfter = null)
    {
        if (response.IsSuccessStatusCode)
        {
            return;
        }

        var rateLimited = response.StatusCode == HttpStatusCode.TooManyRequests;
        var unknown = (int)response.StatusCode >= 500 || response.StatusCode == HttpStatusCode.RequestTimeout;
        retryAfter ??= GetRetryAfter(response);
        throw new ChannelDeliveryException($"Provider returned HTTP {(int)response.StatusCode}.",
            rateLimited, outcomeUnknown: unknown, retryAfter: retryAfter);
    }

    public static void EnsurePreSendAccepted(HttpResponseMessage response)
    {
        if (response.IsSuccessStatusCode)
        {
            return;
        }

        var transient = response.StatusCode == HttpStatusCode.TooManyRequests ||
            response.StatusCode == HttpStatusCode.RequestTimeout ||
            (int)response.StatusCode >= 500;
        throw new ChannelDeliveryException($"Provider pre-send request returned HTTP {(int)response.StatusCode}.",
            transient, retryAfter: GetRetryAfter(response));
    }

    public static TimeSpan? GetRetryAfter(HttpResponseMessage response)
    {
        var retryAfter = response.Headers.RetryAfter?.Delta;
        if (retryAfter is null && response.Headers.RetryAfter?.Date is { } date)
        {
            retryAfter = date - DateTimeOffset.UtcNow;
        }

        return retryAfter;
    }

    public static async Task RequireZeroCodeAsync(HttpResponseMessage response, string field, CancellationToken ct)
    {
        EnsureAccepted(response);
        using var document = JsonDocument.Parse(await response.Content.ReadAsStringAsync(ct));
        if (!document.RootElement.TryGetProperty(field, out var code) || !code.TryGetInt32(out var value))
        {
            throw new ChannelDeliveryException("Provider acknowledgment is missing its status code.", false, outcomeUnknown: true);
        }
        if (value != 0)
        {
            var detail = GetProviderErrorDetail(document.RootElement);
            var suffix = detail is null ? string.Empty : $", detail={detail}";
            throw new ChannelDeliveryException($"Provider rejected the send ({field}={value}{suffix}).", false);
        }
    }

    public static string? GetProviderErrorDetail(JsonElement root)
    {
        foreach (var field in new[] { "error", "message", "msg", "errmsg" })
        {
            if (!root.TryGetProperty(field, out var value) || value.ValueKind != JsonValueKind.String)
            {
                continue;
            }

            var detail = value.GetString()?.Replace('\r', ' ').Replace('\n', ' ').Trim();
            if (string.IsNullOrWhiteSpace(detail))
            {
                continue;
            }

            return detail.Length <= 256 ? detail : detail[..256];
        }

        return null;
    }
}

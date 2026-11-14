using System.Net;
using System.Text.Json;
using OpenClaw.Core.Abstractions;

namespace OpenClaw.Channels;

internal static class ChannelDeliveryHttp
{
    public static void EnsureAccepted(HttpResponseMessage response)
    {
        if (response.IsSuccessStatusCode)
            return;
        var rateLimited = response.StatusCode == HttpStatusCode.TooManyRequests;
        var unknown = (int)response.StatusCode >= 500 || response.StatusCode == HttpStatusCode.RequestTimeout;
        var retryAfter = response.Headers.RetryAfter?.Delta;
        if (retryAfter is null && response.Headers.RetryAfter?.Date is { } date)
            retryAfter = date - DateTimeOffset.UtcNow;
        throw new ChannelDeliveryException($"Provider returned HTTP {(int)response.StatusCode}.",
            rateLimited, outcomeUnknown: unknown, retryAfter: retryAfter);
    }

    public static async Task RequireZeroCodeAsync(HttpResponseMessage response, string field, CancellationToken ct)
    {
        EnsureAccepted(response);
        using var document = JsonDocument.Parse(await response.Content.ReadAsStringAsync(ct));
        if (!document.RootElement.TryGetProperty(field, out var code) || !code.TryGetInt32(out var value))
            throw new ChannelDeliveryException("Provider acknowledgment is missing its status code.", false, outcomeUnknown: true);
        if (value != 0)
            throw new ChannelDeliveryException($"Provider rejected the send ({field}={value}).", false);
    }
}

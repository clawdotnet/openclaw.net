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

        throw CreateFailure(response, retryAfter, detail: null);
    }

    public static async Task EnsureAcceptedAsync(
        HttpResponseMessage response,
        CancellationToken ct,
        TimeSpan? retryAfter = null)
    {
        if (response.IsSuccessStatusCode)
        {
            return;
        }

        string? detail = null;
        try
        {
            using var document = JsonDocument.Parse(await response.Content.ReadAsStringAsync(ct));
            detail = GetProviderErrorDetail(document.RootElement);
        }
        catch (JsonException)
        {
            // Status and retry metadata remain sufficient when the error body is not JSON.
        }
        catch (HttpRequestException)
        {
            // Status and retry metadata remain sufficient when the error body cannot be read.
        }
        catch (IOException)
        {
            // Status and retry metadata remain sufficient when the error body cannot be read.
        }

        throw CreateFailure(response, retryAfter, detail);
    }

    private static ChannelDeliveryException CreateFailure(
        HttpResponseMessage response,
        TimeSpan? retryAfter,
        string? detail)
    {
        var rateLimited = response.StatusCode == HttpStatusCode.TooManyRequests;
        var unknown = (int)response.StatusCode >= 500 || response.StatusCode == HttpStatusCode.RequestTimeout;
        retryAfter ??= GetRetryAfter(response);
        var suffix = detail is null ? string.Empty : $" ({detail})";
        return new ChannelDeliveryException($"Provider returned HTTP {(int)response.StatusCode}{suffix}.",
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
            var detail = GetProviderMessage(document.RootElement);
            var suffix = detail is null ? string.Empty : $", detail={detail}";
            throw new ChannelDeliveryException($"Provider rejected the send ({field}={value}{suffix}).", false);
        }
    }

    public static string? GetProviderErrorDetail(JsonElement root)
    {
        if (root.ValueKind != JsonValueKind.Object)
        {
            return null;
        }

        if (root.TryGetProperty("error", out var error) && error.ValueKind == JsonValueKind.Object)
        {
            return GetProviderErrorDetail(error);
        }

        var code = GetProviderCode(root);
        var detail = GetProviderMessage(root);
        if (detail is not null)
        {
            return code is null ? detail : $"code={code}, message={detail}";
        }

        return code is null ? null : $"code={code}";
    }

    private static string? GetProviderMessage(JsonElement root)
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

            var safeDetail = detail.Length <= 256 ? detail : detail[..256];
            return safeDetail;
        }

        return null;
    }

    private static string? GetProviderCode(JsonElement root)
    {
        foreach (var field in new[] { "code", "error_code", "errorCode" })
        {
            if (!root.TryGetProperty(field, out var value))
            {
                continue;
            }

            if (value.ValueKind == JsonValueKind.String)
            {
                return value.GetString();
            }

            if (value.ValueKind == JsonValueKind.Number)
            {
                return value.GetRawText();
            }
        }

        return null;
    }
}

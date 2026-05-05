using System.Net;
using System.Net.Http.Headers;
using OpenClaw.Payments.Abstractions;

namespace OpenClaw.Payments.Core;

public sealed class PaymentAwareHttpHandler : DelegatingHandler
{
    private readonly PaymentRuntimeService _payments;
    private readonly PaymentExecutionContext _context;
    private readonly string _providerId;

    public PaymentAwareHttpHandler(
        PaymentRuntimeService payments,
        PaymentExecutionContext context,
        string providerId,
        HttpMessageHandler? innerHandler = null)
        : base(innerHandler ?? new HttpClientHandler())
    {
        _payments = payments;
        _context = context;
        _providerId = providerId;
    }

    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        var response = await base.SendAsync(request, cancellationToken);
        if (response.StatusCode != HttpStatusCode.PaymentRequired)
            return response;

        var challenge = await MachinePaymentChallengeParser.ParseAsync(response, cancellationToken);
        if (challenge is null || request.RequestUri is null)
            return response;

        response.Dispose();
        var result = await _payments.ExecuteMachinePaymentAsync(new MachinePaymentRequest
        {
            ProviderId = _providerId,
            Challenge = challenge with
            {
                ResourceUrl = challenge.ResourceUrl ?? request.RequestUri.ToString(),
                ProviderId = challenge.ProviderId ?? _providerId
            },
            Environment = _context.Environment
        }, _context, cancellationToken);

        var secret = await _payments.RetrieveMachineAuthorizationOnceAsync(result.PaymentId, cancellationToken);
        var header = secret?.Resolve(PaymentSecretField.AuthorizationHeader);
        if (string.IsNullOrWhiteSpace(header))
            throw new InvalidOperationException("Machine payment provider did not return scoped authorization.");

        using var retry = CloneRequest(request);
        retry.Headers.Authorization = AuthenticationHeaderValue.Parse(header);
        return await base.SendAsync(retry, cancellationToken);
    }

    private static HttpRequestMessage CloneRequest(HttpRequestMessage request)
    {
        var clone = new HttpRequestMessage(request.Method, request.RequestUri)
        {
            Version = request.Version,
            VersionPolicy = request.VersionPolicy
        };
        foreach (var header in request.Headers)
            clone.Headers.TryAddWithoutValidation(header.Key, header.Value);
        return clone;
    }
}

public static class MachinePaymentChallengeParser
{
    public static async ValueTask<MachinePaymentChallenge?> ParseAsync(HttpResponseMessage response, CancellationToken ct)
    {
        string? body = null;
        try
        {
            body = response.Content is null ? null : await response.Content.ReadAsStringAsync(ct);
        }
        catch
        {
            body = null;
        }

        var header = response.Headers.TryGetValues("Payment-Required", out var values)
            ? values.FirstOrDefault()
            : null;
        var challengeText = !string.IsNullOrWhiteSpace(header) ? header : body;
        if (string.IsNullOrWhiteSpace(challengeText))
            return null;

        return new MachinePaymentChallenge
        {
            Protocol = challengeText.Contains("x402", StringComparison.OrdinalIgnoreCase) ? "x402" : "http-402",
            ChallengeId = ExtractKeyValue(challengeText, "challenge") ?? ExtractKeyValue(challengeText, "id"),
            MerchantName = ExtractKeyValue(challengeText, "merchant"),
            Currency = ExtractKeyValue(challengeText, "currency") ?? "USD",
            AmountMinor = long.TryParse(ExtractKeyValue(challengeText, "amount"), out var amount) ? amount : 0,
            SafeMetadata = new Dictionary<string, string>(StringComparer.Ordinal)
            {
                ["source"] = "http_402"
            }
        };
    }

    private static string? ExtractKeyValue(string text, string key)
    {
        var needle = key + "=";
        var index = text.IndexOf(needle, StringComparison.OrdinalIgnoreCase);
        if (index < 0)
            return null;
        var start = index + needle.Length;
        var end = text.IndexOfAny([';', ',', '\n', '\r'], start);
        return (end < 0 ? text[start..] : text[start..end]).Trim().Trim('"', '\'');
    }
}

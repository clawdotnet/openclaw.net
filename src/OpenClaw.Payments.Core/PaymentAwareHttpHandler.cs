using System.Net;
using System.Net.Http.Headers;
using OpenClaw.Payments.Abstractions;

namespace OpenClaw.Payments.Core;

public sealed class PaymentAwareHttpHandler : DelegatingHandler
{
    public static readonly HttpRequestOptionsKey<string> IdempotencyKeyOption = new("OpenClaw.PaymentIdempotencyKey");

    private readonly PaymentRuntimeService _payments;
    private readonly PaymentExecutionContext _context;
    private readonly string _providerId;

    public PaymentAwareHttpHandler(
        PaymentRuntimeService payments,
        PaymentExecutionContext context,
        string providerId,
        HttpMessageHandler? innerHandler = null)
        : base(innerHandler ?? CreateDefaultHandler())
    {
        _payments = payments;
        _context = context;
        _providerId = providerId;
    }

    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        var originalContent = request.Content;
        var bufferedContent = await BufferedRequestContent.CaptureAsync(originalContent, cancellationToken);
        if (bufferedContent is not null)
        {
            request.Content = bufferedContent.CreateContent();
            originalContent?.Dispose();
        }

        var response = await base.SendAsync(request, cancellationToken);
        if (response.StatusCode != HttpStatusCode.PaymentRequired)
            return response;

        var challenge = await MachinePaymentChallengeParser.ParseAsync(response, cancellationToken);
        if (challenge is null || request.RequestUri is null)
            return response;

        response.Dispose();
        var result = await _payments.ExecuteMachinePaymentAsync(new MachinePaymentRequest
        {
            IdempotencyKey = request.Options.TryGetValue(IdempotencyKeyOption, out var key)
                ? key
                : string.IsNullOrWhiteSpace(challenge.ChallengeId) ? null
                    : FilePaymentIdempotencyStore.GetId(request.RequestUri.ToString(), challenge.ChallengeId),
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

        using var retry = CloneRequest(request, bufferedContent);
        retry.Headers.Authorization = AuthenticationHeaderValue.Parse(header);
        return await base.SendAsync(retry, cancellationToken);
    }

    private static HttpMessageHandler CreateDefaultHandler()
        => new HttpClientHandler();

    private static HttpRequestMessage CloneRequest(HttpRequestMessage request, BufferedRequestContent? content)
    {
        var clone = new HttpRequestMessage(request.Method, request.RequestUri)
        {
            Version = request.Version,
            VersionPolicy = request.VersionPolicy
        };
        foreach (var header in request.Headers)
            clone.Headers.TryAddWithoutValidation(header.Key, header.Value);
        foreach (var option in request.Options)
            clone.Options.Set(new HttpRequestOptionsKey<object?>(option.Key), option.Value);
        if (content is not null)
            clone.Content = content.CreateContent();
        return clone;
    }

    private sealed class BufferedRequestContent
    {
        private readonly byte[] _body;
        private readonly KeyValuePair<string, IEnumerable<string>>[] _headers;

        private BufferedRequestContent(byte[] body, KeyValuePair<string, IEnumerable<string>>[] headers)
        {
            _body = body;
            _headers = headers;
        }

        public static async ValueTask<BufferedRequestContent?> CaptureAsync(HttpContent? content, CancellationToken ct)
        {
            if (content is null)
                return null;

            var body = await content.ReadAsByteArrayAsync(ct);
            return new BufferedRequestContent(body, content.Headers.ToArray());
        }

        public HttpContent CreateContent()
        {
            var content = new ByteArrayContent(_body);
            foreach (var header in _headers)
                content.Headers.TryAddWithoutValidation(header.Key, header.Value);
            return content;
        }
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
        catch (HttpRequestException)
        {
            body = null;
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            body = null;
        }
        catch (ObjectDisposedException)
        {
            body = null;
        }
        catch (InvalidOperationException)
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

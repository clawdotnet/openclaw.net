using System.Collections.Concurrent;
using OpenClaw.Core.Contacts;
using OpenClaw.Core.Models;
using OpenClaw.Core.Pipeline;
using OpenClaw.Core.Security;

namespace OpenClaw.Gateway;

internal sealed class TwilioSmsWebhookHandler
{
    private readonly TwilioSmsConfig _config;
    private readonly string _twilioAuthToken;
    private readonly IContactStore _contacts;
    private readonly AllowlistManager _allowlists;
    private readonly RecentSendersStore _recentSenders;
    private readonly AllowlistSemantics _allowlistSemantics;
    private readonly ConcurrentDictionary<string, RateWindow> _rate = new(StringComparer.Ordinal);

    private sealed class RateWindow
    {
        private readonly int _limit;
        private readonly Lock _lock = new();
        private long _windowMinute;
        private int _count;

        public RateWindow(int limit) => _limit = Math.Max(1, limit);

        public bool TryConsume()
        {
            lock (_lock)
            {
                var minute = DateTimeOffset.UtcNow.ToUnixTimeSeconds() / 60;
                if (minute != _windowMinute)
                {
                    _windowMinute = minute;
                    _count = 0;
                }

                _count++;
                return _count <= _limit;
            }
        }
    }

    public TwilioSmsWebhookHandler(
        TwilioSmsConfig config,
        string twilioAuthToken,
        IContactStore contacts,
        AllowlistManager allowlists,
        RecentSendersStore recentSenders,
        AllowlistSemantics allowlistSemantics)
    {
        _config = config;
        _twilioAuthToken = twilioAuthToken;
        _contacts = contacts;
        _allowlists = allowlists;
        _recentSenders = recentSenders;
        _allowlistSemantics = allowlistSemantics;
    }

    public string PublicWebhookUrl
    {
        get
        {
            var baseUrl = (_config.WebhookPublicBaseUrl ?? "").TrimEnd('/');
            var path = _config.WebhookPath.StartsWith('/') ? _config.WebhookPath : "/" + _config.WebhookPath;
            return baseUrl + path;
        }
    }

    public async Task<WebhookResult> HandleAsync(
        IReadOnlyDictionary<string, string> form,
        string? providedSignature,
        Func<InboundMessage, CancellationToken, ValueTask> enqueue,
        CancellationToken ct)
    {
        if (!_config.Enabled)
            return WebhookResult.NotFound();

        form.TryGetValue("From", out var from);
        form.TryGetValue("To", out var to);
        form.TryGetValue("Body", out var body);
        form.TryGetValue("MessageSid", out var messageSid);

        if (string.IsNullOrWhiteSpace(from) || string.IsNullOrWhiteSpace(to))
            return WebhookResult.BadRequest("Missing From/To.");

        await _recentSenders.RecordAsync("sms", from, senderName: null, ct);

        body ??= "";
        if (body.Length > _config.MaxInboundChars)
            return WebhookResult.Status(413);

        if (_config.ValidateSignature)
        {
            if (string.IsNullOrWhiteSpace(_config.WebhookPublicBaseUrl))
                return WebhookResult.Status(500);

            if (!TwilioWebhookVerifier.IsValidSignature(PublicWebhookUrl, form, _twilioAuthToken, providedSignature))
                return WebhookResult.Unauthorized();
        }

        if (!IsAllowedInbound(from, to))
        {
            if (_config.AutoReplyForBlocked)
                return WebhookResult.TwiMl(_config.HelpText);
            return WebhookResult.Unauthorized();
        }

        var limiter = _rate.GetOrAdd(from, _ => new RateWindow(_config.RateLimitPerFromPerMinute));
        if (!limiter.TryConsume())
            return WebhookResult.Status(429);

        await _contacts.TouchAsync(from, ct);

        var normalized = body.Trim();
        var keyword = normalized.ToUpperInvariant();

        if (IsStopKeyword(keyword))
        {
            await _contacts.SetDoNotTextAsync(from, true, ct);
            return WebhookResult.Ok();
        }

        if (IsStartKeyword(keyword))
        {
            await _contacts.SetDoNotTextAsync(from, false, ct);
            return WebhookResult.Ok();
        }

        if (IsHelpKeyword(keyword))
        {
            return WebhookResult.TwiMl(_config.HelpText);
        }

        var msg = new InboundMessage
        {
            ChannelId = "sms",
            SenderId = from,
            Text = normalized,
            MessageId = string.IsNullOrWhiteSpace(messageSid) ? null : messageSid
        };

        await enqueue(msg, ct);
        return WebhookResult.Ok();
    }

    private bool IsAllowedInbound(string fromE164, string toE164)
    {
        var dyn = _allowlists.TryGetDynamic("sms");
        var allowedFrom = dyn is { AllowedFrom.Length: > 0 } ? dyn.AllowedFrom : _config.AllowedFromNumbers;
        var allowedTo = dyn is { AllowedTo.Length: > 0 } ? dyn.AllowedTo : _config.AllowedToNumbers;

        if (_allowlistSemantics == AllowlistSemantics.Strict)
        {
            return AllowlistPolicy.IsAllowed(allowedFrom, fromE164, AllowlistSemantics.Strict)
                   && AllowlistPolicy.IsAllowed(allowedTo, toE164, AllowlistSemantics.Strict);
        }

        // Legacy SMS behavior: empty lists deny; exact match or wildcard allow.
        return IsAllowedLegacyExactOrWildcard(allowedFrom, fromE164)
               && IsAllowedLegacyExactOrWildcard(allowedTo, toE164);
    }

    private static bool IsAllowedLegacyExactOrWildcard(string[] allowlist, string value)
    {
        if (allowlist.Length == 0)
            return false;

        foreach (var entry in allowlist)
        {
            if (string.IsNullOrWhiteSpace(entry))
                continue;

            var pat = entry.Trim();
            if (pat == "*")
                return true;

            if (string.Equals(pat, value, StringComparison.Ordinal))
                return true;
        }

        return false;
    }

    private static bool IsStopKeyword(string keyword) =>
        keyword is "STOP" or "UNSUBSCRIBE" or "CANCEL" or "END" or "QUIT";

    private static bool IsStartKeyword(string keyword) =>
        keyword is "START" or "YES" or "UNSTOP";

    private static bool IsHelpKeyword(string keyword) =>
        keyword is "HELP" or "INFO";
}

internal readonly record struct WebhookResult(int StatusCode, string? ContentType, string? Body)
{
    public static WebhookResult Ok() => new(200, null, null);
    public static WebhookResult Unauthorized() => new(401, null, null);
    public static WebhookResult NotFound() => new(404, null, null);
    public static WebhookResult BadRequest(string message) => new(400, "text/plain; charset=utf-8", message);
    public static WebhookResult Status(int statusCode) => new(statusCode, null, null);

    public static WebhookResult TwiMl(string message)
    {
        var xml = $"""<?xml version="1.0" encoding="UTF-8"?><Response><Message>{EscapeXml(message)}</Message></Response>""";
        return new WebhookResult(200, "application/xml; charset=utf-8", xml);
    }

    private static string EscapeXml(string value)
        => value.Replace("&", "&amp;").Replace("<", "&lt;").Replace(">", "&gt;").Replace("\"", "&quot;").Replace("'", "&apos;");
}

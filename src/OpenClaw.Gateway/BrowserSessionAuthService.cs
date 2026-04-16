using System.Collections.Concurrent;
using System.Security.Cryptography;
using Microsoft.AspNetCore.Http;
using OpenClaw.Core.Models;

namespace OpenClaw.Gateway;

internal sealed class BrowserSessionAuthService
{
    internal const string CookieName = "openclaw_web_session";
    internal const string CsrfHeaderName = "X-CSRF-Token";

    private sealed record SessionState(
        string SessionId,
        string CsrfToken,
        DateTimeOffset ExpiresAtUtc,
        bool Persistent,
        string Role,
        string? AccountId,
        string? Username,
        string? DisplayName,
        bool IsBootstrapAdmin);

    private readonly ConcurrentDictionary<string, SessionState> _sessions = new(StringComparer.Ordinal);
    private readonly GatewayConfig _config;

    public BrowserSessionAuthService(GatewayConfig config)
    {
        _config = config;
    }

    public BrowserSessionTicket Create(bool remember, OperatorIdentitySnapshot? identity = null)
    {
        CleanupExpired();

        var sessionId = $"bws_{Guid.NewGuid():N}";
        var csrfToken = Convert.ToHexString(RandomNumberGenerator.GetBytes(32));
        var lifetime = GetLifetime(remember);
        var expiresAtUtc = DateTimeOffset.UtcNow.Add(lifetime);
        var effectiveIdentity = identity ?? new OperatorIdentitySnapshot
        {
            AuthMode = "browser-session",
            Role = OperatorRoleNames.Admin
        };

        _sessions[sessionId] = new SessionState(
            sessionId,
            csrfToken,
            expiresAtUtc,
            remember,
            OperatorRoleNames.Normalize(effectiveIdentity.Role),
            effectiveIdentity.AccountId,
            effectiveIdentity.Username,
            effectiveIdentity.DisplayName,
            effectiveIdentity.IsBootstrapAdmin);
        return new BrowserSessionTicket(
            sessionId,
            csrfToken,
            expiresAtUtc,
            remember,
            OperatorRoleNames.Normalize(effectiveIdentity.Role),
            effectiveIdentity.AccountId,
            effectiveIdentity.Username,
            effectiveIdentity.DisplayName,
            effectiveIdentity.IsBootstrapAdmin);
    }

    public bool TryAuthorize(HttpContext ctx, bool requireCsrf, out BrowserSessionTicket? ticket)
    {
        ticket = null;
        CleanupExpired();

        var sessionId = ctx.Request.Cookies[CookieName];
        if (string.IsNullOrWhiteSpace(sessionId))
            return false;

        if (!_sessions.TryGetValue(sessionId, out var state))
            return false;

        if (state.ExpiresAtUtc <= DateTimeOffset.UtcNow)
        {
            _sessions.TryRemove(sessionId, out _);
            return false;
        }

        if (requireCsrf)
        {
            var csrf = ctx.Request.Headers[CsrfHeaderName].ToString();
            if (!string.Equals(csrf, state.CsrfToken, StringComparison.Ordinal))
                return false;
        }

        var lifetime = GetLifetime(state.Persistent);
        var refreshed = state with { ExpiresAtUtc = DateTimeOffset.UtcNow.Add(lifetime) };
        _sessions[sessionId] = refreshed;
        ticket = new BrowserSessionTicket(
            refreshed.SessionId,
            refreshed.CsrfToken,
            refreshed.ExpiresAtUtc,
            refreshed.Persistent,
            refreshed.Role,
            refreshed.AccountId,
            refreshed.Username,
            refreshed.DisplayName,
            refreshed.IsBootstrapAdmin);
        return true;
    }

    public void Revoke(HttpContext ctx)
    {
        var sessionId = ctx.Request.Cookies[CookieName];
        if (!string.IsNullOrWhiteSpace(sessionId))
            _sessions.TryRemove(sessionId, out _);
    }

    public void WriteCookie(HttpContext ctx, BrowserSessionTicket ticket)
    {
        var options = new CookieOptions
        {
            HttpOnly = true,
            IsEssential = true,
            SameSite = SameSiteMode.Lax,
            Secure = ctx.Request.IsHttps,
            Path = "/"
        };

        if (ticket.Persistent)
            options.Expires = ticket.ExpiresAtUtc;

        ctx.Response.Cookies.Append(CookieName, ticket.SessionId, options);
    }

    public void ClearCookie(HttpContext ctx)
    {
        ctx.Response.Cookies.Delete(CookieName, new CookieOptions
        {
            HttpOnly = true,
            IsEssential = true,
            SameSite = SameSiteMode.Lax,
            Secure = ctx.Request.IsHttps,
            Path = "/"
        });
    }

    public bool HasActiveSessions()
    {
        CleanupExpired();
        return !_sessions.IsEmpty;
    }

    private void CleanupExpired()
    {
        if (_sessions.IsEmpty)
            return;

        var now = DateTimeOffset.UtcNow;
        foreach (var kvp in _sessions)
        {
            if (kvp.Value.ExpiresAtUtc <= now)
                _sessions.TryRemove(kvp.Key, out _);
        }
    }

    private TimeSpan GetLifetime(bool persistent)
        => persistent
            ? TimeSpan.FromDays(Math.Clamp(_config.Security.BrowserRememberDays, 1, 365))
            : TimeSpan.FromMinutes(Math.Clamp(_config.Security.BrowserSessionIdleMinutes, 5, 24 * 60));
}

internal sealed record BrowserSessionTicket(
    string SessionId,
    string CsrfToken,
    DateTimeOffset ExpiresAtUtc,
    bool Persistent,
    string Role,
    string? AccountId,
    string? Username,
    string? DisplayName,
    bool IsBootstrapAdmin);

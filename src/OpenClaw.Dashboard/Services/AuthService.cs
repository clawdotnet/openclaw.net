using System.Net.Http.Json;
using OpenClaw.Core.Models;
using OpenClaw.Dashboard.Models;

namespace OpenClaw.Dashboard.Services;

public class AuthService
{
    private readonly ApiService _api;

    public AuthState? CurrentAuth { get; private set; }

    public bool IsAuthenticated => CurrentAuth != null;

    public event Action? OnAuthStateChanged;

    public AuthService(ApiService api)
    {
        _api = api;
    }

    public async Task SyncAuth()
    {
        try
        {
            var state = await _api.GetAsync<AuthState>("auth/session").ConfigureAwait(false);
            SetAuth(state);
        }
        catch
        {
            SetAuth(null);
        }
    }

    public Task<bool> LoginWithCredentials(string username, string password)
        => PostLoginAsync(new AuthSessionRequest { Username = username, Password = password });

    public Task<bool> LoginWithToken(string token)
        => PostLoginAsync(new AuthSessionRequest { AccountToken = token });

    public Task<bool> LoginWithBootstrap(string bootstrapToken)
        => PostLoginAsync(new AuthSessionRequest(), bootstrapToken);

    private async Task<bool> PostLoginAsync(AuthSessionRequest body, string? bearerToken = null)
    {
        try
        {
            using var response = await _api.PostRawAsync("auth/session", body, bearerToken).ConfigureAwait(false);
            if (!response.IsSuccessStatusCode)
            {
                SetAuth(null);
                return false;
            }

            AuthState? state = null;
            if (response.Content.Headers.ContentLength != 0)
            {
                try
                {
                    state = await response.Content
                        .ReadFromJsonAsync<AuthState>()
                        .ConfigureAwait(false);
                }
                catch
                {
                    state = null;
                }
            }

            if (state is null)
            {
                await SyncAuth().ConfigureAwait(false);
                return IsAuthenticated;
            }

            SetAuth(state);
            return true;
        }
        catch
        {
            SetAuth(null);
            return false;
        }
    }

    public async Task Logout()
    {
        try
        {
            using var _ = await _api.DeleteAsync("auth/session").ConfigureAwait(false);
        }
        catch
        {
            // swallow — we still clear local state
        }

        SetAuth(null);
    }

    public bool HasRole(string requiredRole)
    {
        if (CurrentAuth is null)
        {
            return false;
        }

        return RoleRank(CurrentAuth.Role) >= RoleRank(requiredRole);
    }

    private static int RoleRank(string? role)
    {
        return role?.ToLowerInvariant() switch
        {
            "admin" => 3,
            "operator" => 2,
            "viewer" => 1,
            _ => 0
        };
    }

    public async Task<string?> GetOperatorToken()
    {
        try
        {
            using var response = await _api.GetRawAsync("auth/operator-token").ConfigureAwait(false);
            if (!response.IsSuccessStatusCode)
            {
                return null;
            }

            var payload = await response.Content
                .ReadFromJsonAsync<OperatorTokenResponse>()
                .ConfigureAwait(false);
            return payload?.Token;
        }
        catch
        {
            return null;
        }
    }

    private void SetAuth(AuthState? state)
    {
        var changed = !Equals(CurrentAuth, state);
        CurrentAuth = state;
        _api.SetCsrfToken(state?.CsrfToken);
        if (changed)
        {
            OnAuthStateChanged?.Invoke();
        }
    }

    private sealed record OperatorTokenResponse(string? Token);
}

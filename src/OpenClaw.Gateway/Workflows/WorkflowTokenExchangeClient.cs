using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using OpenClaw.Core.Abstractions;
using OpenClaw.Core.Models;
using OpenClaw.Core.Security;

namespace OpenClaw.Gateway.Workflows;

internal sealed class WorkflowTokenExchangeClient(
    IHttpClientFactory httpClientFactory, TimeProvider timeProvider, ILogger<WorkflowTokenExchangeClient> logger)
{
    private const string AccessTokenType = "urn:ietf:params:oauth:token-type:access_token";

    public async Task<WorkflowTokenExchangeResult> ExchangeAsync(
        string backendId, WorkflowAuthenticationConfig authentication, WorkflowExecutionContext executionContext,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(30), timeProvider);
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, timeout.Token);
        try
        {
            var secret = await SecretResolver.ResolveAsync(authentication.ClientSecretRef, linked.Token);
            if (string.IsNullOrWhiteSpace(secret) || string.IsNullOrWhiteSpace(authentication.ClientId) ||
                !Uri.TryCreate(authentication.TokenEndpoint, UriKind.Absolute, out var endpoint) ||
                endpoint.Scheme != Uri.UriSchemeHttps || !string.IsNullOrEmpty(endpoint.UserInfo) ||
                !string.IsNullOrEmpty(endpoint.Query) || !string.IsNullOrEmpty(endpoint.Fragment))
                throw Failure(backendId);
            using var request = new HttpRequestMessage(HttpMethod.Post, endpoint);
            var form = new List<KeyValuePair<string, string>>();
            if (string.Equals(authentication.Mode, "entra-obo", StringComparison.OrdinalIgnoreCase))
            {
                form.Add(new("grant_type", "urn:ietf:params:oauth:grant-type:jwt-bearer"));
                form.Add(new("requested_token_use", "on_behalf_of"));
                form.Add(new("assertion", executionContext.Assertion));
                form.Add(new("client_id", authentication.ClientId));
                form.Add(new("client_secret", secret));
            }
            else if (string.Equals(authentication.Mode, "token-exchange", StringComparison.OrdinalIgnoreCase))
            {
                form.Add(new("grant_type", "urn:ietf:params:oauth:grant-type:token-exchange"));
                form.Add(new("subject_token", executionContext.Assertion));
                form.Add(new("subject_token_type", AccessTokenType));
                form.Add(new("requested_token_type", AccessTokenType));
                if (authentication.Audience is not null) form.Add(new("audience", authentication.Audience));
                if (authentication.Resource is not null) form.Add(new("resource", authentication.Resource));
                var encoded = EncodeFormValue(authentication.ClientId) + ":" + EncodeFormValue(secret);
                request.Headers.Authorization = new("Basic", Convert.ToBase64String(Encoding.UTF8.GetBytes(encoded)));
            }
            else
                throw Failure(backendId);

            if (authentication.Scopes.Length != 0)
                form.Add(new("scope", string.Join(' ', authentication.Scopes)));
            request.Content = new FormUrlEncodedContent(form);
            var started = timeProvider.GetUtcNow();
            var client = httpClientFactory.CreateClient("workflow-token-exchange");
            using var response = await client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, linked.Token);
            await using var stream = await response.Content.ReadAsStreamAsync(linked.Token);
            using var buffer = new MemoryStream();
            var chunk = new byte[4096];
            int read;
            while ((read = await stream.ReadAsync(chunk, linked.Token)) != 0)
            {
                if (buffer.Length + read > 65536)
                    throw Failure(backendId);
                buffer.Write(chunk, 0, read);
            }
            var parsed = JsonSerializer.Deserialize(buffer.GetBuffer().AsSpan(0, (int)buffer.Length),
                GatewayJsonContext.Default.WorkflowOAuthTokenResponse);
            if (!response.IsSuccessStatusCode)
            {
                var denied = (int)response.StatusCode is >= 400 and < 500 &&
                             parsed?.Error is "access_denied" or "invalid_grant" or "interaction_required" or "consent_required" or "login_required";
                throw Failure(backendId, denied);
            }
            if (parsed is null || parsed.Error is not null ||
                parsed.AccessToken is not { Length: > 0 } accessToken || !IsBearerToken(accessToken) ||
                !string.Equals(parsed.TokenType, "Bearer", StringComparison.OrdinalIgnoreCase) ||
                parsed.ExpiresIn is not (>= 1 and <= 86400) ||
                string.Equals(authentication.Mode, "token-exchange", StringComparison.OrdinalIgnoreCase) && parsed.IssuedTokenType != AccessTokenType)
                throw Failure(backendId);
            if (parsed.Scope is not null)
            {
                var returned = parsed.Scope.Split(' ', StringSplitOptions.RemoveEmptyEntries).ToHashSet(StringComparer.Ordinal);
                if (!authentication.Scopes.All(returned.Contains))
                    throw Failure(backendId);
            }
            return new(accessToken, started, timeProvider.GetUtcNow(), parsed.ExpiresIn.Value);
        }
        catch (JsonException)
        {
            throw Failure(backendId);
        }
        catch (SecretResolutionException)
        {
            throw Failure(backendId);
        }
        catch (HttpRequestException)
        {
            throw Failure(backendId);
        }
        catch (IOException)
        {
            throw Failure(backendId);
        }
        catch (InvalidOperationException)
        {
            throw Failure(backendId);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            throw Failure(backendId);
        }
    }

    private WorkflowDelegationException Failure(string backendId, bool denied = false)
    {
        var code = denied ? "workflow_delegation_denied" : "workflow_token_exchange_failed";
        logger.LogWarning("Workflow backend {BackendId} token acquisition failed with {ErrorCode}.", backendId, code);
        return new(code, denied ? StatusCodes.Status403Forbidden : StatusCodes.Status502BadGateway);
    }

    private static string EncodeFormValue(string value)
        => Uri.EscapeDataString(value).Replace("%20", "+", StringComparison.Ordinal);

    private static bool IsBearerToken(string value)
    {
        var token = value.AsSpan().TrimEnd('=');
        if (token.IsEmpty)
            return false;
        foreach (var character in token)
        {
            if (!char.IsAsciiLetterOrDigit(character) && character is not ('-' or '.' or '_' or '~' or '+' or '/'))
                return false;
        }
        return true;
    }
}

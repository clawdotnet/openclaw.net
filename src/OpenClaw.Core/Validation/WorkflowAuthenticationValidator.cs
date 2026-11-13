using OpenClaw.Core.Models;

namespace OpenClaw.Core.Validation;

internal static class WorkflowAuthenticationValidator
{
    public static void Validate(string backendId, WorkflowBackendConfig backend, GatewayConfig gateway, List<string> errors)
    {
        var path = $"Workflows.Backends.{backendId}.Authentication";
        var auth = backend.Authentication;
        if (auth is null)
        {
            errors.Add($"{path} must not be null.");
            return;
        }

        if (string.Equals(auth.Mode, "static", StringComparison.OrdinalIgnoreCase))
        {
            if (auth.TokenEndpoint is not null || auth.ClientId is not null || auth.ClientSecretRef is not null ||
                auth.Audience is not null || auth.Resource is not null || auth.UserAssertionProfile is not null ||
                auth.Scopes is null || auth.Scopes.Length != 0 ||
                auth.AccessTokenClaims is null || auth.AccessTokenClaims.Count != 0 ||
                auth.UserDelegationClaims is null || auth.UserDelegationClaims.Count != 0)
                errors.Add($"{path} must not contain delegation settings in static mode.");
            return;
        }

        var entra = string.Equals(auth.Mode, "entra-obo", StringComparison.OrdinalIgnoreCase);
        if (!entra && !string.Equals(auth.Mode, "token-exchange", StringComparison.OrdinalIgnoreCase))
            errors.Add($"{path}.Mode must be static, entra-obo, or token-exchange.");
        if (!IsSafeHttps(backend.BaseUrl))
            errors.Add($"Workflows.Backends.{backendId}.BaseUrl must be an absolute HTTPS URL without userinfo, query, or fragment.");
        if (!IsSafeHttps(auth.TokenEndpoint))
            errors.Add($"{path}.TokenEndpoint must be an absolute HTTPS URL without userinfo, query, or fragment.");
        if (string.IsNullOrWhiteSpace(auth.ClientId))
            errors.Add($"{path}.ClientId must be set.");
        if (string.IsNullOrWhiteSpace(auth.ClientSecretRef))
            errors.Add($"{path}.ClientSecretRef must be set.");
        if (!string.IsNullOrWhiteSpace(backend.ApiTokenSecret))
            errors.Add($"Workflows.Backends.{backendId}.ApiTokenSecret must not be set in delegated mode.");
        if (string.IsNullOrWhiteSpace(gateway.Security.Oidc.Authority))
            errors.Add($"{path} requires Security.Oidc.Authority.");
        if (string.IsNullOrWhiteSpace(gateway.Security.Oidc.Audience))
            errors.Add($"{path} requires Security.Oidc.Audience.");

        if (auth.Scopes is null || auth.Scopes.Any(scope => string.IsNullOrWhiteSpace(scope) || scope.Any(char.IsWhiteSpace)))
            errors.Add($"{path}.Scopes must contain nonempty individual scope values.");
        if (entra)
        {
            if (auth.Scopes is null || auth.Scopes.Length == 0)
                errors.Add($"{path}.Scopes must be set for entra-obo.");
            if (auth.Audience is not null)
                errors.Add($"{path}.Audience is not supported for entra-obo.");
            if (auth.Resource is not null)
                errors.Add($"{path}.Resource is not supported for entra-obo.");
        }
        else if ((auth.Scopes is null || auth.Scopes.Length == 0) &&
                 string.IsNullOrWhiteSpace(auth.Audience) && string.IsNullOrWhiteSpace(auth.Resource))
            errors.Add($"{path} requires Scopes, Audience, or Resource.");

        if (string.Equals(auth.UserAssertionProfile, "entra-delegated", StringComparison.OrdinalIgnoreCase))
        {
            if (auth.AccessTokenClaims is null || auth.AccessTokenClaims.Count != 0 ||
                auth.UserDelegationClaims is null || auth.UserDelegationClaims.Count != 0)
                errors.Add($"{path} must not contain claim mappings for entra-delegated.");
        }
        else if (string.Equals(auth.UserAssertionProfile, "required-claims", StringComparison.OrdinalIgnoreCase))
        {
            ValidateClaims(auth.AccessTokenClaims, $"{path}.AccessTokenClaims", errors);
            ValidateClaims(auth.UserDelegationClaims, $"{path}.UserDelegationClaims", errors);
        }
        else
            errors.Add($"{path}.UserAssertionProfile must be entra-delegated or required-claims.");
    }

    private static bool IsSafeHttps(string? value)
        => Uri.TryCreate(value, UriKind.Absolute, out var uri) &&
           uri.Scheme == Uri.UriSchemeHttps && string.IsNullOrEmpty(uri.UserInfo) &&
           string.IsNullOrEmpty(uri.Query) && string.IsNullOrEmpty(uri.Fragment);

    private static void ValidateClaims(Dictionary<string, string>? claims, string path, List<string> errors)
    {
        if (claims is null || claims.Count == 0 ||
            claims.Any(pair => string.IsNullOrWhiteSpace(pair.Key) || string.IsNullOrWhiteSpace(pair.Value)))
            errors.Add($"{path} must contain nonempty claim names and values.");
    }
}

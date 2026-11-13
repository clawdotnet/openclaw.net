using System.Security.Claims;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.IdentityModel.JsonWebTokens;
using OpenClaw.Core.Abstractions;
using OpenClaw.Core.Models;

namespace OpenClaw.Gateway.Workflows;

internal sealed class WorkflowCallerContextFactory
{
    private readonly Dictionary<string, AdmissionPolicy> _policies = new(StringComparer.OrdinalIgnoreCase);
    private readonly TimeProvider _timeProvider;

    public WorkflowCallerContextFactory(GatewayConfig config, TimeProvider timeProvider)
    {
        _timeProvider = timeProvider;
        if (!config.Workflows.Enabled)
            return;
        foreach (var (id, backend) in config.Workflows.Backends)
        {
            if (!backend.Enabled)
                continue;
            var auth = backend.Authentication;
            _policies.Add(id.Trim(), new(
                string.Equals(auth.Mode, "static", StringComparison.OrdinalIgnoreCase),
                auth.UserAssertionProfile,
                new(auth.AccessTokenClaims, StringComparer.Ordinal),
                new(auth.UserDelegationClaims, StringComparer.Ordinal)));
        }
    }

    public static Task CaptureValidatedTokenAsync(TokenValidatedContext context)
    {
        ClearValidatedToken(context.HttpContext);
        if (context.Result is not null && !context.Result.Succeeded ||
            context.Principal?.Identity?.IsAuthenticated != true ||
            context.Scheme.Name != JwtBearerDefaults.AuthenticationScheme ||
            context.SecurityToken is not JsonWebToken token ||
            string.IsNullOrWhiteSpace(token.EncodedToken))
            return Task.CompletedTask;

        context.HttpContext.Features.Set(new ValidatedAssertion(
            context.Principal, token.EncodedToken, token.Issuer,
            token.Claims.ToArray(), new DateTimeOffset(token.ValidTo, TimeSpan.Zero)));
        return Task.CompletedTask;
    }

    public static void ClearValidatedToken(HttpContext context)
        => context.Features.Set<ValidatedAssertion>(null);

    public WorkflowExecutionContext? Create(HttpContext? httpContext, string backendId)
    {
        if (string.IsNullOrWhiteSpace(backendId) || !_policies.TryGetValue(backendId.Trim(), out var policy))
            throw new KeyNotFoundException("Workflow backend was not found.");
        if (policy.Static)
            return null;
        var feature = httpContext?.Features.Get<ValidatedAssertion>();
        if (feature is null || !ReferenceEquals(feature.Principal, httpContext!.User) ||
            feature.Principal.Identity?.IsAuthenticated != true ||
            feature.ExpiresAtUtc <= _timeProvider.GetUtcNow() ||
            string.IsNullOrWhiteSpace(feature.Issuer) ||
            !TryGetSingle(feature.Claims, "iss", out var issuer) || issuer != feature.Issuer ||
            !TryGetSingle(feature.Claims, "sub", out var subject) || string.IsNullOrWhiteSpace(subject) ||
            !TryGetSingle(feature.Claims, "exp", out _) ||
            !Admits(policy, feature.Claims))
        {
            httpContext?.RequestServices?.GetService<ILogger<WorkflowCallerContextFactory>>()?
                .LogWarning("Workflow caller admission was rejected with {ErrorCode}.",
                    "workflow_user_auth_required");
            throw new WorkflowDelegationException("workflow_user_auth_required", StatusCodes.Status401Unauthorized);
        }

        return new WorkflowExecutionContext(backendId.Trim(), feature.Issuer, subject, feature.ExpiresAtUtc, feature.Assertion);
    }

    private static bool Admits(AdmissionPolicy policy, Claim[] claims)
    {
        if (string.Equals(policy.Profile, "entra-delegated", StringComparison.OrdinalIgnoreCase))
        {
            if (!TryGetSingle(claims, "scp", out var scope) || string.IsNullOrWhiteSpace(scope))
                return false;
            var types = claims.Where(claim => claim.Type == "idtyp").ToArray();
            return types.Length == 0 ||
                   (types.Length == 1 && types[0].Value != "app");
        }
        return string.Equals(policy.Profile, "required-claims", StringComparison.OrdinalIgnoreCase) &&
               Matches(policy.AccessTokenClaims, claims) && Matches(policy.UserDelegationClaims, claims);
    }

    private static bool Matches(Dictionary<string, string> required, Claim[] claims)
        => required.Count != 0 &&
           required.All(pair => TryGetSingle(claims, pair.Key, out var value) && value == pair.Value);

    private static bool TryGetSingle(Claim[] claims, string name, out string value)
    {
        var matches = claims.Where(claim => claim.Type == name).ToArray();
        value = matches.Length == 1 ? matches[0].Value : "";
        return matches.Length == 1 && matches[0].ValueType == ClaimValueTypes.String ||
               matches.Length == 1 && name == "exp" && matches[0].ValueType == ClaimValueTypes.Integer64;
    }

    private sealed record AdmissionPolicy(bool Static, string? Profile,
        Dictionary<string, string> AccessTokenClaims, Dictionary<string, string> UserDelegationClaims);

    private sealed class ValidatedAssertion(ClaimsPrincipal principal, string assertion, string issuer, Claim[] claims, DateTimeOffset expiresAtUtc)
    {
        public ClaimsPrincipal Principal { get; } = principal;
        public string Assertion { get; } = assertion;
        public string Issuer { get; } = issuer;
        public Claim[] Claims { get; } = claims;
        public DateTimeOffset ExpiresAtUtc { get; } = expiresAtUtc;
        public override string ToString() => "[REDACTED]";
    }
}

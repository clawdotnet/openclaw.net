using System.Security.Claims;
using Microsoft.IdentityModel.JsonWebTokens;
using Microsoft.IdentityModel.Tokens;
using OpenClaw.Core.Models;
using Xunit;

namespace OpenClaw.Tests;

/// <summary>
/// Pins how role claims from an external IdP (Keycloak in practice) are resolved
/// into an <see cref="OperatorRoleNames"/> value.
///
/// Why this matters: EndpointHelpers reads the role with
/// <c>ctx.User.FindFirstValue(Oidc.RoleClaim)</c>, which returns only the FIRST
/// claim of that type. Keycloak emits realm roles as a JSON array, and the JWT
/// handler flattens arrays into one claim per element — so a user holding
/// ["app_user", "app_admin"] resolves to "app_user" and, since
/// <see cref="OperatorRoleNames.Normalize"/> maps unknown values to viewer,
/// that admin is silently downgraded to viewer and cannot even chat.
///
/// Consequence for configuration (documented in docs/AUTHENTICATION.md): the
/// Keycloak side must emit a single-valued claim for the operator role. These
/// tests pin that a single value works and that the multi-value case is the
/// trap — so a future change to role resolution has to face the trade-off.
/// </summary>
public sealed class OidcRoleClaimResolutionTests
{
    private static ClaimsPrincipal PrincipalFromToken(string payloadJson)
    {
        var key = new SymmetricSecurityKey(
            System.Text.Encoding.UTF8.GetBytes("0123456789abcdef0123456789abcdef"));
        var descriptor = new SecurityTokenDescriptor
        {
            Claims = System.Text.Json.JsonSerializer.Deserialize<Dictionary<string, object>>(payloadJson)!,
            SigningCredentials = new SigningCredentials(key, SecurityAlgorithms.HmacSha256),
        };
        var jwt = new JsonWebTokenHandler().CreateToken(descriptor);
        var parsed = new JsonWebTokenHandler().ReadJsonWebToken(jwt);
        return new ClaimsPrincipal(new ClaimsIdentity(parsed.Claims, "oidc"));
    }

    [Theory]
    [InlineData("admin", OperatorRoleNames.Admin)]
    [InlineData("operator", OperatorRoleNames.Operator)]
    [InlineData("viewer", OperatorRoleNames.Viewer)]
    public void SingleValuedRoleClaim_MapsToExpectedRole(string claimValue, string expectedRole)
    {
        var principal = PrincipalFromToken($$"""{"sub":"u1","roles":"{{claimValue}}"}""");

        var rawRole = principal.FindFirstValue("roles");
        Assert.Equal(expectedRole, OperatorRoleNames.Normalize(rawRole));
    }

    [Fact]
    public void SingleValuedCustomClaimName_IsWhatKeycloakMustEmit()
    {
        // The recommended Keycloak setup: a dedicated single-valued claim
        // (e.g. "operator_role") populated from a realm role via a
        // hardcoded-claim or user-attribute mapper. This is the shape that works.
        var principal = PrincipalFromToken("""{"sub":"u1","operator_role":"admin"}""");

        Assert.Equal(OperatorRoleNames.Admin,
            OperatorRoleNames.Normalize(principal.FindFirstValue("operator_role")));
    }

    [Fact]
    public void ArrayRoleClaim_HighestRoleWins_AdminIsNotLost()
    {
        // Regression guard for the silent-downgrade bug.
        //
        // A multi-role identity produces one claim per element. The old resolver
        // used FindFirstValue, so ["viewer","admin"] resolved to "viewer" and the
        // admin lost every admin-only endpoint with no error anywhere. Resolution
        // must now take the highest-ranked role present, independent of order.
        var principal = PrincipalFromToken(
            """{"sub":"u1","realm_roles":["viewer","admin"]}""");

        var all = principal.FindAll("realm_roles").Select(c => c.Value).ToArray();
        Assert.Equal(["viewer", "admin"], all);

        var resolved = OperatorRoleNames.FromRoles(all);
        Assert.Equal(OperatorRoleNames.Admin, resolved);
        Assert.True(OperatorRoleNames.CanAccess(resolved, OperatorRoleNames.Admin));
    }

    [Fact]
    public void ArrayRoleClaim_OrderDoesNotMatter()
    {
        // The same set in the opposite order must resolve identically; otherwise
        // the result depends on token serialization order.
        var reversed = PrincipalFromToken(
            """{"sub":"u1","realm_roles":["admin","viewer"]}""");

        var resolved = OperatorRoleNames.FromRoles(
            reversed.FindAll("realm_roles").Select(c => c.Value));

        Assert.Equal(OperatorRoleNames.Admin, resolved);
    }

    [Fact]
    public void UnknownRoleNames_AreIgnored_NotMappedToAdmin()
    {
        // Free-form names must never be guessed at. "app_admin" is NOT "admin";
        // a fuzzy match would also promote "app_administrator" or "not_admin".
        // The IdP is responsible for emitting viewer/operator/admin through a
        // dedicated claim (see docs/KEYCLOAK.md).
        Assert.Equal(OperatorRoleNames.Viewer,
            OperatorRoleNames.FromRoles("app_admin", "openclaw_admin"));

        // A real role beside unknown ones still wins.
        Assert.Equal(OperatorRoleNames.Admin,
            OperatorRoleNames.FromRoles("app_user", "admin", "some_unknown"));
        Assert.Equal(OperatorRoleNames.Operator,
            OperatorRoleNames.FromRoles("app_user", "operator"));
    }

    [Fact]
    public void EmptyOrAllUnknownRoleSet_StillFailsClosedToViewer()
    {
        // Authorization must never open up because a claim was empty. Note
        // FromRoles(null) also yields viewer, so a provider that omits the claim
        // entirely keeps the documented behaviour.
        Assert.Equal(OperatorRoleNames.Viewer, OperatorRoleNames.FromRoles());
        IEnumerable<string?>? nullRoles = null;
        Assert.Equal(OperatorRoleNames.Viewer, OperatorRoleNames.FromRoles(nullRoles));
        Assert.Equal(OperatorRoleNames.Viewer, OperatorRoleNames.FromRoles("", "   ", null));
        Assert.Equal(OperatorRoleNames.Viewer,
            OperatorRoleNames.FromRoles("app_user", "some_unknown_role"));
        Assert.False(OperatorRoleNames.CanAccess(
            OperatorRoleNames.FromRoles("unknown"), OperatorRoleNames.Operator));
    }

    [Fact]
    public void CaseAndWhitespaceVariants_AreRecognized()
    {
        // IdPs are inconsistent about casing; "Admin" and " ADMIN " must not
        // collapse to viewer.
        Assert.Equal(OperatorRoleNames.Admin, OperatorRoleNames.FromRoles("Admin"));
        Assert.Equal(OperatorRoleNames.Admin, OperatorRoleNames.FromRoles("  ADMIN  "));
        Assert.Equal(OperatorRoleNames.Operator, OperatorRoleNames.FromRoles("Operator"));
    }

    [Fact]
    public void MissingRoleClaim_DowngradesToViewer()
    {
        // docs/AUTHENTICATION.md: "A JWT without the configured RoleClaim
        // resolves to viewer and cannot chat." Pinned so the failure mode is
        // explicit rather than a surprise in production.
        var principal = PrincipalFromToken("""{"sub":"u1"}""");

        Assert.Null(principal.FindFirstValue("roles"));
        Assert.Equal(OperatorRoleNames.Viewer,
            OperatorRoleNames.Normalize(principal.FindFirstValue("roles")));
    }

    [Fact]
    public void KeycloakDefaultClaims_MapToAccountIdAndUsername()
    {
        // EndpointHelpers prefers ClaimTypes.NameIdentifier then "sub" for
        // AccountId, and preferred_username for Username. Keycloak's default
        // token carries both, so no custom mapper is needed for identity.
        var principal = PrincipalFromToken(
            """{"sub":"d0000000-0000-4000-8000-000000000001","preferred_username":"demo","email":"demo@example.com"}""");

        var accountId = principal.FindFirstValue(ClaimTypes.NameIdentifier)
                        ?? principal.FindFirstValue("sub");
        var username = principal.FindFirstValue("preferred_username")
                       ?? principal.FindFirstValue(ClaimTypes.Name)
                       ?? accountId;

        Assert.Equal("d0000000-0000-4000-8000-000000000001", accountId);
        Assert.Equal("demo", username);
    }
}

using Microsoft.Extensions.DependencyInjection;
using OpenClaw.Core.Models;
using OpenClaw.Gateway.Bootstrap;
using OpenClaw.Gateway.Composition;
using Xunit;

namespace OpenClaw.Tests;

/// <summary>
/// Guards the OIDC service-registration contract between Program.cs and
/// SecurityServicesExtensions.
///
/// Program.cs calls <c>UseAuthentication()</c> when
/// <c>IsOidcMode || !string.IsNullOrWhiteSpace(Oidc.Authority)</c>, but the
/// scheme provider is only registered when Authority is non-blank. Without an
/// explicit guard, "AuthMode=oidc, Authority empty" starts the host and then
/// dies inside the middleware pipeline with
/// <c>UnableToResolveService ... IAuthenticationSchemeProvider</c>, which gives
/// no hint about the actual misconfiguration.
/// </summary>
public sealed class OidcServiceRegistrationTests
{
    private static GatewayStartupContext Startup(OidcConfig oidc, string authMode)
        => new()
        {
            Config = new GatewayConfig
            {
                Security = new SecurityConfig
                {
                    AuthMode = authMode,
                    Oidc = oidc
                }
            },
            RuntimeState = new GatewayRuntimeState
            {
                RequestedMode = "jit",
                EffectiveMode = GatewayRuntimeMode.Jit,
                DynamicCodeSupported = true
            },
            IsNonLoopbackBind = false
        };

    [Fact]
    public void OidcMode_WithoutAuthority_ThrowsInsteadOfFailingDeepInPipeline()
    {
        var services = new ServiceCollection();
        services.AddLogging();
        var startup = Startup(new OidcConfig { Authority = "" }, SecurityAuthModeNames.Oidc);

        var ex = Assert.Throws<InvalidOperationException>(
            () => services.AddOpenClawSecurityServices(startup));

        // The message must name both the setting and the expected shape, otherwise
        // operators cannot act on it. Assert the actionable parts, not the whole
        // sentence (wording is allowed to change).
        Assert.Contains("Security.Oidc.Authority", ex.Message, StringComparison.Ordinal);
        Assert.Contains("realms", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void OidcMode_WithWhitespaceAuthority_AlsoThrows()
    {
        // IsNullOrWhiteSpace is the actual predicate used elsewhere; a blank-but-
        // present value must not be treated as configured.
        var services = new ServiceCollection();
        services.AddLogging();
        var startup = Startup(new OidcConfig { Authority = "   " }, SecurityAuthModeNames.Oidc);

        Assert.Throws<InvalidOperationException>(
            () => services.AddOpenClawSecurityServices(startup));
    }

    [Fact]
    public void TokenMode_WithoutAuthority_DoesNotThrow()
    {
        // Control case: the guard must only fire for OIDC mode. Token mode has
        // always worked without an Authority and must keep working.
        var services = new ServiceCollection();
        services.AddLogging();
        var startup = Startup(new OidcConfig { Authority = "" }, SecurityAuthModeNames.Token);

        services.AddOpenClawSecurityServices(startup);
    }

    [Fact]
    public void OidcMode_WithAuthority_RegistersAuthenticationSchemeProvider()
    {
        var services = new ServiceCollection();
        services.AddLogging();
        var startup = Startup(
            new OidcConfig { Authority = "https://keycloak.example.com/realms/supabase" },
            SecurityAuthModeNames.Oidc);

        services.AddOpenClawSecurityServices(startup);

        using var provider = services.BuildServiceProvider();
        // This is the service whose absence produced the original crash.
        Assert.NotNull(provider.GetService<
            Microsoft.AspNetCore.Authentication.IAuthenticationSchemeProvider>());
    }

    [Fact]
    public void TokenMode_WithAuthority_StillRegistersJwtBearer()
    {
        // Hybrid mode documented in docs/AUTHENTICATION.md Scenario 4: AuthMode is
        // "token" but an Authority is set so JWT callers are also accepted.
        var services = new ServiceCollection();
        services.AddLogging();
        var startup = Startup(
            new OidcConfig { Authority = "https://keycloak.example.com/realms/supabase" },
            SecurityAuthModeNames.Token);

        services.AddOpenClawSecurityServices(startup);

        using var provider = services.BuildServiceProvider();
        Assert.NotNull(provider.GetService<
            Microsoft.AspNetCore.Authentication.IAuthenticationSchemeProvider>());
    }
}

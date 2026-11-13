using System.Text.Json;
using OpenClaw.Core.Models;
using OpenClaw.Core.Validation;
using Xunit;

namespace OpenClaw.Tests;

public sealed class WorkflowAuthenticationConfigTests
{
    [Fact]
    public void Deserialize_WhenAuthenticationOmitted_ShouldPreserveStaticMode()
    {
        var backend = JsonSerializer.Deserialize("""{"baseUrl":"http://localhost:5095","apiTokenSecret":"env:OLD_TOKEN"}""",
            CoreJsonContext.Default.WorkflowBackendConfig)!;

        Assert.Equal("static", backend.Authentication.Mode);
        Assert.Equal("env:OLD_TOKEN", backend.ApiTokenSecret);
        Assert.DoesNotContain(ConfigValidator.Validate(CreateConfig(backend)), IsWorkflowError);
    }

    [Theory]
    [InlineData("entra-obo", "entra-delegated")]
    [InlineData("token-exchange", "required-claims")]
    [InlineData("token-exchange", "entra-delegated")]
    [InlineData("entra-obo", "required-claims")]
    public void Validate_WhenDelegatedPolicyValid_ShouldAcceptIndependentProfile(string mode, string profile)
    {
        var backend = CreateBackend(mode, profile);
        var json = JsonSerializer.Serialize(backend, CoreJsonContext.Default.WorkflowBackendConfig);
        var restored = JsonSerializer.Deserialize(json, CoreJsonContext.Default.WorkflowBackendConfig)!;

        Assert.Equal(mode, restored.Authentication.Mode);
        Assert.Equal(profile, restored.Authentication.UserAssertionProfile);
        Assert.Equal(backend.Authentication.AccessTokenClaims, restored.Authentication.AccessTokenClaims);
        Assert.DoesNotContain(ConfigValidator.Validate(CreateConfig(restored)), IsWorkflowError);
    }

    [Theory]
    [InlineData("http://identity.example/token")]
    [InlineData("/token")]
    [InlineData("https://user:password@identity.example/token")]
    [InlineData("https://identity.example/token?secret=marker")]
    [InlineData("https://identity.example/token#fragment")]
    public void Validate_WhenTokenEndpointUnsafe_ShouldRejectWithoutExposingValue(string endpoint)
    {
        var backend = CreateBackend();
        backend.Authentication.TokenEndpoint = endpoint;
        var errors = ConfigValidator.Validate(CreateConfig(backend));

        Assert.Contains(errors, error => error.Contains(".Authentication.TokenEndpoint", StringComparison.Ordinal));
        Assert.DoesNotContain(errors, error => error.Contains(endpoint, StringComparison.Ordinal));
    }

    [Theory]
    [InlineData("BaseUrl")]
    [InlineData("Mode")]
    [InlineData("ClientId")]
    [InlineData("ClientSecretRef")]
    [InlineData("Scopes")]
    [InlineData("Audience")]
    [InlineData("Resource")]
    [InlineData("UserAssertionProfile")]
    [InlineData("ApiTokenSecret")]
    [InlineData("Authority")]
    [InlineData("OidcAudience")]
    [InlineData("Authentication")]
    public void Validate_WhenDelegatedConfigurationInvalid_ShouldReject(string field)
    {
        var backend = CreateBackend();
        var config = CreateConfig(backend);
        switch (field)
        {
            case "BaseUrl": backend.BaseUrl = "http://workflow.example"; break;
            case "Mode": backend.Authentication.Mode = "unknown"; break;
            case "ClientId": backend.Authentication.ClientId = ""; break;
            case "ClientSecretRef": backend.Authentication.ClientSecretRef = ""; break;
            case "Scopes": backend.Authentication.Scopes = [""]; break;
            case "Audience": backend.Authentication.Audience = "forbidden"; break;
            case "Resource": backend.Authentication.Resource = "forbidden"; break;
            case "UserAssertionProfile": backend.Authentication.UserAssertionProfile = "unknown"; break;
            case "ApiTokenSecret": backend.ApiTokenSecret = "raw:secret-marker"; break;
            case "Authority": config.Security.Oidc.Authority = ""; break;
            case "OidcAudience": config.Security.Oidc.Audience = ""; break;
            case "Authentication": backend.Authentication = null!; break;
        }

        var errors = ConfigValidator.Validate(config);
        Assert.Contains(errors, IsWorkflowError);
        Assert.DoesNotContain(errors, error => error.Contains("secret-marker", StringComparison.Ordinal));
    }

    [Fact]
    public void Validate_WhenRfcHasNoTarget_ShouldReject()
    {
        var backend = CreateBackend("token-exchange");
        backend.Authentication.Scopes = [];
        Assert.Contains(ConfigValidator.Validate(CreateConfig(backend)), IsWorkflowError);
    }

    [Theory]
    [InlineData("AccessTokenClaims")]
    [InlineData("UserDelegationClaims")]
    public void Validate_WhenRequiredClaimsMissing_ShouldReject(string field)
    {
        var backend = CreateBackend("token-exchange", "required-claims");
        if (field == "AccessTokenClaims")
            backend.Authentication.AccessTokenClaims.Clear();
        else
            backend.Authentication.UserDelegationClaims.Clear();
        Assert.Contains(ConfigValidator.Validate(CreateConfig(backend)), error => error.Contains(field, StringComparison.Ordinal));
    }

    [Theory]
    [InlineData("", "user")]
    [InlineData("actor", "")]
    [InlineData("actor", "  ")]
    public void Validate_WhenClaimMappingInvalid_ShouldReject(string name, string value)
    {
        var backend = CreateBackend("token-exchange", "required-claims");
        backend.Authentication.UserDelegationClaims = new() { [name] = value };
        Assert.Contains(ConfigValidator.Validate(CreateConfig(backend)), IsWorkflowError);
    }

    [Fact]
    public void Validate_WhenEntraProfileHasClaimMappings_ShouldReject()
    {
        var backend = CreateBackend();
        backend.Authentication.AccessTokenClaims["typ"] = "Bearer";
        Assert.Contains(ConfigValidator.Validate(CreateConfig(backend)), IsWorkflowError);
    }

    [Fact]
    public void Validate_WhenStaticModeHasDelegationFields_ShouldReject()
    {
        var backend = CreateBackend();
        backend.Authentication.Mode = "static";
        Assert.Contains(ConfigValidator.Validate(CreateConfig(backend)), IsWorkflowError);
    }

    private static bool IsWorkflowError(string error) => error.StartsWith("Workflows.", StringComparison.Ordinal);

    private static GatewayConfig CreateConfig(WorkflowBackendConfig backend) => new()
    {
        Security = new SecurityConfig
        {
            Oidc = new OidcConfig { Authority = "https://identity.example", Audience = "gateway-api" }
        },
        Workflows = new WorkflowsConfig { Enabled = true, Backends = { ["review"] = backend } }
    };

    private static WorkflowBackendConfig CreateBackend(string mode = "entra-obo", string profile = "entra-delegated") => new()
    {
        BaseUrl = "https://workflow.example",
        Authentication = new WorkflowAuthenticationConfig
        {
            Mode = mode,
            TokenEndpoint = "https://identity.example/token",
            ClientId = "gateway-client",
            ClientSecretRef = "env:WORKFLOW_CLIENT_SECRET",
            Scopes = ["workflow.read"],
            UserAssertionProfile = profile,
            AccessTokenClaims = profile == "required-claims" ? new() { ["typ"] = "Bearer" } : [],
            UserDelegationClaims = profile == "required-claims" ? new() { ["actor_kind"] = "user" } : []
        }
    };
}

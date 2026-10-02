using System.Net;
using System.Reflection;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.AspNetCore.WebUtilities;
using Microsoft.IdentityModel.JsonWebTokens;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Http;
using Microsoft.Extensions.Logging;
using Microsoft.IdentityModel.Tokens;
using OpenClaw.Core.Models;
using OpenClaw.Core.Plugins;
using OpenClaw.Core.Security;
using OpenClaw.Gateway.Bootstrap;
using OpenClaw.Gateway.Composition;
using OpenClaw.Gateway.Security;
using Xunit;

namespace OpenClaw.Tests;

public sealed class McpDelegatedCredentialProviderTests
{
    [Fact]
    public void AddOpenClawSecurityServices_RegistersMcpDelegatedCredentialProvider()
    {
        var startup = new GatewayStartupContext
        {
            Config = new GatewayConfig(),
            RuntimeState = new GatewayRuntimeState
            {
                RequestedMode = "jit",
                EffectiveMode = GatewayRuntimeMode.Jit,
                DynamicCodeSupported = true
            },
            IsNonLoopbackBind = false
        };
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddOpenClawSecurityServices(startup);

        using var provider = services.BuildServiceProvider();

        Assert.IsType<McpDelegatedCredentialProvider>(provider.GetRequiredService<IMcpDelegatedCredentialProvider>());
    }

    [Fact]
    public void AddOpenClawSecurityServices_DisablesTokenExchangeRedirects()
    {
        var startup = new GatewayStartupContext
        {
            Config = new GatewayConfig(),
            RuntimeState = new GatewayRuntimeState
            {
                RequestedMode = "jit",
                EffectiveMode = GatewayRuntimeMode.Jit,
                DynamicCodeSupported = true
            },
            IsNonLoopbackBind = false
        };
        var handlerFilter = new CapturingHttpMessageHandlerBuilderFilter();
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddOpenClawSecurityServices(startup);
        services.AddSingleton<IHttpMessageHandlerBuilderFilter>(handlerFilter);

        using var provider = services.BuildServiceProvider();
        using var httpClient = provider.GetRequiredService<IHttpClientFactory>()
            .CreateClient(nameof(IMcpDelegatedCredentialProvider));

        var redirectsEnabled = handlerFilter.PrimaryHandler switch
        {
            HttpClientHandler handler => handler.AllowAutoRedirect,
            SocketsHttpHandler handler => handler.AllowAutoRedirect,
            _ => throw new InvalidOperationException("The token exchange client has an unexpected primary handler.")
        };
        Assert.False(redirectsEnabled);
    }

    [Fact]
    public async Task TokenExchange_PostsConfiguredGrantAndReturnsCredential()
    {
        Dictionary<string, string>? submittedFields = null;
        var handler = new CallbackHandler(async (request, ct) =>
        {
            Assert.Equal(HttpMethod.Post, request.Method);
            Assert.Equal(new Uri("https://identity.example/token"), request.RequestUri);
            Assert.Equal("application/x-www-form-urlencoded", request.Content?.Headers.ContentType?.MediaType);
            var form = QueryHelpers.ParseQuery(await request.Content!.ReadAsStringAsync(ct));
            submittedFields = form.ToDictionary(pair => pair.Key, pair => pair.Value.ToString(), StringComparer.Ordinal);
            return Json(HttpStatusCode.OK, """{"access_token":"delegated-access-token","expires_in":300,"token_type":"Bearer"}""");
        });
        using var httpClient = new HttpClient(handler);
        var provider = new McpDelegatedCredentialProvider(httpClient, new CapturingLogger<McpDelegatedCredentialProvider>());
        var policy = CreateTokenExchangePolicy();
        var caller = new McpCallerCredentialContext("caller-oidc-token", "caller-subject", DateTimeOffset.UtcNow.AddMinutes(5));

        var credential = await provider.GetCredentialAsync(policy, caller, CancellationToken.None);

        Assert.Equal("delegated-access-token", credential.AccessToken);
        Assert.True(credential.ExpiresAtUtc > DateTimeOffset.UtcNow);
        Assert.NotNull(submittedFields);
        Assert.Equal(
            new[] { "audience", "client_id", "client_secret", "grant_type", "scope", "subject_token", "subject_token_type" }.Order(StringComparer.Ordinal),
            submittedFields.Keys.Order(StringComparer.Ordinal));
        Assert.Equal("strategos", submittedFields["audience"]);
        Assert.Equal("openclaw-gateway", submittedFields["client_id"]);
        Assert.Equal("client-secret-value", submittedFields["client_secret"]);
        Assert.Equal("urn:ietf:params:oauth:grant-type:token-exchange", submittedFields["grant_type"]);
        Assert.Equal("inventory.read reports.read", submittedFields["scope"]);
        Assert.Equal("caller-oidc-token", submittedFields["subject_token"]);
        Assert.Equal("urn:ietf:params:oauth:token-type:access_token", submittedFields["subject_token_type"]);
    }

    [Theory]
    [InlineData("{\"diagnostic\":\"response-body-marker\"")]
    [InlineData("{\"diagnostic\":\"response-body-marker\"}")]
    [InlineData("{\"access_token\":\"hidden-token\",\"expires_in\":0,\"diagnostic\":\"response-body-marker\"}")]
    public async Task TokenExchange_InvalidResponses_FailWithoutExposingResponseBody(string responseBody)
    {
        var logger = new CapturingLogger<McpDelegatedCredentialProvider>();
        using var httpClient = new HttpClient(new CallbackHandler((_, _) => Task.FromResult(Json(HttpStatusCode.OK, responseBody))));
        var provider = new McpDelegatedCredentialProvider(httpClient, logger);

        var error = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            provider.GetCredentialAsync(CreateTokenExchangePolicy(), CreateCallerCredential(), CancellationToken.None));

        Assert.DoesNotContain("response-body-marker", error.ToString(), StringComparison.Ordinal);
        Assert.DoesNotContain(logger.Messages, message => message.Contains("response-body-marker", StringComparison.Ordinal));
    }

    [Fact]
    public async Task TokenExchange_NonSuccess_FailsWithoutLoggingResponseBody()
    {
        var logger = new CapturingLogger<McpDelegatedCredentialProvider>();
        using var httpClient = new HttpClient(new CallbackHandler((_, _) =>
            Task.FromResult(Json(HttpStatusCode.BadGateway, "response-body-marker"))));
        var provider = new McpDelegatedCredentialProvider(httpClient, logger);

        var error = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            provider.GetCredentialAsync(CreateTokenExchangePolicy(), CreateCallerCredential(), CancellationToken.None));

        Assert.DoesNotContain("response-body-marker", error.ToString(), StringComparison.Ordinal);
        Assert.DoesNotContain(logger.Messages, message => message.Contains("response-body-marker", StringComparison.Ordinal));
        Assert.Contains(logger.Messages, message => message.Contains("502", StringComparison.Ordinal));
    }

    [Fact]
    public async Task TokenExchange_Cancellation_Propagates()
    {
        using var httpClient = new HttpClient(new CallbackHandler((_, _) =>
            Task.FromResult(Json(HttpStatusCode.OK, "{}"))));
        var provider = new McpDelegatedCredentialProvider(httpClient, new CapturingLogger<McpDelegatedCredentialProvider>());
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            provider.GetCredentialAsync(CreateTokenExchangePolicy(), CreateCallerCredential(), cancellation.Token));
    }

    [Fact]
    public async Task UnsupportedMode_FailsWithoutSendingRequest()
    {
        var requestCount = 0;
        using var httpClient = new HttpClient(new CallbackHandler((_, _) =>
        {
            requestCount++;
            return Task.FromResult(Json(HttpStatusCode.OK, "{}"));
        }));
        var provider = new McpDelegatedCredentialProvider(httpClient, new CapturingLogger<McpDelegatedCredentialProvider>());
        var policy = CreateTokenExchangePolicy();
        policy.Mode = "unknown_mode";

        var error = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            provider.GetCredentialAsync(policy, CreateCallerCredential(), CancellationToken.None));

        Assert.Contains("mode", error.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Equal(0, requestCount);
    }

    [Fact]
    public async Task GatewaySigned_EmitsCallerBoundClaimsAndValidSignature()
    {
        const string signingSecret = "ephemeral-test-signing-key-with-32-bytes-minimum";
        using var httpClient = new HttpClient(new CallbackHandler((_, _) =>
            throw new InvalidOperationException("Gateway-signed mode must not call the token endpoint.")));
        var provider = new McpDelegatedCredentialProvider(httpClient, new CapturingLogger<McpDelegatedCredentialProvider>());
        var policy = CreateGatewaySignedPolicy(signingSecret);
        var caller = new McpCallerCredentialContext("caller-token", "strategos-user-42", DateTimeOffset.UtcNow.AddMinutes(5));

        var credential = await provider.GetCredentialAsync(policy, caller, CancellationToken.None);
        var handler = new JsonWebTokenHandler();
        var validation = await handler.ValidateTokenAsync(credential.AccessToken, new TokenValidationParameters
        {
            ValidateIssuer = true,
            ValidIssuer = "https://gateway.example",
            ValidateAudience = true,
            ValidAudience = "strategos",
            ValidateIssuerSigningKey = true,
            IssuerSigningKey = new SymmetricSecurityKey(Encoding.UTF8.GetBytes(signingSecret)),
            ValidateLifetime = true,
            ClockSkew = TimeSpan.Zero
        });

        Assert.True(validation.IsValid, validation.Exception?.ToString());
        var token = handler.ReadJsonWebToken(credential.AccessToken);
        Assert.Equal("https://gateway.example", token.Issuer);
        Assert.Equal("strategos-user-42", token.Subject);
        Assert.Equal("strategos", Assert.Single(token.Audiences));
        Assert.Equal("inventory.read reports.read", token.Claims.Single(claim => claim.Type == "scope").Value);

        var issuedAtSeconds = long.Parse(token.Claims.Single(claim => claim.Type == "iat").Value, System.Globalization.CultureInfo.InvariantCulture);
        var expiresAtSeconds = long.Parse(token.Claims.Single(claim => claim.Type == "exp").Value, System.Globalization.CultureInfo.InvariantCulture);
        Assert.True(expiresAtSeconds <= issuedAtSeconds + policy.LifetimeSeconds);
        Assert.True(credential.ExpiresAtUtc <= caller.ExpiresAtUtc);
        Assert.Equal(DateTimeOffset.FromUnixTimeSeconds(expiresAtSeconds), credential.ExpiresAtUtc);
    }

    [Fact]
    public async Task GatewaySigned_MissingSigningKey_FailsWithoutCredential()
    {
        using var httpClient = new HttpClient(new CallbackHandler((_, _) =>
            throw new InvalidOperationException("Gateway-signed mode must not call the token endpoint.")));
        var provider = new McpDelegatedCredentialProvider(httpClient, new CapturingLogger<McpDelegatedCredentialProvider>());
        var policy = CreateGatewaySignedPolicy("unused");
        policy.SigningKeyRef = null;

        var error = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            provider.GetCredentialAsync(policy, CreateCallerCredential(), CancellationToken.None));

        Assert.Contains("signing key", error.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task GatewaySigned_InvalidLifetime_FailsWithoutCredential()
    {
        using var httpClient = new HttpClient(new CallbackHandler((_, _) =>
            throw new InvalidOperationException("Gateway-signed mode must not call the token endpoint.")));
        var provider = new McpDelegatedCredentialProvider(httpClient, new CapturingLogger<McpDelegatedCredentialProvider>());
        var policy = CreateGatewaySignedPolicy("ephemeral-test-signing-key-with-32-bytes-minimum");
        policy.LifetimeSeconds = 0;

        var error = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            provider.GetCredentialAsync(policy, CreateCallerCredential(), CancellationToken.None));

        Assert.Contains("lifetime", error.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task GatewaySigned_ExpiredCallerCredential_FailsWithoutCredential()
    {
        using var httpClient = new HttpClient(new CallbackHandler((_, _) =>
            throw new InvalidOperationException("Gateway-signed mode must not call the token endpoint.")));
        var provider = new McpDelegatedCredentialProvider(httpClient, new CapturingLogger<McpDelegatedCredentialProvider>());
        var caller = new McpCallerCredentialContext("caller-token", "caller-subject", DateTimeOffset.UtcNow.AddSeconds(-1));

        var error = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            provider.GetCredentialAsync(CreateGatewaySignedPolicy("ephemeral-test-signing-key-with-32-bytes-minimum"), caller, CancellationToken.None));

        Assert.Contains("caller", error.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void CredentialValues_JsonAndToString_RedactBearerTokens()
    {
        const string callerTokenMarker = "caller-token-marker-3f8d";
        const string delegatedTokenMarker = "delegated-token-marker-a71c";
        var caller = new McpCallerCredentialContext(
            callerTokenMarker,
            "caller-subject",
            DateTimeOffset.UtcNow.AddMinutes(5));
        var delegated = new McpDelegatedCredential(
            delegatedTokenMarker,
            DateTimeOffset.UtcNow.AddMinutes(1));

        var formattedValues = new[]
        {
            caller.ToString(),
            JsonSerializer.Serialize(caller),
            delegated.ToString(),
            JsonSerializer.Serialize(delegated)
        };

        foreach (var formattedValue in formattedValues)
        {
            Assert.DoesNotContain(callerTokenMarker, formattedValue, StringComparison.Ordinal);
            Assert.DoesNotContain(delegatedTokenMarker, formattedValue, StringComparison.Ordinal);
        }
    }

    [Fact]
    public void DelegatedToolCallContracts_ExposeOnlyDeclaredFieldsAndDoNotSerializeCallerContext()
    {
        var requestProperties = typeof(McpDelegatedToolCallRequest)
            .GetProperties(BindingFlags.Instance | BindingFlags.Public)
            .Select(property => property.Name)
            .Order(StringComparer.Ordinal)
            .ToArray();
        var resultProperties = typeof(McpDelegatedToolCallResult)
            .GetProperties(BindingFlags.Instance | BindingFlags.Public)
            .Select(property => property.Name)
            .Order(StringComparer.Ordinal)
            .ToArray();

        Assert.Equal(
            new[]
            {
                "ArgumentsJson",
                "CallerCredentialContext",
                "Endpoint",
                "EndpointId",
                "Policy",
                "RemoteToolName",
                "RequestTimeoutSeconds",
                "StaticHeaders",
                "SuppressStructuredContent"
            }.Order(StringComparer.Ordinal),
            requestProperties);
        Assert.Equal(
            new[] { "IsError", "ResponseText" }.Order(StringComparer.Ordinal),
            resultProperties);

        var callerContextProperty = typeof(McpDelegatedToolCallRequest)
            .GetProperty(nameof(McpDelegatedToolCallRequest.CallerCredentialContext));
        Assert.NotNull(callerContextProperty);
        Assert.NotNull(callerContextProperty.GetCustomAttribute<JsonIgnoreAttribute>());

        var invokeMethod = typeof(IMcpDelegatedToolInvoker).GetMethod(nameof(IMcpDelegatedToolInvoker.InvokeAsync));
        Assert.NotNull(invokeMethod);
        Assert.Equal(typeof(Task<McpDelegatedToolCallResult>), invokeMethod.ReturnType);
        Assert.Equal(
            new[] { typeof(McpDelegatedToolCallRequest), typeof(CancellationToken) },
            invokeMethod.GetParameters().Select(parameter => parameter.ParameterType));
    }

    private static McpDelegatedCredentialsConfig CreateTokenExchangePolicy()
        => new()
        {
            Enabled = true,
            Mode = "token_exchange",
            Audience = "strategos",
            Scopes = ["inventory.read", "reports.read"],
            TokenEndpoint = "https://identity.example/token",
            ClientId = "openclaw-gateway",
            ClientSecretRef = "raw:client-secret-value"
        };

    private static McpCallerCredentialContext CreateCallerCredential()
        => new("caller-oidc-token", "caller-subject", DateTimeOffset.UtcNow.AddMinutes(5));

    private static McpDelegatedCredentialsConfig CreateGatewaySignedPolicy(string signingSecret)
        => new()
        {
            Enabled = true,
            Mode = "gateway_signed",
            Audience = "strategos",
            Scopes = ["inventory.read", "reports.read"],
            Issuer = "https://gateway.example",
            SigningKeyRef = $"raw:{signingSecret}",
            LifetimeSeconds = 60
        };

    private static HttpResponseMessage Json(HttpStatusCode statusCode, string body)
        => new(statusCode)
        {
            Content = new StringContent(body, Encoding.UTF8, "application/json")
        };

    private sealed class CallbackHandler(
        Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> callback) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
            => callback(request, cancellationToken);
    }

    private sealed class CapturingHttpMessageHandlerBuilderFilter : IHttpMessageHandlerBuilderFilter
    {
        public HttpMessageHandler? PrimaryHandler { get; private set; }

        public Action<HttpMessageHandlerBuilder> Configure(Action<HttpMessageHandlerBuilder> next)
            => builder =>
            {
                next(builder);
                PrimaryHandler = builder.PrimaryHandler;
            };
    }

    private sealed class CapturingLogger<T> : ILogger<T>
    {
        public List<string> Messages { get; } = [];

        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
            => Messages.Add(formatter(state, exception));
    }
}
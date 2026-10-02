using System.Collections.Concurrent;
using System.Text.Json;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using ModelContextProtocol;
using ModelContextProtocol.Protocol;
using OpenClaw.Core.Plugins;
using OpenClaw.Core.Security;
using OpenClaw.Gateway.Mcp;
using NSubstitute;
using Xunit;

namespace OpenClaw.Tests;

public sealed class McpDelegatedToolInvokerTests
{
    [Fact]
    public async Task InvokeAsync_UsesFreshDelegatedAuthorizationAndPreservesResponse()
    {
        await using var server = await StartMcpServerAsync();
        var credentialProvider = Substitute.For<IMcpDelegatedCredentialProvider>();
        var credentialNumber = 0;
        credentialProvider.GetCredentialAsync(
                Arg.Any<McpDelegatedCredentialsConfig>(),
                Arg.Any<McpCallerCredentialContext>(),
                Arg.Any<CancellationToken>())
            .Returns(_ => Task.FromResult(new McpDelegatedCredential(
                $"delegated-token-{Interlocked.Increment(ref credentialNumber)}",
                DateTimeOffset.UtcNow.AddMinutes(5))));
        var invoker = new McpDelegatedToolInvoker(credentialProvider, new McpDelegatedHttpClientFactory());
        var caller = CreateCallerCredential();

        var first = await invoker.InvokeAsync(CreateRequest(server.Endpoint, caller), TestContext.Current.CancellationToken);
        var second = await invoker.InvokeAsync(
            CreateRequest(server.Endpoint, caller, suppressStructuredContent: true),
            TestContext.Current.CancellationToken);

        Assert.Equal("remote text\n\n{\"ok\":true}", first.ResponseText);
        Assert.True(first.IsError);
        Assert.Equal("remote text", second.ResponseText);
        Assert.True(second.IsError);
        Assert.Collection(
            server.Calls,
            call =>
            {
                Assert.Equal("Bearer delegated-token-1", call.Authorization);
                Assert.Equal("static-header-value", call.StaticHeader);
                Assert.Equal("echo", call.ToolName);
                Assert.Equal("{\"value\":7}", call.ArgumentsJson);
            },
            call =>
            {
                Assert.Equal("Bearer delegated-token-2", call.Authorization);
                Assert.Equal("static-header-value", call.StaticHeader);
            });
        Assert.DoesNotContain(server.Calls, call => call.Authorization == "Bearer static-configured-token");
        await credentialProvider.Received(2).GetCredentialAsync(
            Arg.Any<McpDelegatedCredentialsConfig>(),
            caller,
            Arg.Any<CancellationToken>());
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task InvokeAsync_WhenCallerContextMissingOrExpired_SendsNoToolCall(bool expired)
    {
        await using var server = await StartMcpServerAsync();
        var credentialProvider = Substitute.For<IMcpDelegatedCredentialProvider>();
        var caller = expired
            ? new McpCallerCredentialContext("expired-caller-token", "caller-subject", DateTimeOffset.UtcNow.AddSeconds(-1))
            : null;
        var invoker = new McpDelegatedToolInvoker(credentialProvider, new McpDelegatedHttpClientFactory());

        var error = await Assert.ThrowsAsync<McpDelegatedToolInvocationException>(async () =>
            await invoker.InvokeAsync(CreateRequest(server.Endpoint, caller), TestContext.Current.CancellationToken));

        Assert.Equal(expired ? "MCP_DELEGATED_CALLER_EXPIRED" : "MCP_DELEGATED_CALLER_MISSING", error.FailureCode);
        Assert.Empty(server.Calls);
        await credentialProvider.DidNotReceiveWithAnyArgs().GetCredentialAsync(default!, default!, default);
    }

    [Fact]
    public async Task InvokeAsync_WhenCredentialProviderFails_SendsNoToolCall()
    {
        await using var server = await StartMcpServerAsync();
        var credentialProvider = Substitute.For<IMcpDelegatedCredentialProvider>();
        credentialProvider.GetCredentialAsync(
                Arg.Any<McpDelegatedCredentialsConfig>(),
                Arg.Any<McpCallerCredentialContext>(),
                Arg.Any<CancellationToken>())
            .Returns<Task<McpDelegatedCredential>>(_ => throw new InvalidOperationException(
                "provider failed caller-oidc-token delegated-secret-marker"));
        var invoker = new McpDelegatedToolInvoker(credentialProvider, new McpDelegatedHttpClientFactory());

        var error = await Assert.ThrowsAsync<McpDelegatedToolInvocationException>(async () =>
            await invoker.InvokeAsync(CreateRequest(server.Endpoint, CreateCallerCredential()), TestContext.Current.CancellationToken));

        Assert.Equal("MCP_DELEGATED_CREDENTIAL_PROVIDER_FAILED", error.Message);
        Assert.DoesNotContain("caller-oidc-token", error.ToString(), StringComparison.Ordinal);
        Assert.DoesNotContain("delegated-secret-marker", error.ToString(), StringComparison.Ordinal);
        Assert.Empty(server.Calls);
    }

    [Theory]
    [InlineData(401, "MCP_DELEGATED_UPSTREAM_UNAUTHORIZED")]
    [InlineData(403, "MCP_DELEGATED_UPSTREAM_FORBIDDEN")]
    public async Task InvokeAsync_WhenUpstreamRejectsDelegatedCredential_DoesNotRetryStaticAuthorization(
        int statusCode,
        string expectedFailureCode)
    {
        await using var server = await StartMcpServerAsync(toolCallStatusCode: statusCode);
        var credentialProvider = Substitute.For<IMcpDelegatedCredentialProvider>();
        credentialProvider.GetCredentialAsync(
                Arg.Any<McpDelegatedCredentialsConfig>(),
                Arg.Any<McpCallerCredentialContext>(),
                Arg.Any<CancellationToken>())
            .Returns(Task.FromResult(new McpDelegatedCredential(
                "delegated-secret-marker",
                DateTimeOffset.UtcNow.AddMinutes(5))));
        var invoker = new McpDelegatedToolInvoker(credentialProvider, new McpDelegatedHttpClientFactory());

        var error = await Assert.ThrowsAsync<McpDelegatedToolInvocationException>(async () =>
            await invoker.InvokeAsync(CreateRequest(server.Endpoint, CreateCallerCredential()), TestContext.Current.CancellationToken));

        Assert.Equal(expectedFailureCode, error.Message);
        Assert.DoesNotContain("delegated-secret-marker", error.ToString(), StringComparison.Ordinal);
        var call = Assert.Single(server.Calls);
        Assert.Equal("Bearer delegated-secret-marker", call.Authorization);
        Assert.Equal("{\"value\":7}", call.ArgumentsJson);
        Assert.DoesNotContain("caller-oidc-token", call.ArgumentsJson, StringComparison.Ordinal);
        Assert.DoesNotContain("delegated-secret-marker", call.ArgumentsJson, StringComparison.Ordinal);
        Assert.DoesNotContain("caller-oidc-token", call.MetaJson, StringComparison.Ordinal);
        Assert.DoesNotContain("delegated-secret-marker", call.MetaJson, StringComparison.Ordinal);
        Assert.DoesNotContain("Bearer static-configured-token", server.Calls.Select(call => call.Authorization));
    }

    [Fact]
    public async Task InvokeAsync_WhenCallerExpiresDuringCredentialAcquisition_SendsNoToolCall()
    {
        await using var server = await StartMcpServerAsync();
        var credentialProvider = Substitute.For<IMcpDelegatedCredentialProvider>();
        credentialProvider.GetCredentialAsync(
                Arg.Any<McpDelegatedCredentialsConfig>(),
                Arg.Any<McpCallerCredentialContext>(),
                Arg.Any<CancellationToken>())
            .Returns(async _ =>
            {
                await Task.Delay(TimeSpan.FromMilliseconds(80), TestContext.Current.CancellationToken);
                return new McpDelegatedCredential("fresh-delegated-token", DateTimeOffset.UtcNow.AddMinutes(5));
            });
        var invoker = new McpDelegatedToolInvoker(credentialProvider, new McpDelegatedHttpClientFactory());
        var caller = new McpCallerCredentialContext(
            "caller-oidc-token",
            "caller-subject",
            DateTimeOffset.UtcNow.AddMilliseconds(30));

        var error = await Assert.ThrowsAsync<McpDelegatedToolInvocationException>(async () =>
            await invoker.InvokeAsync(CreateRequest(server.Endpoint, caller), TestContext.Current.CancellationToken));

        Assert.Equal("MCP_DELEGATED_CALLER_EXPIRED", error.FailureCode);
        Assert.Empty(server.Calls);
    }

    [Fact]
    public async Task InvokeAsync_WhenDelegatedCredentialExpiresDuringClientSetup_SendsNoToolCall()
    {
        await using var server = await StartMcpServerAsync(
            clientSetupDelay: TimeSpan.FromMilliseconds(500));
        var credentialProvider = Substitute.For<IMcpDelegatedCredentialProvider>();
        credentialProvider.GetCredentialAsync(
                Arg.Any<McpDelegatedCredentialsConfig>(),
                Arg.Any<McpCallerCredentialContext>(),
                Arg.Any<CancellationToken>())
            .Returns(Task.FromResult(new McpDelegatedCredential(
                "short-lived-delegated-token",
                DateTimeOffset.UtcNow.AddMilliseconds(250))));
        var invoker = new McpDelegatedToolInvoker(credentialProvider, new McpDelegatedHttpClientFactory());

        var invocationError = await Record.ExceptionAsync(async () =>
            await invoker.InvokeAsync(CreateRequest(server.Endpoint, CreateCallerCredential()), TestContext.Current.CancellationToken));

        Assert.True(
            invocationError is McpDelegatedToolInvocationException { FailureCode: "MCP_DELEGATED_CREDENTIAL_EXPIRED" },
            $"Expected delegated expiry failure; observed request methods: {string.Join(", ", server.RequestMethods)}");
        Assert.Empty(server.Calls);
    }

    [Fact]
    public async Task InvokeAsync_WhenDelegatedCredentialIsExpired_SendsNoToolCall()
    {
        await using var server = await StartMcpServerAsync();
        var credentialProvider = Substitute.For<IMcpDelegatedCredentialProvider>();
        credentialProvider.GetCredentialAsync(
                Arg.Any<McpDelegatedCredentialsConfig>(),
                Arg.Any<McpCallerCredentialContext>(),
                Arg.Any<CancellationToken>())
            .Returns(Task.FromResult(new McpDelegatedCredential(
                "expired-delegated-token",
                DateTimeOffset.UtcNow.AddSeconds(-1))));
        var invoker = new McpDelegatedToolInvoker(credentialProvider, new McpDelegatedHttpClientFactory());

        var error = await Assert.ThrowsAsync<McpDelegatedToolInvocationException>(async () =>
            await invoker.InvokeAsync(CreateRequest(server.Endpoint, CreateCallerCredential()), TestContext.Current.CancellationToken));

        Assert.Equal("MCP_DELEGATED_CREDENTIAL_EXPIRED", error.FailureCode);
        Assert.Empty(server.Calls);
    }

    private static McpDelegatedToolCallRequest CreateRequest(Uri endpoint, McpCallerCredentialContext? caller, bool suppressStructuredContent = false)
        => new()
        {
            EndpointId = "inventory",
            Endpoint = endpoint,
            StaticHeaders = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
            {
                ["Authorization"] = "Bearer static-configured-token",
                ["X-Static"] = "static-header-value"
            },
            RequestTimeoutSeconds = 10,
            Policy = new McpDelegatedCredentialsConfig
            {
                Enabled = true,
                Mode = "token_exchange",
                Audience = "inventory",
                Scopes = ["inventory.read"]
            },
            RemoteToolName = "echo",
            ArgumentsJson = "{\"value\":7}",
            CallerCredentialContext = caller,
            SuppressStructuredContent = suppressStructuredContent
        };

    private static McpCallerCredentialContext CreateCallerCredential()
        => new("caller-oidc-token", "caller-subject", DateTimeOffset.UtcNow.AddMinutes(5));

    private static async Task<McpServerFixture> StartMcpServerAsync(
        int? toolCallStatusCode = null,
        TimeSpan? clientSetupDelay = null)
    {
        var calls = new ConcurrentQueue<ReceivedMcpCall>();
        var requestMethods = new ConcurrentQueue<string>();
        var builder = WebApplication.CreateSlimBuilder();
        builder.WebHost.UseUrls("http://127.0.0.1:0");
        builder.Services.AddMcpServer(options =>
            {
                options.ServerInfo = new Implementation { Name = "delegated-test", Version = "1.0.0" };
            })
            .WithHttpTransport(options => options.Stateless = true)
            .WithCallToolHandler((_, _) => ValueTask.FromResult(new CallToolResult
            {
                Content = [new TextContentBlock { Text = "remote text" }],
                StructuredContent = JsonSerializer.SerializeToElement(new { ok = true }),
                IsError = true
            }));

        var app = builder.Build();
        app.Use(async (context, next) =>
        {
            if (context.Request.Path.StartsWithSegments("/mcp", StringComparison.Ordinal)
                && HttpMethods.IsPost(context.Request.Method))
            {
                context.Request.EnableBuffering();
                using var document = await JsonDocument.ParseAsync(context.Request.Body, cancellationToken: context.RequestAborted);
                context.Request.Body.Position = 0;
                var root = document.RootElement;
                var methodName = root.TryGetProperty("method", out var method) ? method.GetString() ?? "" : "";
                requestMethods.Enqueue(methodName);
                if (methodName == "tools/call")
                {
                    var parameters = root.GetProperty("params");
                    calls.Enqueue(new ReceivedMcpCall(
                        context.Request.Headers.Authorization.ToString(),
                        context.Request.Headers["X-Static"].ToString(),
                        parameters.GetProperty("name").GetString() ?? "",
                        parameters.GetProperty("arguments").GetRawText(),
                        parameters.TryGetProperty("_meta", out var meta) ? meta.GetRawText() : ""));
                    if (toolCallStatusCode is { } statusCode)
                    {
                        context.Response.StatusCode = statusCode;
                        return;
                    }
                }
                else if (methodName == "server/discover" && clientSetupDelay is { } delay)
                {
                    await Task.Delay(delay, context.RequestAborted);
                }
            }

            await next();
        });
        app.MapMcp("/mcp");
        await app.StartAsync();
        return new McpServerFixture(app, new Uri($"{app.Urls.Single().TrimEnd('/')}/mcp"), calls, requestMethods);
    }

    private sealed record ReceivedMcpCall(
        string Authorization,
        string StaticHeader,
        string ToolName,
        string ArgumentsJson,
        string MetaJson);

    private sealed class McpServerFixture(
        WebApplication app,
        Uri endpoint,
        ConcurrentQueue<ReceivedMcpCall> calls,
        ConcurrentQueue<string> requestMethods) : IAsyncDisposable
    {
        public Uri Endpoint { get; } = endpoint;
        public IReadOnlyCollection<ReceivedMcpCall> Calls => calls.ToArray();
        public IReadOnlyCollection<string> RequestMethods => requestMethods.ToArray();

        public ValueTask DisposeAsync() => app.DisposeAsync();
    }
}
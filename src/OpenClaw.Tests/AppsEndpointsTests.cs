using System.Collections.Concurrent;
using System.Collections.Frozen;
using System.Net.Http.Json;
using System.Text.Json;
using System.Security.Claims;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using ModelContextProtocol.Protocol;
using NSubstitute;
using OpenClaw.Agent;
using OpenClaw.Channels;
using OpenClaw.Core.Abstractions;
using OpenClaw.Core.Middleware;
using OpenClaw.Core.Models;
using OpenClaw.Core.Observability;
using OpenClaw.Core.Pipeline;
using OpenClaw.Core.Plugins;
using OpenClaw.Core.Security;
using OpenClaw.Gateway;
using OpenClaw.Core.Sessions;
using OpenClaw.Gateway.Bootstrap;
using OpenClaw.Gateway.Composition;
using OpenClaw.Gateway.Endpoints;
using OpenClaw.Gateway.Pipeline;
using OpenClaw.McpApp;
using OpenClaw.McpApp.Models;
using Xunit;

namespace OpenClaw.Tests;

public sealed class AppsEndpointsTests : IAsyncDisposable
{
    private readonly List<WebApplication> _apps = [];
    private readonly List<string> _tempDirs = [];

    [Fact]
    public async Task Health_ReturnsGatewayProxyUrlForLoadedApp()
    {
        var upstreamUrl = await StartFakeUpstreamAsync();
        await using var harness = await StartGatewayAsync(upstreamUrl, Substitute.For<IAgentRuntime>());

        var response = await harness.Client.GetAsync("/apps/health");
        response.EnsureSuccessStatusCode();

        using var json = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        Assert.True(json.RootElement.GetProperty("ok").GetBoolean());
        Assert.Equal("inventory-app", json.RootElement.GetProperty("name").GetString());
        Assert.EndsWith("/apps/mcp/inventory-app", json.RootElement.GetProperty("mcp").GetString(), StringComparison.Ordinal);
        Assert.Equal("http://localhost:3101/sandbox.html", json.RootElement.GetProperty("sandbox").GetString());
    }

    [Fact]
    public async Task Chat_StreamsEvents_AndPassesUiEventsIntoPrompt()
    {
        string? capturedPrompt = null;
        var agentRuntime = Substitute.For<IAgentRuntime>();
        agentRuntime.RunStreamingAsync(
                Arg.Any<Session>(),
                Arg.Do<string>(prompt => capturedPrompt = prompt),
                Arg.Any<CancellationToken>(),
                Arg.Any<ToolApprovalCallback?>(),
                Arg.Any<string?>())
            .Returns(StreamEvents());

        await using var harness = await StartGatewayAsync(null, agentRuntime);

        var payload = new
        {
            message = "继续处理库存差异",
            sessionId = "apps-session-1",
            appEvents = new[]
            {
                new
                {
                    tool = "inventory.sync",
                    args = new { sku = "ABC-001" },
                    resultText = "库存已同步到 42"
                }
            }
        };

        using var content = JsonContent.Create(payload);
        var response = await harness.Client.PostAsync("/apps/chat", content);
        response.EnsureSuccessStatusCode();

        Assert.Equal("text/event-stream", response.Content.Headers.ContentType?.MediaType);
        var body = await response.Content.ReadAsStringAsync();
        var events = ParseSseEvents(body);
        Assert.Contains(events, evt => evt.GetProperty("type").GetString() == "session"
            && evt.GetProperty("sessionId").GetString() == "apps-session-1");
        Assert.Contains(events, evt => evt.GetProperty("type").GetString() == "text"
            && evt.GetProperty("text").GetString() == "已收到");
        Assert.Contains(events, evt => evt.GetProperty("type").GetString() == "tool"
            && evt.GetProperty("name").GetString() == "inventory.lookup"
            && evt.GetProperty("input").GetProperty("sku").GetString() == "ABC-001");
        Assert.Contains(events, evt => evt.GetProperty("type").GetString() == "result");
        Assert.Contains(events, evt => evt.GetProperty("type").GetString() == "done");

        Assert.NotNull(capturedPrompt);
        Assert.Contains("[UI event]", capturedPrompt, StringComparison.Ordinal);
        Assert.Contains("inventory.sync", capturedPrompt, StringComparison.Ordinal);
        Assert.Contains("库存已同步到 42", capturedPrompt, StringComparison.Ordinal);
        Assert.Contains("继续处理库存差异", capturedPrompt, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Chat_ValidatedOidcBearer_PassesCallerContextToRuntime()
    {
        const string accessToken = "apps-oidc-access-token-marker";
        const string subject = "apps-oidc-subject";
        var expiresAt = DateTimeOffset.UtcNow.AddMinutes(5);
        var principal = new ClaimsPrincipal(new ClaimsIdentity(
        [
            new Claim("sub", subject),
            new Claim("exp", expiresAt.ToUnixTimeSeconds().ToString(System.Globalization.CultureInfo.InvariantCulture)),
            new Claim(ClaimTypes.Role, OperatorRoleNames.Operator)
        ],
        authenticationType: "oidc"));
        McpCallerCredentialContext? callerContext = null;
        var agentRuntime = Substitute.For<IAgentRuntime>();
        agentRuntime.RunStreamingAsync(
                Arg.Any<Session>(),
                Arg.Any<string>(),
                Arg.Any<CancellationToken>(),
                Arg.Any<ToolApprovalCallback?>(),
                Arg.Any<string?>(),
                Arg.Do<McpCallerCredentialContext?>(context => callerContext = context))
            .Returns(_ => StreamEvents());

        await using var harness = await StartGatewayAsync(
            null,
            agentRuntime,
            authenticatedPrincipal: principal,
            oidcAuthority: "https://issuer.example");
        using var request = new HttpRequestMessage(HttpMethod.Post, "/apps/chat")
        {
            Content = JsonContent.Create(new { message = "hello", sessionId = "apps-oidc" })
        };
        request.Headers.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", accessToken);

        var response = await harness.Client.SendAsync(request);
        response.EnsureSuccessStatusCode();
        await response.Content.ReadAsStringAsync();

        Assert.NotNull(callerContext);
        Assert.Equal(accessToken, callerContext.OidcAccessToken);
        Assert.Equal(subject, callerContext.Subject);
        Assert.Equal(expiresAt.ToUnixTimeSeconds(), callerContext.ExpiresAtUtc.ToUnixTimeSeconds());
    }

    [Fact]
    public async Task Chat_ConcurrentTurnsOnSameSession_RunOneAtATime()
    {
        var firstTurnStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var secondTurnStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var releaseFirstTurn = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var turns = 0;
        var agentRuntime = Substitute.For<IAgentRuntime>();
        agentRuntime.RunStreamingAsync(
                Arg.Any<Session>(),
                Arg.Any<string>(),
                Arg.Any<CancellationToken>(),
                Arg.Any<ToolApprovalCallback?>(),
                Arg.Any<string?>())
            .Returns(_ => HeldTurn());

        async IAsyncEnumerable<AgentStreamEvent> HeldTurn()
        {
            if (Interlocked.Increment(ref turns) == 1)
            {
                firstTurnStarted.SetResult();
                await releaseFirstTurn.Task;
            }
            else
            {
                secondTurnStarted.SetResult();
            }

            yield return AgentStreamEvent.Complete();
        }

        await using var harness = await StartGatewayAsync(null, agentRuntime);
        var ct = TestContext.Current.CancellationToken;

        var first = harness.Client.PostAsync("/apps/chat", JsonContent.Create(new { message = "one", sessionId = "apps-serial" }), ct);
        await firstTurnStarted.Task.WaitAsync(TimeSpan.FromSeconds(10), ct);
        var second = harness.Client.PostAsync("/apps/chat", JsonContent.Create(new { message = "two", sessionId = "apps-serial" }), ct);

        // While the first turn holds the session, the second must wait rather than run alongside it.
        var overlapped = await Task.WhenAny(secondTurnStarted.Task, Task.Delay(TimeSpan.FromMilliseconds(500), ct)) == secondTurnStarted.Task;
        releaseFirstTurn.SetResult();
        (await first).EnsureSuccessStatusCode();
        (await second).EnsureSuccessStatusCode();

        Assert.False(overlapped);
        Assert.True(secondTurnStarted.Task.IsCompletedSuccessfully);
    }

    [Fact]
    public async Task Chat_PersistsSessionAfterTurn()
    {
        var store = new TestMemoryStore();
        var agentRuntime = Substitute.For<IAgentRuntime>();
        agentRuntime.RunStreamingAsync(
                Arg.Any<Session>(),
                Arg.Any<string>(),
                Arg.Any<CancellationToken>(),
                Arg.Any<ToolApprovalCallback?>(),
                Arg.Any<string?>())
            .Returns(_ => StreamEvents());

        await using var harness = await StartGatewayAsync(null, agentRuntime, store);
        var ct = TestContext.Current.CancellationToken;

        var response = await harness.Client.PostAsync("/apps/chat", JsonContent.Create(new { message = "hello", sessionId = "apps-persisted" }), ct);
        response.EnsureSuccessStatusCode();

        Assert.NotNull(await store.GetSessionAsync("apps-persisted", ct));
    }

    [Fact]
    public void CorsAllowHeaders_IncludeMcpSessionHeaders()
    {
        Assert.Contains("mcp-protocol-version", PipelineExtensions.CorsAllowHeaders, StringComparison.Ordinal);
        Assert.Contains("Mcp-Session-Id", PipelineExtensions.CorsAllowHeaders, StringComparison.Ordinal);
    }

    private async Task<string> StartFakeUpstreamAsync()
    {
        var builder = WebApplication.CreateSlimBuilder();
        builder.WebHost.UseUrls("http://127.0.0.1:0");
        builder.Services.AddMcpServer(options =>
            {
                options.ServerInfo = new Implementation { Name = "fake-upstream", Version = "1.0.0" };
            })
            .WithHttpTransport(options => { options.Stateless = true; })
            .WithListToolsHandler((_, _) => ValueTask.FromResult(new ListToolsResult
            {
                Tools = [new Tool { Name = "echo_session", Description = "echo" }],
            }));

        var app = builder.Build();
        app.MapMcp("/mcp");
        await app.StartAsync();
        _apps.Add(app);
        return $"{app.Urls.Single().TrimEnd('/')}/mcp";
    }

    private async Task<AppsGatewayTestHarness> StartGatewayAsync(
        string? upstreamUrl,
        IAgentRuntime agentRuntime,
        IMemoryStore? store = null,
        ClaimsPrincipal? authenticatedPrincipal = null,
        string? oidcAuthority = null)
    {
        var config = new GatewayConfig
        {
            BindAddress = "127.0.0.1",
            AuthToken = "test-token",
            McpApps = new McpAppsConfig { Enabled = false }
        };
        if (oidcAuthority is not null)
        {
            config.Security.AuthMode = SecurityAuthModeNames.Oidc;
            config.Security.Oidc.Authority = oidcAuthority;
        }

        if (!string.IsNullOrWhiteSpace(upstreamUrl))
        {
            var root = Path.Combine(Path.GetTempPath(), "openclaw-apps-endpoints-tests", Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(root);
            _tempDirs.Add(root);

            var manifest = new McpAppManifest
            {
                Id = "inventory-app",
                Name = "Inventory App",
                Version = "1.0.0",
                Transport = "http",
                Url = upstreamUrl,
                HasUi = true,
                UiResourceUri = "ui://inventory/app.html",
            };
            await File.WriteAllTextAsync(
                Path.Combine(root, "openclaw.mcpapp.json"),
                JsonSerializer.Serialize(manifest, McpAppManifestJsonContext.Default.McpAppManifest));

            config.McpApps = new McpAppsConfig
            {
                Enabled = true,
                DiscoveryPaths = [root],
            };
        }

        var startup = new GatewayStartupContext
        {
            Config = config,
            RuntimeState = RuntimeModeResolver.Resolve(config.Runtime),
            IsNonLoopbackBind = false,
            WorkspacePath = null,
        };

        var builder = WebApplication.CreateSlimBuilder();
        builder.WebHost.UseTestServer();
        builder.Services.AddOpenClawMcpAppServices(config.McpApps);
        builder.Services.AddSingleton(new BrowserSessionAuthService(config));

        var runtime = CreateRuntime(config, agentRuntime, store ?? new TestMemoryStore());
        var app = builder.Build();
        if (authenticatedPrincipal is not null)
        {
            app.Use(async (ctx, next) =>
            {
                ctx.User = authenticatedPrincipal;
                await next();
            });
        }
        app.MapOpenClawAppsEndpoints(startup, runtime);
        await app.StartAsync();
        _apps.Add(app);

        var registry = app.Services.GetRequiredService<McpAppRegistry>();
        await registry.LoadAllAsync(CancellationToken.None);

        var client = app.GetTestClient();
        client.BaseAddress = new Uri("http://localhost/");
        return new AppsGatewayTestHarness(app, client, registry);
    }

    private static GatewayAppRuntime CreateRuntime(GatewayConfig config, IAgentRuntime agentRuntime, IMemoryStore store)
    {
        var sessionManager = new SessionManager(store, config, NullLogger.Instance);
        return new GatewayAppRuntime
        {
            AgentRuntime = agentRuntime,
            OrchestratorId = "test",
            Pipeline = null!,
            MiddlewarePipeline = null!,
            WebSocketChannel = null!,
            ChannelAdapters = new Dictionary<string, IChannelAdapter>(),
            SessionManager = sessionManager,
            RetentionCoordinator = null!,
            PairingManager = null!,
            Allowlists = null!,
            AllowlistSemantics = AllowlistSemantics.Legacy,
            RecentSenders = null!,
            CommandProcessor = new ChatCommandProcessor(sessionManager),
            ToolApprovalService = new ToolApprovalService(),
            ApprovalAuditStore = null!,
            RuntimeMetrics = new RuntimeMetrics(),
            ProviderUsage = new ProviderUsageTracker(),
            PaymentRuntime = null!,
            Heartbeat = null!,
            LoadedSkills = [],
            SkillWatcher = null!,
            PluginReports = [],
            Operations = null!,
            EffectiveRequireToolApproval = false,
            EffectiveApprovalRequiredTools = [],
            NativeRegistry = null!,
            SessionLocks = sessionManager.SessionLocks,
            LockLastUsed = sessionManager.LockLastUsed,
            AllowedOriginsSet = FrozenSet.ToFrozenSet<string>([]),
            DynamicProviderOwners = [],
            EstimatedSkillPromptChars = 0,
            CronTask = null,
            ChannelAuthEvents = null!,
            ArtifactRuntime = null!,
            RegisteredToolNames = FrozenSet.ToFrozenSet<string>([]),
            AbortRegistry = null!,
        };
    }

    private static async IAsyncEnumerable<AgentStreamEvent> StreamEvents()
    {
        yield return AgentStreamEvent.TextDelta("已收到");
        yield return AgentStreamEvent.ToolStarted("inventory.lookup", "{\"sku\":\"ABC-001\"}");
        yield return AgentStreamEvent.Complete();
        await Task.CompletedTask;
    }

    private static List<JsonElement> ParseSseEvents(string body)
    {
        var events = new List<JsonElement>();
        foreach (var line in body.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                     .Where(static line => line.StartsWith("data: ", StringComparison.Ordinal)))
        {
            using var document = JsonDocument.Parse(line[6..]);
            events.Add(document.RootElement.Clone());
        }

        return events;
    }

    public async ValueTask DisposeAsync()
    {
        foreach (var app in _apps)
            await app.DisposeAsync();

        foreach (var dir in _tempDirs)
        {
            try
            {
                if (Directory.Exists(dir))
                    Directory.Delete(dir, recursive: true);
            }
            catch
            {
            }
        }
    }

    private sealed record AppsGatewayTestHarness(WebApplication App, HttpClient Client, McpAppRegistry Registry) : IAsyncDisposable
    {
        public async ValueTask DisposeAsync()
        {
            await Registry.DisposeAsync();
            Client.Dispose();
            await App.DisposeAsync();
        }
    }

    private sealed class TestMemoryStore : IMemoryStore
    {
        private readonly ConcurrentDictionary<string, Session> _sessions = new(StringComparer.Ordinal);

        public ValueTask<Session?> GetSessionAsync(string sessionId, CancellationToken ct)
            => ValueTask.FromResult(_sessions.TryGetValue(sessionId, out var session) ? session : null);

        public ValueTask SaveSessionAsync(Session session, CancellationToken ct)
        {
            _sessions[session.Id] = session;
            return ValueTask.CompletedTask;
        }

        public ValueTask DeleteSessionAsync(string sessionId, CancellationToken ct)
        {
            _sessions.TryRemove(sessionId, out _);
            return ValueTask.CompletedTask;
        }

        public ValueTask<string?> LoadNoteAsync(string key, CancellationToken ct) => ValueTask.FromResult<string?>(null);
        public ValueTask SaveNoteAsync(string key, string content, CancellationToken ct) => ValueTask.CompletedTask;
        public ValueTask DeleteNoteAsync(string key, CancellationToken ct) => ValueTask.CompletedTask;
        public ValueTask<IReadOnlyList<string>> ListNotesWithPrefixAsync(string prefix, CancellationToken ct) => ValueTask.FromResult<IReadOnlyList<string>>([]);
        public ValueTask SaveBranchAsync(SessionBranch branch, CancellationToken ct) => ValueTask.CompletedTask;
        public ValueTask<SessionBranch?> LoadBranchAsync(string branchId, CancellationToken ct) => ValueTask.FromResult<SessionBranch?>(null);
        public ValueTask<IReadOnlyList<SessionBranch>> ListBranchesAsync(string sessionId, CancellationToken ct) => ValueTask.FromResult<IReadOnlyList<SessionBranch>>([]);
        public ValueTask DeleteBranchAsync(string branchId, CancellationToken ct) => ValueTask.CompletedTask;
    }
}

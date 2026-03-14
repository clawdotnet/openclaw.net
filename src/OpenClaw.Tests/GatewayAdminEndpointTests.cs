using System.Collections.Concurrent;
using System.Net;
using System.Net.Http.Headers;
using System.Text.RegularExpressions;
using System.Text;
using System.Text.Json;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Routing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;
using OpenClaw.Agent;
using OpenClaw.Agent.Plugins;
using OpenClaw.Channels;
using OpenClaw.Core.Abstractions;
using OpenClaw.Core.Memory;
using OpenClaw.Core.Middleware;
using OpenClaw.Core.Models;
using OpenClaw.Core.Observability;
using OpenClaw.Core.Pipeline;
using OpenClaw.Core.Plugins;
using OpenClaw.Core.Security;
using OpenClaw.Core.Sessions;
using OpenClaw.Gateway;
using OpenClaw.Gateway.Bootstrap;
using OpenClaw.Gateway.Composition;
using OpenClaw.Gateway.Endpoints;
using OpenClaw.Gateway.Extensions;
using Xunit;

namespace OpenClaw.Tests;

public sealed class GatewayAdminEndpointTests
{
    [Fact]
    public async Task AuthSession_BearerAndBrowserSessionFlow_Works()
    {
        await using var harness = await CreateHarnessAsync(nonLoopbackBind: true);

        var anonymousResponse = await harness.Client.GetAsync("/auth/session");
        Assert.Equal(HttpStatusCode.Unauthorized, anonymousResponse.StatusCode);

        using var bearerRequest = new HttpRequestMessage(HttpMethod.Get, "/auth/session");
        bearerRequest.Headers.Authorization = new AuthenticationHeaderValue("Bearer", harness.AuthToken);
        var bearerResponse = await harness.Client.SendAsync(bearerRequest);
        Assert.Equal(HttpStatusCode.OK, bearerResponse.StatusCode);
        var bearerPayload = await ReadJsonAsync(bearerResponse);
        Assert.Equal("bearer", bearerPayload.RootElement.GetProperty("authMode").GetString());

        using var loginRequest = new HttpRequestMessage(HttpMethod.Post, "/auth/session")
        {
            Content = JsonContent("""{"remember":true}""")
        };
        loginRequest.Headers.Authorization = new AuthenticationHeaderValue("Bearer", harness.AuthToken);
        var loginResponse = await harness.Client.SendAsync(loginRequest);
        Assert.Equal(HttpStatusCode.OK, loginResponse.StatusCode);
        var loginPayload = await ReadJsonAsync(loginResponse);
        Assert.Equal("browser-session", loginPayload.RootElement.GetProperty("authMode").GetString());
        var csrfToken = loginPayload.RootElement.GetProperty("csrfToken").GetString();
        Assert.False(string.IsNullOrWhiteSpace(csrfToken));
        var cookie = Assert.Single(loginResponse.Headers.GetValues("Set-Cookie"));

        using var sessionRequest = new HttpRequestMessage(HttpMethod.Get, "/auth/session");
        sessionRequest.Headers.Add("Cookie", cookie);
        var sessionResponse = await harness.Client.SendAsync(sessionRequest);
        Assert.Equal(HttpStatusCode.OK, sessionResponse.StatusCode);
        var sessionPayload = await ReadJsonAsync(sessionResponse);
        Assert.Equal("browser-session", sessionPayload.RootElement.GetProperty("authMode").GetString());

        using var deleteRequest = new HttpRequestMessage(HttpMethod.Delete, "/auth/session");
        deleteRequest.Headers.Add("Cookie", cookie);
        deleteRequest.Headers.Add(BrowserSessionAuthService.CsrfHeaderName, csrfToken);
        var deleteResponse = await harness.Client.SendAsync(deleteRequest);
        Assert.Equal(HttpStatusCode.OK, deleteResponse.StatusCode);
    }

    [Fact]
    public async Task AdminSettings_BrowserSessionMutation_RequiresCsrf()
    {
        await using var harness = await CreateHarnessAsync(nonLoopbackBind: true);
        var (cookie, csrfToken) = await LoginAsync(harness.Client, harness.AuthToken);

        using var currentSettingsRequest = new HttpRequestMessage(HttpMethod.Get, "/admin/settings");
        currentSettingsRequest.Headers.Add("Cookie", cookie);
        var currentSettingsResponse = await harness.Client.SendAsync(currentSettingsRequest);
        currentSettingsResponse.EnsureSuccessStatusCode();
        using var currentSettings = await ReadJsonAsync(currentSettingsResponse);
        var settingsPayload = currentSettings.RootElement.GetProperty("settings").Clone();
        var settingsDict = JsonSerializer.Deserialize<Dictionary<string, JsonElement>>(settingsPayload.GetRawText(), CoreJsonContext.Default.BridgeDictionaryStringJsonElement)!;
        settingsDict["usageFooter"] = JsonSerializer.SerializeToElement("tokens");

        using var forbiddenRequest = new HttpRequestMessage(HttpMethod.Post, "/admin/settings")
        {
            Content = JsonContent(JsonSerializer.Serialize(settingsDict, CoreJsonContext.Default.BridgeDictionaryStringJsonElement))
        };
        forbiddenRequest.Headers.Add("Cookie", cookie);
        var forbiddenResponse = await harness.Client.SendAsync(forbiddenRequest);
        Assert.Equal(HttpStatusCode.Unauthorized, forbiddenResponse.StatusCode);

        using var allowedRequest = new HttpRequestMessage(HttpMethod.Post, "/admin/settings")
        {
            Content = JsonContent(JsonSerializer.Serialize(settingsDict, CoreJsonContext.Default.BridgeDictionaryStringJsonElement))
        };
        allowedRequest.Headers.Add("Cookie", cookie);
        allowedRequest.Headers.Add(BrowserSessionAuthService.CsrfHeaderName, csrfToken);
        var allowedResponse = await harness.Client.SendAsync(allowedRequest);
        Assert.Equal(HttpStatusCode.OK, allowedResponse.StatusCode);
        var payload = await ReadJsonAsync(allowedResponse);
        Assert.Equal("tokens", payload.RootElement.GetProperty("settings").GetProperty("usageFooter").GetString());
    }

    [Fact]
    public async Task ToolsApprovals_AndHistory_AreServed()
    {
        await using var harness = await CreateHarnessAsync(nonLoopbackBind: true);
        var approval = harness.Runtime.ToolApprovalService.Create("sess1", "telegram", "sender1", "shell", """{"cmd":"ls"}""", TimeSpan.FromMinutes(5));
        harness.Runtime.ApprovalAuditStore.RecordCreated(approval);

        using var approvalsRequest = new HttpRequestMessage(HttpMethod.Get, "/tools/approvals");
        approvalsRequest.Headers.Authorization = new AuthenticationHeaderValue("Bearer", harness.AuthToken);
        var approvalsResponse = await harness.Client.SendAsync(approvalsRequest);
        Assert.Equal(HttpStatusCode.OK, approvalsResponse.StatusCode);
        var approvalsPayload = await ReadJsonAsync(approvalsResponse);
        Assert.Equal(1, approvalsPayload.RootElement.GetProperty("items").GetArrayLength());

        using var historyRequest = new HttpRequestMessage(HttpMethod.Get, "/tools/approvals/history?limit=10");
        historyRequest.Headers.Authorization = new AuthenticationHeaderValue("Bearer", harness.AuthToken);
        var historyResponse = await harness.Client.SendAsync(historyRequest);
        Assert.Equal(HttpStatusCode.OK, historyResponse.StatusCode);
        var historyPayload = await ReadJsonAsync(historyResponse);
        Assert.Equal(1, historyPayload.RootElement.GetProperty("items").GetArrayLength());
        Assert.Equal("created", historyPayload.RootElement.GetProperty("items")[0].GetProperty("eventType").GetString());
    }

    [Fact]
    public async Task ProviderPolicies_Audit_AndRateLimits_AreServed()
    {
        await using var harness = await CreateHarnessAsync(nonLoopbackBind: true);

        using var createPolicy = new HttpRequestMessage(HttpMethod.Post, "/admin/providers/policies")
        {
            Content = JsonContent("""
                {
                  "id": "pp_test",
                  "priority": 10,
                  "providerId": "openai",
                  "modelId": "gpt-4o-mini",
                  "enabled": true,
                  "fallbackModels": ["gpt-4o"]
                }
                """)
        };
        createPolicy.Headers.Authorization = new AuthenticationHeaderValue("Bearer", harness.AuthToken);
        var createPolicyResponse = await harness.Client.SendAsync(createPolicy);
        Assert.Equal(HttpStatusCode.OK, createPolicyResponse.StatusCode);

        using var listPolicies = new HttpRequestMessage(HttpMethod.Get, "/admin/providers/policies");
        listPolicies.Headers.Authorization = new AuthenticationHeaderValue("Bearer", harness.AuthToken);
        var listPoliciesResponse = await harness.Client.SendAsync(listPolicies);
        Assert.Equal(HttpStatusCode.OK, listPoliciesResponse.StatusCode);
        using var policiesPayload = await ReadJsonAsync(listPoliciesResponse);
        Assert.Equal(1, policiesPayload.RootElement.GetProperty("items").GetArrayLength());

        using var resetCircuit = new HttpRequestMessage(HttpMethod.Post, "/admin/providers/openai/circuit/reset");
        resetCircuit.Headers.Authorization = new AuthenticationHeaderValue("Bearer", harness.AuthToken);
        var resetCircuitResponse = await harness.Client.SendAsync(resetCircuit);
        Assert.Equal(HttpStatusCode.OK, resetCircuitResponse.StatusCode);

        using var createRateLimit = new HttpRequestMessage(HttpMethod.Post, "/admin/rate-limits")
        {
            Content = JsonContent("""
                {
                  "id": "rl_test",
                  "actorType": "ip",
                  "endpointScope": "openai_http",
                  "burstLimit": 5,
                  "burstWindowSeconds": 60,
                  "sustainedLimit": 10,
                  "sustainedWindowSeconds": 300,
                  "enabled": true
                }
                """)
        };
        createRateLimit.Headers.Authorization = new AuthenticationHeaderValue("Bearer", harness.AuthToken);
        var createRateLimitResponse = await harness.Client.SendAsync(createRateLimit);
        Assert.Equal(HttpStatusCode.OK, createRateLimitResponse.StatusCode);

        using var auditRequest = new HttpRequestMessage(HttpMethod.Get, "/admin/audit?limit=10");
        auditRequest.Headers.Authorization = new AuthenticationHeaderValue("Bearer", harness.AuthToken);
        var auditResponse = await harness.Client.SendAsync(auditRequest);
        Assert.Equal(HttpStatusCode.OK, auditResponse.StatusCode);
        using var auditPayload = await ReadJsonAsync(auditResponse);
        var actions = auditPayload.RootElement.GetProperty("items").EnumerateArray()
            .Select(item => item.GetProperty("actionType").GetString())
            .ToArray();
        Assert.Contains("provider_policy_upsert", actions);
        Assert.Contains("provider_circuit_reset", actions);
        Assert.Contains("rate_limit_policy_upsert", actions);
    }

    [Fact]
    public async Task PluginState_ApprovalPolicies_AndTimeline_AreServed()
    {
        await using var harness = await CreateHarnessAsync(nonLoopbackBind: true);

        using var disablePlugin = new HttpRequestMessage(HttpMethod.Post, "/admin/plugins/test-plugin/disable")
        {
            Content = JsonContent("""{"reason":"maintenance"}""")
        };
        disablePlugin.Headers.Authorization = new AuthenticationHeaderValue("Bearer", harness.AuthToken);
        var disableResponse = await harness.Client.SendAsync(disablePlugin);
        Assert.Equal(HttpStatusCode.OK, disableResponse.StatusCode);

        using var pluginRequest = new HttpRequestMessage(HttpMethod.Get, "/admin/plugins/test-plugin");
        pluginRequest.Headers.Authorization = new AuthenticationHeaderValue("Bearer", harness.AuthToken);
        var pluginResponse = await harness.Client.SendAsync(pluginRequest);
        Assert.Equal(HttpStatusCode.OK, pluginResponse.StatusCode);
        using var pluginPayload = await ReadJsonAsync(pluginResponse);
        Assert.True(pluginPayload.RootElement.GetProperty("disabled").GetBoolean());

        using var createGrant = new HttpRequestMessage(HttpMethod.Post, "/tools/approval-policies")
        {
            Content = JsonContent("""
                {
                  "id": "grant_test",
                  "scope": "sender_tool_window",
                  "channelId": "telegram",
                  "senderId": "user1",
                  "toolName": "shell",
                  "grantedBy": "tester",
                  "grantSource": "test",
                  "remainingUses": 1
                }
                """)
        };
        createGrant.Headers.Authorization = new AuthenticationHeaderValue("Bearer", harness.AuthToken);
        var createGrantResponse = await harness.Client.SendAsync(createGrant);
        Assert.Equal(HttpStatusCode.OK, createGrantResponse.StatusCode);

        using var listGrantRequest = new HttpRequestMessage(HttpMethod.Get, "/tools/approval-policies");
        listGrantRequest.Headers.Authorization = new AuthenticationHeaderValue("Bearer", harness.AuthToken);
        var listGrantResponse = await harness.Client.SendAsync(listGrantRequest);
        Assert.Equal(HttpStatusCode.OK, listGrantResponse.StatusCode);
        using var grantPayload = await ReadJsonAsync(listGrantResponse);
        Assert.Equal(1, grantPayload.RootElement.GetProperty("items").GetArrayLength());

        var session = await harness.Runtime.SessionManager.GetOrCreateByIdAsync("sess-timeline", "telegram", "user1", CancellationToken.None);
        session.History.Add(new ChatTurn { Role = "user", Content = "hello" });
        await harness.Runtime.SessionManager.PersistAsync(session, CancellationToken.None);
        harness.Runtime.Operations.RuntimeEvents.Append(new RuntimeEventEntry
        {
            Id = "evt_timeline",
            SessionId = session.Id,
            ChannelId = session.ChannelId,
            SenderId = session.SenderId,
            Component = "test",
            Action = "seeded",
            Severity = "info",
            Summary = "seeded"
        });

        using var timelineRequest = new HttpRequestMessage(HttpMethod.Get, $"/admin/sessions/{Uri.EscapeDataString(session.Id)}/timeline");
        timelineRequest.Headers.Authorization = new AuthenticationHeaderValue("Bearer", harness.AuthToken);
        var timelineResponse = await harness.Client.SendAsync(timelineRequest);
        Assert.Equal(HttpStatusCode.OK, timelineResponse.StatusCode);
        using var timelinePayload = await ReadJsonAsync(timelineResponse);
        Assert.Equal(session.Id, timelinePayload.RootElement.GetProperty("sessionId").GetString());
        Assert.Equal(1, timelinePayload.RootElement.GetProperty("events").GetArrayLength());
    }

    [Fact]
    public async Task AdminSummary_IncludesRuntimeOrchestrator()
    {
        await using var harness = await CreateHarnessAsync(nonLoopbackBind: false);

        using var request = new HttpRequestMessage(HttpMethod.Get, "/admin/summary");
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", harness.AuthToken);
        var response = await harness.Client.SendAsync(request);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        using var payload = await ReadJsonAsync(response);
        Assert.Equal(
            OpenClaw.Core.Models.RuntimeOrchestrator.Native,
            payload.RootElement.GetProperty("runtime").GetProperty("orchestrator").GetString());
    }

    [Fact]
    public async Task AdminUiContract_ReferencedRoutes_AreMapped()
    {
        await using var harness = await CreateHarnessAsync(nonLoopbackBind: false);
        var dataSource = harness.App.Services.GetRequiredService<EndpointDataSource>();
        var routePatterns = dataSource.Endpoints
            .OfType<RouteEndpoint>()
            .Select(static endpoint => endpoint.RoutePattern.RawText)
            .Where(static pattern => !string.IsNullOrWhiteSpace(pattern))
            .ToHashSet(StringComparer.Ordinal);

        var expectedRoutes = new[]
        {
            "/auth/session",
            "/admin",
            "/admin/summary",
            "/admin/providers",
            "/admin/providers/policies",
            "/admin/providers/{providerId}/circuit/reset",
            "/admin/events",
            "/admin/sessions",
            "/admin/sessions/{id}",
            "/admin/sessions/{id}/branches",
            "/admin/sessions/{id}/timeline",
            "/admin/sessions/{id}/diff",
            "/admin/sessions/{id}/metadata",
            "/admin/sessions/export",
            "/admin/sessions/{id}/export",
            "/admin/branches/{id}/restore",
            "/admin/plugins",
            "/admin/plugins/{id}",
            "/admin/plugins/{id}/disable",
            "/admin/plugins/{id}/enable",
            "/admin/plugins/{id}/quarantine",
            "/admin/plugins/{id}/clear-quarantine",
            "/admin/audit",
            "/admin/webhooks/dead-letter",
            "/admin/webhooks/dead-letter/{id}/replay",
            "/admin/webhooks/dead-letter/{id}/discard",
            "/admin/rate-limits",
            "/admin/rate-limits/{id}",
            "/admin/settings",
            "/tools/approvals",
            "/tools/approvals/history",
            "/tools/approval-policies",
            "/tools/approval-policies/{id}",
            "/pairing/list",
            "/allowlists/{channelId}",
            "/allowlists/{channelId}/add_latest",
            "/allowlists/{channelId}/tighten",
            "/memory/retention/status",
            "/memory/retention/sweep",
            "/doctor/text"
        };

        foreach (var route in expectedRoutes)
            Assert.Contains(route, routePatterns);
    }

    [Fact]
    public async Task AdminUi_StaticApiTargets_MapToKnownRoutes()
    {
        await using var harness = await CreateHarnessAsync(nonLoopbackBind: false);
        var dataSource = harness.App.Services.GetRequiredService<EndpointDataSource>();
        var routePatterns = dataSource.Endpoints
            .OfType<RouteEndpoint>()
            .Select(static endpoint => endpoint.RoutePattern.RawText)
            .Where(static pattern => !string.IsNullOrWhiteSpace(pattern))
            .ToHashSet(StringComparer.Ordinal);

        var adminHtmlPath = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "../../../../../src/OpenClaw.Gateway/wwwroot/admin.html"));
        var html = await File.ReadAllTextAsync(adminHtmlPath);
        var matches = Regex.Matches(html, @"(?:api|mutate)\('(?<route>/[^']+)'");
        var staticRoutes = matches
            .Select(match => match.Groups["route"].Value.Split('?', 2)[0])
            .Where(static route => !route.Contains('{', StringComparison.Ordinal))
            .Distinct(StringComparer.Ordinal)
            .ToArray();

        foreach (var route in staticRoutes)
            Assert.Contains(route, routePatterns);
    }

    private static async Task<(string Cookie, string CsrfToken)> LoginAsync(HttpClient client, string authToken)
    {
        using var loginRequest = new HttpRequestMessage(HttpMethod.Post, "/auth/session")
        {
            Content = JsonContent("""{"remember":false}""")
        };
        loginRequest.Headers.Authorization = new AuthenticationHeaderValue("Bearer", authToken);
        var response = await client.SendAsync(loginRequest);
        response.EnsureSuccessStatusCode();
        var payload = await ReadJsonAsync(response);
        return (
            Assert.Single(response.Headers.GetValues("Set-Cookie")),
            payload.RootElement.GetProperty("csrfToken").GetString()!);
    }

    private static StringContent JsonContent(string json)
        => new(json, Encoding.UTF8, "application/json");

    private static async Task<JsonDocument> ReadJsonAsync(HttpResponseMessage response)
    {
        var payload = await response.Content.ReadAsStringAsync();
        return JsonDocument.Parse(payload);
    }

    private static async Task<GatewayTestHarness> CreateHarnessAsync(bool nonLoopbackBind)
    {
        var storagePath = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "openclaw-admin-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(storagePath);

        var config = new GatewayConfig
        {
            BindAddress = nonLoopbackBind ? "0.0.0.0" : "127.0.0.1",
            AuthToken = "test-admin-token",
            Memory = new MemoryConfig
            {
                StoragePath = storagePath
            },
            Llm = new LlmProviderConfig
            {
                Provider = "openai",
                ApiKey = "test-key",
                Model = "gpt-4o",
                RetryCount = 0,
                TimeoutSeconds = 0
            },
            Tooling = new ToolingConfig
            {
                EnableBrowserTool = false,
                AllowBrowserEvaluate = false
            },
            Plugins = new OpenClaw.Core.Plugins.PluginsConfig
            {
                Enabled = false
            }
        };

        var startup = new GatewayStartupContext
        {
            Config = config,
            RuntimeState = RuntimeModeResolver.Resolve(config.Runtime),
            IsNonLoopbackBind = nonLoopbackBind,
            WorkspacePath = null
        };

        var builder = WebApplication.CreateSlimBuilder();
        builder.WebHost.UseTestServer();
        builder.Services.ConfigureHttpJsonOptions(opts => opts.SerializerOptions.TypeInfoResolverChain.Add(CoreJsonContext.Default));
        var memoryStore = new FileMemoryStore(storagePath, maxCachedSessions: 8);
        builder.Services.AddSingleton<IMemoryStore>(memoryStore);
        builder.Services.AddSingleton(new BrowserSessionAuthService(config));
        builder.Services.AddSingleton(new AdminSettingsService(
            config,
            AdminSettingsService.CreateSnapshot(config),
            AdminSettingsService.GetSettingsPath(config),
            NullLogger<AdminSettingsService>.Instance));

        var app = builder.Build();
        var runtime = CreateRuntime(config, storagePath, memoryStore);
        app.MapOpenClawEndpoints(startup, runtime);
        await app.StartAsync();

        return new GatewayTestHarness(app, app.GetTestClient(), runtime, config.AuthToken!);
    }

    private static GatewayAppRuntime CreateRuntime(GatewayConfig config, string storagePath, IMemoryStore memoryStore)
    {
        var sessionManager = new SessionManager(memoryStore, config, NullLogger.Instance);
        var allowlistSemantics = AllowlistPolicy.ParseSemantics(config.Channels.AllowlistSemantics);
        var allowlists = new AllowlistManager(storagePath, NullLogger<AllowlistManager>.Instance);
        var recentSenders = new RecentSendersStore(storagePath, NullLogger<RecentSendersStore>.Instance);
        var commandProcessor = new ChatCommandProcessor(sessionManager);
        var toolApprovalService = new ToolApprovalService();
        var approvalAuditStore = new ApprovalAuditStore(storagePath, NullLogger<ApprovalAuditStore>.Instance);
        var runtimeMetrics = new RuntimeMetrics();
        var providerUsage = new ProviderUsageTracker();
        var providerRegistry = new LlmProviderRegistry();
        var providerPolicies = new ProviderPolicyService(storagePath, NullLogger<ProviderPolicyService>.Instance);
        var runtimeEvents = new RuntimeEventStore(storagePath, NullLogger<RuntimeEventStore>.Instance);
        var operatorAudit = new OperatorAuditStore(storagePath, NullLogger<OperatorAuditStore>.Instance);
        var approvalGrants = new ToolApprovalGrantStore(storagePath, NullLogger<ToolApprovalGrantStore>.Instance);
        var webhookDeliveries = new WebhookDeliveryStore(storagePath, NullLogger<WebhookDeliveryStore>.Instance);
        var actorRateLimits = new ActorRateLimitService(storagePath, NullLogger<ActorRateLimitService>.Instance);
        var sessionMetadata = new SessionMetadataStore(storagePath, NullLogger<SessionMetadataStore>.Instance);
        var pluginHealth = new PluginHealthService(storagePath, NullLogger<PluginHealthService>.Instance);
        var llmExecution = new GatewayLlmExecutionService(
            config,
            providerRegistry,
            providerPolicies,
            runtimeEvents,
            runtimeMetrics,
            providerUsage,
            NullLogger<GatewayLlmExecutionService>.Instance);
        var retentionCoordinator = Substitute.For<IMemoryRetentionCoordinator>();
        retentionCoordinator.GetStatusAsync(Arg.Any<CancellationToken>())
            .Returns(ValueTask.FromResult(new RetentionRunStatus { Enabled = false, StoreSupportsRetention = false }));
        retentionCoordinator.SweepNowAsync(Arg.Any<bool>(), Arg.Any<CancellationToken>())
            .Returns(ValueTask.FromResult(new RetentionSweepResult()));

        var agentRuntime = Substitute.For<IAgentRuntime>();
        agentRuntime.CircuitBreakerState.Returns(CircuitState.Closed);
        agentRuntime.LoadedSkillNames.Returns(Array.Empty<string>());
        agentRuntime.ReloadSkillsAsync(Arg.Any<CancellationToken>()).Returns(Task.FromResult<IReadOnlyList<string>>(Array.Empty<string>()));

        var pipeline = new MessagePipeline();
        var middleware = new MiddlewarePipeline([]);
        var wsChannel = new WebSocketChannel(config.WebSocket);
        var nativeRegistry = new NativePluginRegistry(config.Plugins.Native, NullLogger.Instance, config.Tooling);
        var skillWatcher = new SkillWatcherService(config.Skills, null, [], agentRuntime, NullLogger<SkillWatcherService>.Instance);

        return new GatewayAppRuntime
        {
            AgentRuntime = agentRuntime,
            OrchestratorId = RuntimeOrchestrator.Native,
            Pipeline = pipeline,
            MiddlewarePipeline = middleware,
            WebSocketChannel = wsChannel,
            ChannelAdapters = new Dictionary<string, OpenClaw.Core.Abstractions.IChannelAdapter>(StringComparer.Ordinal)
            {
                ["websocket"] = wsChannel
            },
            SessionManager = sessionManager,
            RetentionCoordinator = retentionCoordinator,
            PairingManager = new PairingManager(storagePath, NullLogger<PairingManager>.Instance),
            Allowlists = allowlists,
            AllowlistSemantics = allowlistSemantics,
            RecentSenders = recentSenders,
            CommandProcessor = commandProcessor,
            ToolApprovalService = toolApprovalService,
            ApprovalAuditStore = approvalAuditStore,
            RuntimeMetrics = runtimeMetrics,
            ProviderUsage = providerUsage,
            SkillWatcher = skillWatcher,
            PluginReports = Array.Empty<PluginLoadReport>(),
            Operations = new RuntimeOperationsState
            {
                ProviderPolicies = providerPolicies,
                ProviderRegistry = providerRegistry,
                LlmExecution = llmExecution,
                PluginHealth = pluginHealth,
                ApprovalGrants = approvalGrants,
                RuntimeEvents = runtimeEvents,
                OperatorAudit = operatorAudit,
                WebhookDeliveries = webhookDeliveries,
                ActorRateLimits = actorRateLimits,
                SessionMetadata = sessionMetadata
            },
            EffectiveRequireToolApproval = false,
            EffectiveApprovalRequiredTools = Array.Empty<string>(),
            NativeRegistry = nativeRegistry,
            SessionLocks = new ConcurrentDictionary<string, SemaphoreSlim>(),
            LockLastUsed = new ConcurrentDictionary<string, DateTimeOffset>(),
            AllowedOriginsSet = null,
            DynamicProviderOwners = Array.Empty<string>(),
            CronTask = null,
            TwilioSmsWebhookHandler = null,
            PluginHost = null,
            NativeDynamicPluginHost = null
        };
    }

    private sealed record GatewayTestHarness(
        WebApplication App,
        HttpClient Client,
        GatewayAppRuntime Runtime,
        string AuthToken) : IAsyncDisposable
    {
        public async ValueTask DisposeAsync()
        {
            Client.Dispose();
            await App.DisposeAsync();
        }
    }
}

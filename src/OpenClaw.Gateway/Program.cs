using System.Net;
using System.Text;
using System.Text.Json;
using System.Net.Http.Headers;
using Microsoft.AspNetCore.HttpOverrides;
using Microsoft.Extensions.AI;
using OpenClaw.Gateway;
using OpenClaw.Agent;
using OpenClaw.Agent.Plugins;
using OpenClaw.Agent.Tools;
using OpenClaw.Channels;
using OpenClaw.Core.Abstractions;
using OpenClaw.Core.Contacts;
using OpenClaw.Core.Memory;
using OpenClaw.Core.Middleware;
using OpenClaw.Core.Models;
using OpenClaw.Core.Pipeline;
using OpenClaw.Core.Sessions;
using OpenClaw.Core.Security;
using OpenClaw.Core.Skills;
using OpenClaw.Core.Observability;
using OpenClaw.Core.Validation;

// ── Bootstrap ──────────────────────────────────────────────────────────
var builder = WebApplication.CreateSlimBuilder(args);

builder.Logging.ClearProviders();
builder.Logging.AddConsole();

// AOT-compatible JSON
builder.Services.ConfigureHttpJsonOptions(opts =>
    opts.SerializerOptions.TypeInfoResolverChain.Add(CoreJsonContext.Default));

// Configuration
var config = builder.Configuration.GetSection("OpenClaw").Get<GatewayConfig>() ?? new GatewayConfig();

// Override from environment (12-factor friendly)
config.Llm.ApiKey ??= Environment.GetEnvironmentVariable("OPENCLAW_API_KEY");
config.Llm.Model = Environment.GetEnvironmentVariable("OPENCLAW_MODEL") ?? config.Llm.Model;
config.Llm.Endpoint ??= Environment.GetEnvironmentVariable("OPENCLAW_ENDPOINT");
config.AuthToken ??= Environment.GetEnvironmentVariable("OPENCLAW_AUTH_TOKEN");

var isNonLoopbackBind = !GatewaySecurity.IsLoopbackBind(config.BindAddress);

// Healthcheck mode for minimal/distroless containers (no curl/wget).
// Exits 0 if the running gateway reports healthy, else non-zero.
if (args.Any(a => string.Equals(a, "--health-check", StringComparison.Ordinal)))
{
    var url = $"http://127.0.0.1:{config.Port}/health";
    using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(2) };
    using var req = new HttpRequestMessage(HttpMethod.Get, url);
    if (isNonLoopbackBind && !string.IsNullOrWhiteSpace(config.AuthToken))
        req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", config.AuthToken);

    try
    {
        using var resp = await http.SendAsync(req);
        Environment.ExitCode = resp.IsSuccessStatusCode ? 0 : 1;
    }
    catch
    {
        Environment.ExitCode = 1;
    }

    return;
}

if (isNonLoopbackBind && string.IsNullOrWhiteSpace(config.AuthToken))
    throw new InvalidOperationException("OPENCLAW_AUTH_TOKEN must be set when binding to a non-loopback address.");

// ── Configuration Validation ───────────────────────────────────────────
var configErrors = ConfigValidator.Validate(config);
if (configErrors.Count > 0)
{
    foreach (var err in configErrors)
        Console.Error.WriteLine($"Configuration error: {err}");
    throw new InvalidOperationException(
        $"Gateway configuration has {configErrors.Count} error(s). See above for details.");
}

EnforcePublicBindHardening(config, isNonLoopbackBind);

// ── Services ───────────────────────────────────────────────────────────
var memoryStore = new FileMemoryStore(
    config.Memory.StoragePath,
    config.Memory.MaxCachedSessions ?? config.MaxConcurrentSessions);
var runtimeMetrics = new RuntimeMetrics();
var pipeline = new MessagePipeline();
var wsChannel = new WebSocketChannel(config.WebSocket);

TwilioSmsChannel? smsChannel = null;
TwilioSmsWebhookHandler? smsWebhookHandler = null;
if (config.Channels.Sms.Twilio.Enabled)
{
    if (config.Channels.Sms.Twilio.ValidateSignature && string.IsNullOrWhiteSpace(config.Channels.Sms.Twilio.WebhookPublicBaseUrl))
        throw new InvalidOperationException("OpenClaw:Channels:Sms:Twilio:WebhookPublicBaseUrl must be set when ValidateSignature is true.");

    var twilioAuthToken = ResolveSecretRef(config.Channels.Sms.Twilio.AuthTokenRef)
        ?? throw new InvalidOperationException("Twilio AuthTokenRef is not configured or could not be resolved.");

    var contacts = new FileContactStore(config.Memory.StoragePath);
    var httpClient = OpenClaw.Core.Http.HttpClientFactory.Create();
    smsChannel = new TwilioSmsChannel(config.Channels.Sms.Twilio, twilioAuthToken, contacts, httpClient);
    smsWebhookHandler = new TwilioSmsWebhookHandler(config.Channels.Sms.Twilio, twilioAuthToken, contacts);
}

var channelAdapters = new Dictionary<string, IChannelAdapter>(StringComparer.Ordinal)
{
    ["websocket"] = wsChannel
};

if (smsChannel is not null)
    channelAdapters["sms"] = smsChannel;

// LLM client via Microsoft.Extensions.AI (provider-agnostic, AOT-safe)
// Providers: "openai" (default), "ollama" (local), "azure-openai",
//            "anthropic" / "google" / "groq" / "together" (via OpenAI-compatible endpoints)
IChatClient chatClient = config.Llm.Provider.ToLowerInvariant() switch
{
    "openai" => CreateOpenAiClient(config.Llm)
        .GetChatClient(config.Llm.Model)
        .AsIChatClient(),
    "ollama" => CreateOpenAiClient(new LlmProviderConfig
        {
            ApiKey = config.Llm.ApiKey ?? "ollama",
            Endpoint = config.Llm.Endpoint ?? "http://localhost:11434/v1",
            Model = config.Llm.Model
        })
        .GetChatClient(config.Llm.Model)
        .AsIChatClient(),
    "azure-openai" => CreateAzureOpenAiClient(config.Llm)
        .GetChatClient(config.Llm.Model)
        .AsIChatClient(),
    // Any OpenAI-compatible provider (Anthropic, Google, Groq, Together, LM Studio, etc.)
    // Just set Provider="openai-compatible", Endpoint="https://api.provider.com/v1", ApiKey="..."
    "openai-compatible" or "anthropic" or "google" or "groq" or "together" or "lmstudio" =>
        CreateOpenAiClient(new LlmProviderConfig
        {
            ApiKey = config.Llm.ApiKey,
            Model = config.Llm.Model,
            Endpoint = config.Llm.Endpoint
                ?? throw new InvalidOperationException(
                    $"Endpoint must be set for provider '{config.Llm.Provider}'. " +
                    "Set OpenClaw:Llm:Endpoint or OPENCLAW_ENDPOINT.")
        })
        .GetChatClient(config.Llm.Model)
        .AsIChatClient(),
    _ => throw new InvalidOperationException(
        $"Unsupported LLM provider: {config.Llm.Provider}. " +
        "Supported: openai, ollama, azure-openai, openai-compatible, anthropic, google, groq, together, lmstudio")
};

// Tools (built-in)
var projectId = config.Memory.ProjectId
    ?? Environment.GetEnvironmentVariable("OPENCLAW_PROJECT")
    ?? "default";

var builtInTools = new List<ITool>
{
    new ShellTool(config.Tooling),
    new FileReadTool(config.Tooling),
    new FileWriteTool(config.Tooling),
    new MemoryNoteTool(memoryStore),
    new ProjectMemoryTool(memoryStore, projectId)
};

// ── App ────────────────────────────────────────────────────────────────
var app = builder.Build();

var sessionLogger = app.Services.GetRequiredService<ILoggerFactory>().CreateLogger("SessionManager");
var sessionManager = new SessionManager(memoryStore, config, sessionLogger);

// Native plugin replicas (C# implementations of popular OpenClaw plugins)
var nativeRegistry = new NativePluginRegistry(
    config.Plugins.Native,
    app.Services.GetRequiredService<ILoggerFactory>().CreateLogger<NativePluginRegistry>(),
    config.Tooling);

// Bridge plugin tools (loaded from OpenClaw TypeScript plugin ecosystem)
PluginHost? pluginHost = null;
IReadOnlyList<ITool> bridgeTools = [];
if (config.Plugins.Enabled)
{
    var bridgeScript = Path.Combine(AppContext.BaseDirectory, "Plugins", "plugin-bridge.mjs");
    var pluginLogger = app.Services.GetRequiredService<ILoggerFactory>().CreateLogger<PluginHost>();
    pluginHost = new PluginHost(config.Plugins, bridgeScript, pluginLogger);

    var workspacePath = Environment.GetEnvironmentVariable("OPENCLAW_WORKSPACE");
    bridgeTools = await pluginHost.LoadAsync(workspacePath, app.Lifetime.ApplicationStopping);
}

// Resolve preferences: native vs bridge for overlapping tool names
var resolveLogger = app.Services.GetRequiredService<ILoggerFactory>().CreateLogger("PluginResolver");
IReadOnlyList<ITool> tools = NativePluginRegistry.ResolvePreference(
    builtInTools, nativeRegistry.Tools, bridgeTools, config.Plugins, resolveLogger);

// ── Skills ────────────────────────────────────────────────────────────
var skillLogger = app.Services.GetRequiredService<ILoggerFactory>().CreateLogger("SkillLoader");
var workspacePathForSkills = Environment.GetEnvironmentVariable("OPENCLAW_WORKSPACE");
var skills = SkillLoader.LoadAll(config.Skills, workspacePathForSkills, skillLogger);
if (skills.Count > 0)
    skillLogger.LogInformation("{Summary}", SkillPromptBuilder.BuildSummary(skills));

var agentLogger = app.Services.GetRequiredService<ILoggerFactory>().CreateLogger("AgentRuntime");

// Build tool hooks
var hooks = new List<IToolHook>();
var auditLogger = app.Services.GetRequiredService<ILoggerFactory>().CreateLogger("AuditLog");
hooks.Add(new AuditLogHook(auditLogger));

var agentRuntime = new AgentRuntime(chatClient, tools, memoryStore, config.Llm, config.Memory.MaxHistoryTurns, skills,
    logger: agentLogger, toolTimeoutSeconds: config.Tooling.ToolTimeoutSeconds, metrics: runtimeMetrics,
    parallelToolExecution: config.Tooling.ParallelToolExecution,
    enableCompaction: config.Memory.EnableCompaction,
    compactionThreshold: config.Memory.CompactionThreshold,
    compactionKeepRecent: config.Memory.CompactionKeepRecent,
    requireToolApproval: config.Tooling.RequireToolApproval,
    approvalRequiredTools: config.Tooling.ApprovalRequiredTools,
    hooks: hooks,
    sessionTokenBudget: config.SessionTokenBudget);

// ── Multi-Agent Delegation ─────────────────────────────────────────────
if (config.Delegation.Enabled && config.Delegation.Profiles.Count > 0)
{
    var delegateLogger = app.Services.GetRequiredService<ILoggerFactory>().CreateLogger("DelegateTool");
    var delegateTool = new DelegateTool(
        chatClient, tools, memoryStore, config.Llm, config.Delegation,
        currentDepth: 0, metrics: runtimeMetrics, logger: delegateLogger);

    // Re-create tools list with delegate tool appended
    tools = [.. tools, delegateTool];

    // Rebuild agent runtime with delegation-enabled toolset
    agentRuntime = new AgentRuntime(chatClient, tools, memoryStore, config.Llm, config.Memory.MaxHistoryTurns, skills,
        logger: agentLogger, toolTimeoutSeconds: config.Tooling.ToolTimeoutSeconds, metrics: runtimeMetrics,
        parallelToolExecution: config.Tooling.ParallelToolExecution,
        enableCompaction: config.Memory.EnableCompaction,
        compactionThreshold: config.Memory.CompactionThreshold,
        compactionKeepRecent: config.Memory.CompactionKeepRecent,
        requireToolApproval: config.Tooling.RequireToolApproval,
        approvalRequiredTools: config.Tooling.ApprovalRequiredTools,
        hooks: hooks,
        sessionTokenBudget: config.SessionTokenBudget);
}

// ── Middleware Pipeline ────────────────────────────────────────────────
var middlewareList = new List<IMessageMiddleware>();
if (config.SessionRateLimitPerMinute > 0)
{
    var rlLogger = app.Services.GetRequiredService<ILoggerFactory>().CreateLogger("RateLimit");
    middlewareList.Add(new RateLimitMiddleware(config.SessionRateLimitPerMinute, rlLogger));
}
if (config.SessionTokenBudget > 0)
{
    var tbLogger = app.Services.GetRequiredService<ILoggerFactory>().CreateLogger("TokenBudget");
    middlewareList.Add(new TokenBudgetMiddleware(config.SessionTokenBudget, tbLogger));
}
var middlewarePipeline = new MiddlewarePipeline(middlewareList);

if (config.Security.TrustForwardedHeaders)
{
    var opts = new ForwardedHeadersOptions
    {
        ForwardedHeaders = ForwardedHeaders.XForwardedFor | ForwardedHeaders.XForwardedProto,
        ForwardLimit = 1
    };

    foreach (var proxy in config.Security.KnownProxies)
    {
        if (IPAddress.TryParse(proxy, out var ip))
            opts.KnownProxies.Add(ip);
    }

    app.UseForwardedHeaders(opts);
}

// CORS — when AllowedOrigins is configured, add preflight + header support for HTTP endpoints
if (config.Security.AllowedOrigins.Length > 0)
{
    var allowedOriginsSet = new HashSet<string>(config.Security.AllowedOrigins, StringComparer.Ordinal);
    app.Use(async (ctx, next) =>
    {
        if (ctx.Request.Headers.TryGetValue("Origin", out var origin))
        {
            var originStr = origin.ToString();
            if (allowedOriginsSet.Contains(originStr))
            {
                ctx.Response.Headers["Access-Control-Allow-Origin"] = originStr;
                ctx.Response.Headers["Access-Control-Allow-Methods"] = "GET, POST, OPTIONS";
                ctx.Response.Headers["Access-Control-Allow-Headers"] = "Authorization, Content-Type";
                ctx.Response.Headers["Access-Control-Max-Age"] = "3600";
                ctx.Response.Headers.Vary = "Origin";
            }

            if (ctx.Request.Method == "OPTIONS")
            {
                ctx.Response.StatusCode = 204;
                return;
            }
        }

        await next();
    });
}

app.UseWebSockets(new WebSocketOptions
{
    KeepAliveInterval = TimeSpan.FromSeconds(30)
});

// Health check — useful for monitoring
app.MapGet("/health", (HttpContext ctx) =>
{
    if (isNonLoopbackBind)
    {
        var token = GatewaySecurity.GetToken(ctx, config.Security.AllowQueryStringToken);
        if (!GatewaySecurity.IsTokenValid(token, config.AuthToken!))
            return Results.Unauthorized();
    }

    return Results.Ok(new { status = "ok", uptime = Environment.TickCount64 });
});

// Detailed metrics endpoint — same auth as health
app.MapGet("/metrics", (HttpContext ctx) =>
{
    if (isNonLoopbackBind)
    {
        var token = GatewaySecurity.GetToken(ctx, config.Security.AllowQueryStringToken);
        if (!GatewaySecurity.IsTokenValid(token, config.AuthToken!))
            return Results.Unauthorized();
    }

    runtimeMetrics.SetActiveSessions(sessionManager.ActiveCount);
    runtimeMetrics.SetCircuitBreakerState((int)agentRuntime.CircuitBreakerState);
    return Results.Json(runtimeMetrics.Snapshot(), CoreJsonContext.Default.MetricsSnapshot);
});

var sessionLocks = new System.Collections.Concurrent.ConcurrentDictionary<string, SemaphoreSlim>();
SemaphoreSlim GetSessionLock(string sessionId) => sessionLocks.GetOrAdd(sessionId, _ => new SemaphoreSlim(1, 1));

// Periodic cleanup of session locks for expired sessions
_ = Task.Run(async () =>
{
    var logger = app.Services.GetRequiredService<ILogger<Program>>();
    using var timer = new PeriodicTimer(TimeSpan.FromMinutes(5));
    while (await timer.WaitForNextTickAsync(app.Lifetime.ApplicationStopping))
    {
        try 
        {
            foreach (var kvp in sessionLocks)
            {
                // Only remove if the session is no longer active
                if (!sessionManager.IsActive(kvp.Key))
                {
                    // Acquire the semaphore to ensure no in-flight request holds it
                    if (kvp.Value.Wait(0))
                    {
                        try
                        {
                            // Re-check after acquiring — another request may have started
                            if (!sessionManager.IsActive(kvp.Key))
                            {
                                sessionLocks.TryRemove(kvp);
                                // Dispose after removal so any racing GetOrAdd creates a new one
                                kvp.Value.Dispose();
                            }
                        }
                        finally
                        {
                            // Only release if we didn't dispose (i.e., session became active again)
                            if (sessionLocks.ContainsKey(kvp.Key))
                                kvp.Value.Release();
                        }
                    }
                }
            }
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Error during session lock cleanup");
        }
    }
}, app.Lifetime.ApplicationStopping);

// Pipeline workers: inbound -> agent -> outbound
var workerCount = Math.Max(1, Math.Min(Environment.ProcessorCount, 4));
for (var i = 0; i < workerCount; i++)
{
    var workerId = i;
    _ = Task.Run(async () =>
    {
        var logger = app.Services.GetRequiredService<ILogger<Program>>();
        while (await pipeline.InboundReader.WaitToReadAsync(app.Lifetime.ApplicationStopping))
        {
            while (pipeline.InboundReader.TryRead(out var msg))
            {
                var session = await sessionManager.GetOrCreateAsync(msg.ChannelId, msg.SenderId, app.Lifetime.ApplicationStopping);
                var lockObj = GetSessionLock(session.Id);
                await lockObj.WaitAsync(app.Lifetime.ApplicationStopping);

                try
                {
                    // ── Middleware Pipeline ─────────────────────────────────
                    var mwContext = new MessageContext
                    {
                        ChannelId = msg.ChannelId,
                        SenderId = msg.SenderId,
                        Text = msg.Text,
                        MessageId = msg.MessageId,
                        SessionInputTokens = session.TotalInputTokens,
                        SessionOutputTokens = session.TotalOutputTokens
                    };

                    var shouldProceed = await middlewarePipeline.ExecuteAsync(mwContext, app.Lifetime.ApplicationStopping);
                    if (!shouldProceed)
                    {
                        // Middleware short-circuited — send the response directly
                        var shortCircuitText = mwContext.ShortCircuitResponse ?? "Request blocked.";
                        if (msg.ChannelId == "websocket" && wsChannel.IsClientUsingEnvelopes(msg.SenderId))
                        {
                            await wsChannel.SendStreamEventAsync(
                                msg.SenderId, "assistant_message", shortCircuitText, msg.MessageId,
                                app.Lifetime.ApplicationStopping);
                        }
                        else
                        {
                            await pipeline.OutboundWriter.WriteAsync(new OutboundMessage
                            {
                                ChannelId = msg.ChannelId,
                                RecipientId = msg.SenderId,
                                Text = shortCircuitText,
                                ReplyToMessageId = msg.MessageId
                            }, app.Lifetime.ApplicationStopping);
                        }
                        continue;
                    }

                    // Use potentially transformed text from middleware
                    var messageText = mwContext.Text;

                    // Use streaming for WebSocket envelope-mode clients
                    var useStreaming = msg.ChannelId == "websocket" && wsChannel.IsClientUsingEnvelopes(msg.SenderId);

                    if (useStreaming)
                    {
                        await foreach (var evt in agentRuntime.RunStreamingAsync(
                            session, messageText, app.Lifetime.ApplicationStopping))
                        {
                            await wsChannel.SendStreamEventAsync(
                                msg.SenderId, evt.EnvelopeType, evt.Content, msg.MessageId,
                                app.Lifetime.ApplicationStopping);
                        }
                        await sessionManager.PersistAsync(session, app.Lifetime.ApplicationStopping);
                    }
                    else
                    {
                        // Non-streaming path (SMS, raw-text WS)
                        var responseText = await agentRuntime.RunAsync(session, messageText, app.Lifetime.ApplicationStopping);
                        await sessionManager.PersistAsync(session, app.Lifetime.ApplicationStopping);

                        await pipeline.OutboundWriter.WriteAsync(new OutboundMessage
                        {
                            ChannelId = msg.ChannelId,
                            RecipientId = msg.SenderId,
                            Text = responseText,
                            ReplyToMessageId = msg.MessageId
                        }, app.Lifetime.ApplicationStopping);
                    }
                }
                catch (OperationCanceledException)
                {
                    logger.LogWarning("Request canceled for session {SessionId}", session.Id);
                }
                catch (Exception ex)
                {
                    logger.LogError(ex, "Internal error processing message for session {SessionId}", session.Id);

                    // Try to send error to the client
                    try
                    {
                        var errorText = $"Internal error ({ex.GetType().Name}).";
                        if (msg.ChannelId == "websocket" && wsChannel.IsClientUsingEnvelopes(msg.SenderId))
                        {
                            await wsChannel.SendStreamEventAsync(
                                msg.SenderId, "error", errorText, msg.MessageId,
                                app.Lifetime.ApplicationStopping);
                        }
                        else
                        {
                            await pipeline.OutboundWriter.WriteAsync(new OutboundMessage
                            {
                                ChannelId = msg.ChannelId,
                                RecipientId = msg.SenderId,
                                Text = errorText,
                                ReplyToMessageId = msg.MessageId
                            }, app.Lifetime.ApplicationStopping);
                        }
                    }
                    catch { /* Best effort */ }
                }
                finally
                {
                    lockObj.Release();
                }
            }
        }
    }, app.Lifetime.ApplicationStopping);
}

for (var j = 0; j < workerCount; j++)
{
    var workerId = j;
    _ = Task.Run(async () =>
    {
        var logger = app.Services.GetRequiredService<ILogger<Program>>();
        while (await pipeline.OutboundReader.WaitToReadAsync(app.Lifetime.ApplicationStopping))
        {
            while (pipeline.OutboundReader.TryRead(out var outbound))
            {
                if (!channelAdapters.TryGetValue(outbound.ChannelId, out var adapter))
                {
                    logger.LogWarning("Unknown channel {ChannelId} for outbound message to {RecipientId}", outbound.ChannelId, outbound.RecipientId);
                    continue;
                }

                // One retry attempt for transient delivery failures
                const int maxDeliveryAttempts = 2;
                for (var attempt = 1; attempt <= maxDeliveryAttempts; attempt++)
                {
                    try
                    {
                        await adapter.SendAsync(outbound, app.Lifetime.ApplicationStopping);
                        break; // Success — exit retry loop
                    }
                    catch (OperationCanceledException) when (app.Lifetime.ApplicationStopping.IsCancellationRequested)
                    {
                        return;
                    }
                    catch (Exception ex)
                    {
                        if (attempt < maxDeliveryAttempts)
                        {
                            logger.LogWarning(ex, "Outbound send failed for channel {ChannelId}, retrying…", outbound.ChannelId);
                            await Task.Delay(500, app.Lifetime.ApplicationStopping);
                        }
                        else
                        {
                            logger.LogError(ex, "Outbound send failed for channel {ChannelId} after {Attempts} attempts", outbound.ChannelId, maxDeliveryAttempts);
                        }
                    }
                }
            }
        }
    }, app.Lifetime.ApplicationStopping);
}

// WebSocket endpoint — the primary control plane
app.Map("/ws", async (HttpContext ctx) =>
{
    if (!ctx.WebSockets.IsWebSocketRequest)
    {
        ctx.Response.StatusCode = 400;
        return;
    }

    if (config.Security.AllowedOrigins.Length > 0 && ctx.Request.Headers.TryGetValue("Origin", out var origin))
    {
        var originsSetForWs = new HashSet<string>(config.Security.AllowedOrigins, StringComparer.Ordinal);
        if (!originsSetForWs.Contains(origin.ToString()))
        {
            ctx.Response.StatusCode = 403;
            return;
        }
    }

    if (isNonLoopbackBind)
    {
        var token = GatewaySecurity.GetToken(ctx, config.Security.AllowQueryStringToken);
        if (!GatewaySecurity.IsTokenValid(token, config.AuthToken!))
        {
            ctx.Response.StatusCode = 401;
            return;
        }
    }

    var ws = await ctx.WebSockets.AcceptWebSocketAsync();
    var clientId = ctx.Connection.Id;
    await wsChannel.HandleConnectionAsync(ws, clientId, ctx.Connection.RemoteIpAddress, ctx.RequestAborted);
});

// Wire up the message flow: channel → agent → channel
wsChannel.OnMessageReceived += async (msg, ct) =>
{
    if (!pipeline.InboundWriter.TryWrite(msg))
    {
        await wsChannel.SendAsync(new OutboundMessage
        {
            ChannelId = msg.ChannelId,
            RecipientId = msg.SenderId,
            Text = "Server is busy. Please retry.",
            ReplyToMessageId = msg.MessageId
        }, ct);
    }
};

if (smsChannel is not null && smsWebhookHandler is not null)
{
    app.MapPost(config.Channels.Sms.Twilio.WebhookPath, async (HttpContext ctx) =>
    {
        if (!ctx.Request.HasFormContentType)
        {
            ctx.Response.StatusCode = StatusCodes.Status400BadRequest;
            await ctx.Response.WriteAsync("Expected form content.", ctx.RequestAborted);
            return;
        }

        var form = await ctx.Request.ReadFormAsync(ctx.RequestAborted);
        var dict = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var kvp in form)
            dict[kvp.Key] = kvp.Value.ToString();

        var sig = ctx.Request.Headers["X-Twilio-Signature"].ToString();

        var res = await smsWebhookHandler.HandleAsync(
            dict,
            sig,
            (msg, ct) => pipeline.InboundWriter.WriteAsync(msg, ct),
            ctx.RequestAborted);

        ctx.Response.StatusCode = res.StatusCode;
        if (res.Body is not null)
        {
            ctx.Response.ContentType = res.ContentType;
            await ctx.Response.WriteAsync(res.Body, ctx.RequestAborted);
        }
    });
}

// ── Run ────────────────────────────────────────────────────────────────
// ── Graceful Shutdown ──────────────────────────────────────────────────
var draining = 0; // 0 = normal, 1 = draining
app.Lifetime.ApplicationStopping.Register(() =>
{
    Interlocked.Exchange(ref draining, 1);
    app.Logger.LogInformation("Shutdown signal received — draining in-flight requests ({Timeout}s timeout)…",
        config.GracefulShutdownSeconds);

    if (config.GracefulShutdownSeconds > 0)
    {
        // Wait for all session locks to be released (in-flight requests to complete)
        var deadline = DateTimeOffset.UtcNow.AddSeconds(config.GracefulShutdownSeconds);
        while (DateTimeOffset.UtcNow < deadline)
        {
            var allFree = true;
            foreach (var kvp in sessionLocks)
            {
                if (kvp.Value.CurrentCount == 0) // Lock is held
                {
                    allFree = false;
                    break;
                }
            }
            if (allFree) break;
            Thread.Sleep(100);
        }

        app.Logger.LogInformation("Drain complete — shutting down");
    }

    if (pluginHost is not null)
        pluginHost.DisposeAsync().AsTask().GetAwaiter().GetResult();
    nativeRegistry.Dispose();
});

app.Logger.LogInformation($"""
    ╔══════════════════════════════════════════╗
    ║  OpenClaw.NET Gateway                    ║
    ║  Listening: ws://{config.BindAddress}:{config.Port}/ws  ║
    ║  Model: {config.Llm.Model,-33}║
    ║  NativeAOT: {(AppContext.TryGetSwitch("System.Runtime.CompilerServices.RuntimeFeature.IsDynamicCodeSupported", out var isDynamic) && !isDynamic ? "Yes" : "No"),-29}║
    ╚══════════════════════════════════════════╝
    """);

app.Run($"http://{config.BindAddress}:{config.Port}");


static OpenAI.OpenAIClient CreateOpenAiClient(LlmProviderConfig llm)
{
    if (string.IsNullOrWhiteSpace(llm.ApiKey))
        throw new InvalidOperationException("OPENCLAW_API_KEY must be set for the OpenAI provider.");

    if (string.IsNullOrWhiteSpace(llm.Endpoint))
        return new OpenAI.OpenAIClient(llm.ApiKey);

    var options = new OpenAI.OpenAIClientOptions
    {
        Endpoint = new Uri(llm.Endpoint, UriKind.Absolute)
    };

    return new OpenAI.OpenAIClient(new System.ClientModel.ApiKeyCredential(llm.ApiKey), options);
}

static OpenAI.OpenAIClient CreateAzureOpenAiClient(LlmProviderConfig llm)
{
    if (string.IsNullOrWhiteSpace(llm.ApiKey))
        throw new InvalidOperationException("OPENCLAW_API_KEY must be set for the Azure OpenAI provider.");
    if (string.IsNullOrWhiteSpace(llm.Endpoint))
        throw new InvalidOperationException("OPENCLAW_ENDPOINT must be set for the Azure OpenAI provider (e.g. https://myresource.openai.azure.com/).");

    var options = new OpenAI.OpenAIClientOptions
    {
        Endpoint = new Uri(llm.Endpoint, UriKind.Absolute)
    };

    return new OpenAI.OpenAIClient(new System.ClientModel.ApiKeyCredential(llm.ApiKey), options);
}

static string? ResolveSecretRef(string? secretRef) => SecretResolver.Resolve(secretRef);

static void EnforcePublicBindHardening(GatewayConfig config, bool isNonLoopbackBind)
{
    if (!isNonLoopbackBind)
        return;

    var toolingUnsafe =
        config.Tooling.AllowShell ||
        config.Tooling.AllowedReadRoots.Contains("*", StringComparer.Ordinal) ||
        config.Tooling.AllowedWriteRoots.Contains("*", StringComparer.Ordinal);

    if (toolingUnsafe && !config.Security.AllowUnsafeToolingOnPublicBind)
    {
        throw new InvalidOperationException(
            "Refusing to start with unsafe tooling settings on a non-loopback bind. " +
            "Set OpenClaw:Tooling:AllowShell=false and restrict AllowedReadRoots/AllowedWriteRoots, " +
            "or explicitly opt in via OpenClaw:Security:AllowUnsafeToolingOnPublicBind=true.");
    }

    if (config.Plugins.Enabled && !config.Security.AllowPluginBridgeOnPublicBind)
    {
        throw new InvalidOperationException(
            "Refusing to start with the JS plugin bridge enabled on a non-loopback bind. " +
            "Disable OpenClaw:Plugins:Enabled, or explicitly opt in via OpenClaw:Security:AllowPluginBridgeOnPublicBind=true.");
    }

    if (!config.Security.AllowRawSecretRefsOnPublicBind)
    {
        if (SecretResolver.IsRawRef(config.Channels.Sms.Twilio.AuthTokenRef))
        {
            throw new InvalidOperationException(
                "Refusing to start with a raw: secret ref on a non-loopback bind. " +
                "Use env:... / OS keychain storage, or explicitly opt in via OpenClaw:Security:AllowRawSecretRefsOnPublicBind=true.");
        }
    }
}

using System.Net;
using System.Text;
using System.Text.Json;
using System.Net.Http.Headers;
using Microsoft.AspNetCore.Http.Features;
using Microsoft.AspNetCore.HttpOverrides;
using Microsoft.AspNetCore.WebUtilities;
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
using OpenClaw.Gateway.Extensions;

// ── Bootstrap ──────────────────────────────────────────────────────────
var builder = WebApplication.CreateSlimBuilder(args);
builder.AddGatewayTelemetry();

builder.Logging.ClearProviders();
builder.Logging.AddConsole();

// AOT-compatible JSON
builder.Services.ConfigureHttpJsonOptions(opts =>
    opts.SerializerOptions.TypeInfoResolverChain.Add(CoreJsonContext.Default));

// Configuration
var config = builder.Configuration.GetSection("OpenClaw").Get<GatewayConfig>() ?? new GatewayConfig();

// Override from environment (12-factor friendly)
config.Llm.ApiKey ??= Environment.GetEnvironmentVariable("MODEL_PROVIDER_KEY");
config.Llm.Model = Environment.GetEnvironmentVariable("MODEL_PROVIDER_MODEL") ?? config.Llm.Model;
config.Llm.Endpoint ??= Environment.GetEnvironmentVariable("MODEL_PROVIDER_ENDPOINT");
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

GatewaySecurityExtensions.EnforcePublicBindHardening(config, isNonLoopbackBind);

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

    var twilioAuthToken = SecretResolver.Resolve(config.Channels.Sms.Twilio.AuthTokenRef)
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

if (config.Channels.WhatsApp.Enabled)
{
    builder.Services.AddSingleton(config.Channels.WhatsApp);
    builder.Services.AddSingleton<WhatsAppWebhookHandler>();
    if (config.Channels.WhatsApp.Type == "bridge")
    {
        builder.Services.AddSingleton<WhatsAppBridgeChannel>(sp =>
            new WhatsAppBridgeChannel(
                config.Channels.WhatsApp,
                OpenClaw.Core.Http.HttpClientFactory.Create(),
                sp.GetRequiredService<ILogger<WhatsAppBridgeChannel>>()));
    }
    else
    {
        builder.Services.AddSingleton<WhatsAppChannel>(sp =>
            new WhatsAppChannel(
                config.Channels.WhatsApp,
                OpenClaw.Core.Http.HttpClientFactory.Create(),
                sp.GetRequiredService<ILogger<WhatsAppChannel>>()));
    }
}

if (config.Channels.Telegram.Enabled)
{
    builder.Services.AddSingleton(config.Channels.Telegram);
    builder.Services.AddSingleton<TelegramChannel>();
}

// LLM client via Microsoft.Extensions.AI (provider-agnostic, AOT-safe)
IChatClient chatClient = LlmClientFactory.CreateChatClient(config.Llm);

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

// Retrieve TelegramChannel from DI and add it to the active channels dictionary
if (config.Channels.Telegram.Enabled)
{
    channelAdapters["telegram"] = app.Services.GetRequiredService<TelegramChannel>();
}

if (config.Channels.WhatsApp.Enabled)
{
    if (config.Channels.WhatsApp.Type == "bridge")
        channelAdapters["whatsapp"] = app.Services.GetRequiredService<WhatsAppBridgeChannel>();
    else
        channelAdapters["whatsapp"] = app.Services.GetRequiredService<WhatsAppChannel>();
}

var sessionLogger = app.Services.GetRequiredService<ILoggerFactory>().CreateLogger("SessionManager");
var sessionManager = new SessionManager(memoryStore, config, sessionLogger);

var pairingLogger = app.Services.GetRequiredService<ILogger<PairingManager>>();
var pairingManager = new PairingManager(config.Memory.StoragePath, pairingLogger);
var commandProcessor = new ChatCommandProcessor(sessionManager);

builtInTools.Add(new SessionsTool(sessionManager, pipeline.InboundWriter));

if (config.Tooling.EnableBrowserTool)
{
    builtInTools.Add(new BrowserTool(config.Tooling));
}

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

// ── Multi-Agent Delegation ─────────────────────────────────────────────
if (config.Delegation.Enabled && config.Delegation.Profiles.Count > 0)
{
    var delegateLogger = app.Services.GetRequiredService<ILoggerFactory>().CreateLogger("DelegateTool");
    var delegateTool = new DelegateTool(
        chatClient, tools, memoryStore, config.Llm, config.Delegation,
        currentDepth: 0, metrics: runtimeMetrics, logger: delegateLogger);

    tools = [.. tools, delegateTool];
}

// Construct AgentRuntime once with the final tools list (including DelegateTool if enabled)
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
// Shared set used by both CORS middleware and WebSocket origin check
var allowedOriginsSet = config.Security.AllowedOrigins.Length > 0
    ? new HashSet<string>(config.Security.AllowedOrigins, StringComparer.Ordinal)
    : null;

if (allowedOriginsSet is not null)
{
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

static bool IsAuthorizedRequest(HttpContext ctx, bool nonLoopbackBind, GatewayConfig gatewayConfig)
{
    if (!nonLoopbackBind)
        return true;

    var token = GatewaySecurity.GetToken(ctx, gatewayConfig.Security.AllowQueryStringToken);
    return GatewaySecurity.IsTokenValid(token, gatewayConfig.AuthToken!);
}

static bool TrySetMaxRequestBodySize(HttpContext ctx, long maxBytes)
{
    var feature = ctx.Features.Get<IHttpMaxRequestBodySizeFeature>();
    if (feature is { IsReadOnly: false })
    {
        feature.MaxRequestBodySize = maxBytes;
        return true;
    }

    return false;
}

static async Task<(bool Success, string Text)> TryReadBodyTextAsync(HttpContext ctx, long maxBytes, CancellationToken ct)
{
    var contentLength = ctx.Request.ContentLength;
    if (contentLength.HasValue && contentLength.Value > maxBytes)
        return (false, "");

    TrySetMaxRequestBodySize(ctx, maxBytes);

    var buffer = new byte[8 * 1024];
    await using var ms = new MemoryStream();
    while (true)
    {
        var read = await ctx.Request.Body.ReadAsync(buffer.AsMemory(0, buffer.Length), ct);
        if (read == 0)
            break;

        if (ms.Length + read > maxBytes)
            return (false, "");

        ms.Write(buffer, 0, read);
    }

    return (true, Encoding.UTF8.GetString(ms.GetBuffer(), 0, (int)ms.Length));
}

// Health check — useful for monitoring
app.MapGet("/health", (HttpContext ctx) =>
{
    if (!IsAuthorizedRequest(ctx, isNonLoopbackBind, config))
        return Results.Unauthorized();

    return Results.Ok(new { status = "ok", uptime = Environment.TickCount64 });
});

// Detailed metrics endpoint — same auth as health
app.MapGet("/metrics", (HttpContext ctx) =>
{
    if (!IsAuthorizedRequest(ctx, isNonLoopbackBind, config))
        return Results.Unauthorized();

    runtimeMetrics.SetActiveSessions(sessionManager.ActiveCount);
    runtimeMetrics.SetCircuitBreakerState((int)agentRuntime.CircuitBreakerState);
    return Results.Json(runtimeMetrics.Snapshot(), CoreJsonContext.Default.MetricsSnapshot);
});

var sessionLocks = new System.Collections.Concurrent.ConcurrentDictionary<string, SemaphoreSlim>();

// Periodic cleanup of session locks for expired sessions
var lockLastUsed = new System.Collections.Concurrent.ConcurrentDictionary<string, DateTimeOffset>();
var programLogger = app.Services.GetRequiredService<ILogger<Program>>();
var workerCount = Math.Max(1, Math.Min(Environment.ProcessorCount, 4));

if (config.Cron.Enabled)
{
    var cronLogger = app.Services.GetRequiredService<ILogger<CronScheduler>>();
    var cronTask = new CronScheduler(config, cronLogger, pipeline.InboundWriter);
    _ = cronTask.StartAsync(app.Lifetime.ApplicationStopping);
}

GatewayWorkers.Start(
    app.Lifetime,
    programLogger,
    workerCount,
    sessionManager,
    sessionLocks,
    lockLastUsed,
    pipeline,
    middlewarePipeline,
    wsChannel,
    agentRuntime,
    channelAdapters,
    config,
    pairingManager,
    commandProcessor);

// Embedded WebChat UI
app.MapGet("/chat", async (HttpContext ctx) =>
{
    var htmlPath = Path.Combine(AppContext.BaseDirectory, "wwwroot", "webchat.html");
    if (File.Exists(htmlPath))
    {
        ctx.Response.ContentType = "text/html";
        await ctx.Response.SendFileAsync(htmlPath);
    }
    else
    {
        ctx.Response.StatusCode = 404;
        await ctx.Response.WriteAsync("WebChat UI not found.");
    }
});

app.MapPost("/pairing/approve", (HttpContext ctx, string channelId, string senderId, string code) =>
{
    if (!IsAuthorizedRequest(ctx, isNonLoopbackBind, config))
        return Results.Unauthorized();

    if (pairingManager.TryApprove(channelId, senderId, code, out var error))
        return Results.Ok(new { success = true, message = "Approved successfully." });

    if (error.Contains("Too many invalid attempts", StringComparison.Ordinal))
        return Results.Json(new { success = false, error }, statusCode: StatusCodes.Status429TooManyRequests);

    return Results.BadRequest(new { success = false, error });
});

app.MapPost("/pairing/revoke", (HttpContext ctx, string channelId, string senderId) =>
{
    if (!IsAuthorizedRequest(ctx, isNonLoopbackBind, config))
        return Results.Unauthorized();

    pairingManager.Revoke(channelId, senderId);
    return Results.Ok(new { success = true });
});

app.MapGet("/pairing/list", (HttpContext ctx) =>
{
    if (!IsAuthorizedRequest(ctx, isNonLoopbackBind, config))
        return Results.Unauthorized();

    return Results.Ok(pairingManager.GetApprovedList());
});

// WebSocket endpoint — the primary control plane
app.Map("/ws", async (HttpContext ctx) =>
{
    if (!ctx.WebSockets.IsWebSocketRequest)
    {
        ctx.Response.StatusCode = 400;
        return;
    }

    if (allowedOriginsSet is not null && ctx.Request.Headers.TryGetValue("Origin", out var origin))
    {
        if (!allowedOriginsSet.Contains(origin.ToString()))
        {
            ctx.Response.StatusCode = 403;
            return;
        }
    }

    if (isNonLoopbackBind)
    {
        if (!IsAuthorizedRequest(ctx, isNonLoopbackBind, config))
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
        var maxRequestSize = Math.Max(4 * 1024, config.Channels.Sms.Twilio.MaxRequestBytes);

        if (!ctx.Request.HasFormContentType)
        {
            ctx.Response.StatusCode = StatusCodes.Status400BadRequest;
            await ctx.Response.WriteAsync("Expected form content.", ctx.RequestAborted);
            return;
        }

        var (bodyOk, bodyText) = await TryReadBodyTextAsync(ctx, maxRequestSize, ctx.RequestAborted);
        if (!bodyOk)
        {
            ctx.Response.StatusCode = StatusCodes.Status413PayloadTooLarge;
            await ctx.Response.WriteAsync("Request too large.", ctx.RequestAborted);
            return;
        }

        var parsed = QueryHelpers.ParseQuery(bodyText);
        var dict = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var kvp in parsed)
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
if (config.Channels.Telegram.Enabled)
{
    // Resolve the Telegram webhook secret token once at startup
    byte[]? telegramSecretBytes = null;
    if (config.Channels.Telegram.ValidateSignature)
    {
        var telegramSecret = config.Channels.Telegram.WebhookSecretToken
            ?? SecretResolver.Resolve(config.Channels.Telegram.WebhookSecretTokenRef);
        if (string.IsNullOrWhiteSpace(telegramSecret))
            throw new InvalidOperationException(
                "Telegram ValidateSignature is true but WebhookSecretToken/WebhookSecretTokenRef could not be resolved. " +
                "Set TELEGRAM_WEBHOOK_SECRET or disable ValidateSignature.");
        telegramSecretBytes = Encoding.UTF8.GetBytes(telegramSecret);
    }

    app.MapPost(config.Channels.Telegram.WebhookPath, async (HttpContext ctx) =>
    {
        // Validate X-Telegram-Bot-Api-Secret-Token header
        if (telegramSecretBytes is not null)
        {
            var provided = ctx.Request.Headers["X-Telegram-Bot-Api-Secret-Token"].ToString();
            var providedBytes = Encoding.UTF8.GetBytes(provided ?? "");
            if (providedBytes.Length != telegramSecretBytes.Length ||
                !System.Security.Cryptography.CryptographicOperations.FixedTimeEquals(
                    providedBytes, telegramSecretBytes))
            {
                ctx.Response.StatusCode = 401;
                return;
            }
        }

        var maxRequestSize = Math.Max(4 * 1024, config.Channels.Telegram.MaxRequestBytes);
        var (bodyOk, bodyText) = await TryReadBodyTextAsync(ctx, maxRequestSize, ctx.RequestAborted);
        if (!bodyOk)
        {
            ctx.Response.StatusCode = StatusCodes.Status413PayloadTooLarge;
            await ctx.Response.WriteAsync("Request too large.", ctx.RequestAborted);
            return;
        }

        using var document = JsonDocument.Parse(bodyText,
            new JsonDocumentOptions { MaxDepth = 64 });
        var root = document.RootElement;

        if (root.TryGetProperty("message", out var message) &&
            message.TryGetProperty("text", out var textNode) &&
            message.TryGetProperty("chat", out var chat) &&
            chat.TryGetProperty("id", out var chatId))
        {
            var text = textNode.GetString() ?? "";
            if (text.Length > config.Channels.Telegram.MaxInboundChars)
                text = text[..config.Channels.Telegram.MaxInboundChars];
            var senderIdStr = chatId.GetRawText();

            if (config.Channels.Telegram.AllowedFromUserIds.Length > 0 &&
                !config.Channels.Telegram.AllowedFromUserIds.Contains(senderIdStr))
            {
                ctx.Response.StatusCode = 403;
                return;
            }

            var msg = new InboundMessage
            {
                ChannelId = "telegram",
                SenderId = senderIdStr,
                Text = text
            };

            await pipeline.InboundWriter.WriteAsync(msg, ctx.RequestAborted);
        }

        ctx.Response.StatusCode = 200;
        await ctx.Response.WriteAsync("OK");
    });
}

if (config.Channels.WhatsApp.Enabled)
{
    var whatsappWebhookHandler = app.Services.GetRequiredService<WhatsAppWebhookHandler>();
    app.MapMethods(config.Channels.WhatsApp.WebhookPath, ["GET", "POST"], async (HttpContext ctx) =>
    {
        var res = await whatsappWebhookHandler.HandleAsync(
            ctx,
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

if (config.Webhooks.Enabled)
{
    app.MapPost("/webhooks/{name}", async (HttpContext ctx, string name) =>
    {
        if (!config.Webhooks.Endpoints.TryGetValue(name, out var hookCfg))
        {
            ctx.Response.StatusCode = 404;
            return;
        }

        var maxRequestSize = Math.Max(4 * 1024, hookCfg.MaxRequestBytes);
        var (bodyOk, body) = await TryReadBodyTextAsync(ctx, maxRequestSize, ctx.RequestAborted);
        if (!bodyOk)
        {
            ctx.Response.StatusCode = StatusCodes.Status413PayloadTooLarge;
            await ctx.Response.WriteAsync("Request too large.", ctx.RequestAborted);
            return;
        }

        // Truncate body to limit prompt injection surface
        if (body.Length > hookCfg.MaxBodyLength)
            body = body[..hookCfg.MaxBodyLength];

        if (hookCfg.ValidateHmac && !string.IsNullOrWhiteSpace(hookCfg.Secret))
        {
            var signatureHeader = ctx.Request.Headers[hookCfg.HmacHeader].ToString();
            var computed = GatewaySecurity.ComputeHmacSha256Hex(hookCfg.Secret, body);
            if (!string.Equals(signatureHeader, computed, StringComparison.OrdinalIgnoreCase))
            {
                ctx.Response.StatusCode = 401;
                return;
            }
        }

        var prompt = hookCfg.PromptTemplate.Replace("{body}", body);
        var msg = new InboundMessage
        {
            ChannelId = "webhook",
            SessionId = hookCfg.SessionId ?? $"webhook:{name}",
            SenderId = "system",
            Text = prompt
        };

        await pipeline.InboundWriter.WriteAsync(msg, ctx.RequestAborted);
        ctx.Response.StatusCode = 202; // Accepted
        await ctx.Response.WriteAsync("Webhook queued.");
    });
}
// ── Run ────────────────────────────────────────────────────────────────
// ── Graceful Shutdown ──────────────────────────────────────────────────
var draining = 0; // 0 = normal, 1 = draining
var drainCompleteEvent = new ManualResetEventSlim(false);

app.Lifetime.ApplicationStopping.Register(() =>
{
    Interlocked.Exchange(ref draining, 1);
    app.Logger.LogInformation("Shutdown signal received — draining in-flight requests ({Timeout}s timeout)…",
        config.GracefulShutdownSeconds);

    if (config.GracefulShutdownSeconds > 0)
    {
        // Wait for all session locks to be released (in-flight requests to complete)
        var deadline = DateTimeOffset.UtcNow.AddSeconds(config.GracefulShutdownSeconds);
        var checkInterval = TimeSpan.FromMilliseconds(100);
        
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
            
            if (allFree)
            {
                drainCompleteEvent.Set();
                break;
            }
            
            // Event-based wait instead of spin-wait
            var remaining = deadline - DateTimeOffset.UtcNow;
            if (remaining > TimeSpan.Zero)
                drainCompleteEvent.Wait(checkInterval < remaining ? checkInterval : remaining);
        }

        app.Logger.LogInformation("Drain complete — shutting down");
    }

    // Known sync-over-async: ApplicationStopping callback does not support async delegates.
    // Acceptable during process teardown — the brief thread-pool block has no practical impact.
    if (pluginHost is not null)
        pluginHost.DisposeAsync().AsTask().GetAwaiter().GetResult();
    nativeRegistry.Dispose();
    drainCompleteEvent.Dispose();
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

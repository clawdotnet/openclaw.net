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
using OpenClaw.Agent.Integrations;
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

static string? FindArgValue(string[] argv, string name)
{
    for (var i = 0; i < argv.Length; i++)
    {
        var a = argv[i];
        if (a.Equals(name, StringComparison.Ordinal) && i + 1 < argv.Length)
            return argv[i + 1];

        var prefix = name + "=";
        if (a.StartsWith(prefix, StringComparison.Ordinal))
            return a[prefix.Length..];
    }

    return null;
}

static string ExpandPath(string path)
{
    var expanded = Environment.ExpandEnvironmentVariables(path);
    if (expanded.StartsWith('~'))
    {
        expanded = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
            expanded[1..].TrimStart('/').TrimStart('\\'));
    }

    return expanded;
}

var extraConfigPath = FindArgValue(args, "--config")
    ?? Environment.GetEnvironmentVariable("OPENCLAW_CONFIG_PATH");
if (!string.IsNullOrWhiteSpace(extraConfigPath))
{
    var fullPath = Path.GetFullPath(ExpandPath(extraConfigPath));
    builder.Configuration.AddJsonFile(fullPath, optional: false, reloadOnChange: true);
}

static string? ResolveSecretRefOrNull(string? value)
{
    if (string.IsNullOrWhiteSpace(value))
        return null;

    // Only resolve explicit refs to avoid surprising behavior for literal strings.
    if (value.StartsWith("env:", StringComparison.OrdinalIgnoreCase) ||
        value.StartsWith("raw:", StringComparison.OrdinalIgnoreCase))
    {
        return SecretResolver.Resolve(value);
    }

    return value;
}

// AOT-compatible JSON
builder.Services.ConfigureHttpJsonOptions(opts =>
    opts.SerializerOptions.TypeInfoResolverChain.Add(CoreJsonContext.Default));

// Configuration
var config = builder.Configuration.GetSection("OpenClaw").Get<GatewayConfig>() ?? new GatewayConfig();

// Override from environment (12-factor friendly)
config.Llm.ApiKey = ResolveSecretRefOrNull(config.Llm.ApiKey) ?? Environment.GetEnvironmentVariable("MODEL_PROVIDER_KEY");
config.Llm.Model = Environment.GetEnvironmentVariable("MODEL_PROVIDER_MODEL") ?? config.Llm.Model;
config.Llm.Endpoint = ResolveSecretRefOrNull(config.Llm.Endpoint) ?? Environment.GetEnvironmentVariable("MODEL_PROVIDER_ENDPOINT");
config.AuthToken ??= Environment.GetEnvironmentVariable("OPENCLAW_AUTH_TOKEN");

var isNonLoopbackBind = !GatewaySecurity.IsLoopbackBind(config.BindAddress);
var isDoctorMode = args.Any(a => string.Equals(a, "--doctor", StringComparison.Ordinal));

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
{
    var msg = "OPENCLAW_AUTH_TOKEN must be set when binding to a non-loopback address.";
    if (isDoctorMode)
    {
        Console.Error.WriteLine(msg);
        Environment.ExitCode = 1;
        return;
    }

    throw new InvalidOperationException(msg);
}

// ── Configuration Validation ───────────────────────────────────────────
var configErrors = ConfigValidator.Validate(config);
if (configErrors.Count > 0)
{
    foreach (var err in configErrors)
        Console.Error.WriteLine($"Configuration error: {err}");

    if (isDoctorMode)
    {
        Environment.ExitCode = 1;
        return;
    }

    throw new InvalidOperationException($"Gateway configuration has {configErrors.Count} error(s). See above for details.");
}

if (isDoctorMode)
{
    var ok = await DoctorCheck.RunAsync(config);
    Environment.ExitCode = ok ? 0 : 1;
    return;
}

GatewaySecurityExtensions.EnforcePublicBindHardening(config, isNonLoopbackBind);

// ── Services ───────────────────────────────────────────────────────────
builder.Services.AddSingleton(typeof(AllowlistSemantics), AllowlistPolicy.ParseSemantics(config.Channels.AllowlistSemantics));

builder.Services.AddSingleton(sp =>
    new RecentSendersStore(config.Memory.StoragePath, sp.GetRequiredService<ILogger<RecentSendersStore>>()));
builder.Services.AddSingleton(sp =>
    new AllowlistManager(config.Memory.StoragePath, sp.GetRequiredService<ILogger<AllowlistManager>>()));

IMemoryStore memoryStore;
if (string.Equals(config.Memory.Provider, "sqlite", StringComparison.OrdinalIgnoreCase))
{
    var dbPath = config.Memory.Sqlite.DbPath;
    if (!Path.IsPathRooted(dbPath))
    {
        if (dbPath.Contains(Path.DirectorySeparatorChar) || dbPath.Contains(Path.AltDirectorySeparatorChar))
            dbPath = Path.Combine(Directory.GetCurrentDirectory(), dbPath);
        else
            dbPath = Path.Combine(config.Memory.StoragePath, dbPath);
    }

    memoryStore = new SqliteMemoryStore(Path.GetFullPath(dbPath), config.Memory.Sqlite.EnableFts);
}
else
{
    memoryStore = new FileMemoryStore(
        config.Memory.StoragePath,
        config.Memory.MaxCachedSessions ?? config.MaxConcurrentSessions);
}
var runtimeMetrics = new RuntimeMetrics();
var pipeline = new MessagePipeline();
var wsChannel = new WebSocketChannel(config.WebSocket);

TwilioSmsChannel? smsChannel = null;
TwilioSmsWebhookHandler? smsWebhookHandler = null;
IContactStore? smsContacts = null;
string? twilioAuthToken = null;
if (config.Channels.Sms.Twilio.Enabled)
{
    if (config.Channels.Sms.Twilio.ValidateSignature && string.IsNullOrWhiteSpace(config.Channels.Sms.Twilio.WebhookPublicBaseUrl))
        throw new InvalidOperationException("OpenClaw:Channels:Sms:Twilio:WebhookPublicBaseUrl must be set when ValidateSignature is true.");

    twilioAuthToken = SecretResolver.Resolve(config.Channels.Sms.Twilio.AuthTokenRef)
        ?? throw new InvalidOperationException("Twilio AuthTokenRef is not configured or could not be resolved.");

    smsContacts = new FileContactStore(config.Memory.StoragePath);
    var httpClient = OpenClaw.Core.Http.HttpClientFactory.Create();
    smsChannel = new TwilioSmsChannel(config.Channels.Sms.Twilio, twilioAuthToken, smsContacts, httpClient);
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
    new MemorySearchTool((IMemoryNoteSearch)memoryStore),
    new ProjectMemoryTool(memoryStore, projectId)
};

// ── App ────────────────────────────────────────────────────────────────
var app = builder.Build();

var allowlistSemantics = app.Services.GetRequiredService<AllowlistSemantics>();
var allowlists = app.Services.GetRequiredService<AllowlistManager>();
var recentSenders = app.Services.GetRequiredService<RecentSendersStore>();

if (smsChannel is not null && smsContacts is not null && twilioAuthToken is not null)
{
    smsWebhookHandler = new TwilioSmsWebhookHandler(
        config.Channels.Sms.Twilio,
        twilioAuthToken,
        smsContacts,
        allowlists,
        recentSenders,
        allowlistSemantics);
}

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

if (config.Plugins.Native.Email.Enabled)
{
    channelAdapters["email"] = new EmailChannel(config.Plugins.Native.Email);
}

// Default cron delivery sink (for jobs that do not specify ChannelId / RecipientId)
channelAdapters["cron"] = new CronChannel(
    config.Memory.StoragePath,
    app.Services.GetRequiredService<ILoggerFactory>().CreateLogger<CronChannel>());

var sessionLogger = app.Services.GetRequiredService<ILoggerFactory>().CreateLogger("SessionManager");
var sessionManager = new SessionManager(memoryStore, config, sessionLogger);

var pairingLogger = app.Services.GetRequiredService<ILogger<PairingManager>>();
var pairingManager = new PairingManager(config.Memory.StoragePath, pairingLogger);
var commandProcessor = new ChatCommandProcessor(sessionManager);
var toolApprovalService = new ToolApprovalService();

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

var autonomyLogger = app.Services.GetRequiredService<ILoggerFactory>().CreateLogger("AutonomyHook");
hooks.Add(new AutonomyHook(config.Tooling, autonomyLogger));

// Autonomy mode can elevate tool approvals in supervised mode.
var autonomyMode = (config.Tooling.AutonomyMode ?? "full").Trim().ToLowerInvariant();
var effectiveRequireToolApproval = config.Tooling.RequireToolApproval || autonomyMode == "supervised";
var effectiveApprovalRequiredTools = config.Tooling.ApprovalRequiredTools;
if (autonomyMode == "supervised")
{
    var defaults = new[]
    {
        "shell", "write_file", "code_exec", "git", "home_assistant_write", "mqtt_publish",
        "database", "email", "inbox_zero", "calendar", "delegate_agent"
    };

    effectiveApprovalRequiredTools = effectiveApprovalRequiredTools
        .Concat(defaults)
        .Distinct(StringComparer.OrdinalIgnoreCase)
        .ToArray();
}

var agentRuntime = new AgentRuntime(chatClient, tools, memoryStore, config.Llm, config.Memory.MaxHistoryTurns, skills,
    logger: agentLogger, toolTimeoutSeconds: config.Tooling.ToolTimeoutSeconds, metrics: runtimeMetrics,
    parallelToolExecution: config.Tooling.ParallelToolExecution,
    enableCompaction: config.Memory.EnableCompaction,
    compactionThreshold: config.Memory.CompactionThreshold,
    compactionKeepRecent: config.Memory.CompactionKeepRecent,
    requireToolApproval: effectiveRequireToolApproval,
    approvalRequiredTools: effectiveApprovalRequiredTools,
    hooks: hooks,
    sessionTokenBudget: config.SessionTokenBudget,
    recall: config.Memory.Recall);

// ── Multi-Agent Delegation ─────────────────────────────────────────────
if (config.Delegation.Enabled && config.Delegation.Profiles.Count > 0)
{
    var delegateLogger = app.Services.GetRequiredService<ILoggerFactory>().CreateLogger("DelegateTool");
    var delegateTool = new DelegateTool(
        chatClient, tools, memoryStore, config.Llm, config.Delegation,
        currentDepth: 0, metrics: runtimeMetrics, logger: delegateLogger, recall: config.Memory.Recall);

    // Re-create tools list with delegate tool appended
    tools = [.. tools, delegateTool];

    // Rebuild agent runtime with delegation-enabled toolset
    agentRuntime = new AgentRuntime(chatClient, tools, memoryStore, config.Llm, config.Memory.MaxHistoryTurns, skills,
        logger: agentLogger, toolTimeoutSeconds: config.Tooling.ToolTimeoutSeconds, metrics: runtimeMetrics,
        parallelToolExecution: config.Tooling.ParallelToolExecution,
        enableCompaction: config.Memory.EnableCompaction,
        compactionThreshold: config.Memory.CompactionThreshold,
        compactionKeepRecent: config.Memory.CompactionKeepRecent,
        requireToolApproval: effectiveRequireToolApproval,
        approvalRequiredTools: effectiveApprovalRequiredTools,
        hooks: hooks,
        sessionTokenBudget: config.SessionTokenBudget,
        recall: config.Memory.Recall);
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

    return Results.Json(new HealthResponse { Status = "ok", Uptime = Environment.TickCount64 }, CoreJsonContext.Default.HealthResponse);
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

// ── OpenAI-Compatible HTTP Surface ─────────────────────────────────────
// POST /v1/chat/completions — drop-in for any OpenAI-SDK client
app.MapPost("/v1/chat/completions", async (HttpContext ctx) =>
{
    if (isNonLoopbackBind)
    {
        var token = GatewaySecurity.GetToken(ctx, config.Security.AllowQueryStringToken);
        if (!GatewaySecurity.IsTokenValid(token, config.AuthToken!))
        {
            ctx.Response.StatusCode = 401;
            return;
        }
    }

    OpenAiChatCompletionRequest? req;
    try
    {
        req = await JsonSerializer.DeserializeAsync(ctx.Request.Body,
            CoreJsonContext.Default.OpenAiChatCompletionRequest, ctx.RequestAborted);
    }
    catch
    {
        ctx.Response.StatusCode = 400;
        await ctx.Response.WriteAsync("Invalid JSON request body.", ctx.RequestAborted);
        return;
    }

    if (req is null || req.Messages.Count == 0)
    {
        ctx.Response.StatusCode = 400;
        await ctx.Response.WriteAsync("Request must include at least one message.", ctx.RequestAborted);
        return;
    }

    // Extract the last user message as input
    var lastUserMsg = req.Messages.FindLast(m =>
        string.Equals(m.Role, "user", StringComparison.OrdinalIgnoreCase));
    var userText = lastUserMsg?.Content ?? req.Messages[^1].Content;

    // Ephemeral session scoped to this HTTP request
    var requestId = $"oai-http:{Guid.NewGuid():N}";
    var session = await sessionManager.GetOrCreateAsync("openai-http", requestId, ctx.RequestAborted);
    if (req.Model is not null)
        session.ModelOverride = req.Model;

    // Inject prior messages as conversation context (everything except the last user turn we extracted as userText).
    // OpenAI clients resend full transcript each request; the gateway creates an ephemeral session per HTTP request.
    var lastUserIndex = req.Messages.FindLastIndex(m =>
        string.Equals(m.Role, "user", StringComparison.OrdinalIgnoreCase));
    var excludeIndex = lastUserIndex >= 0 ? lastUserIndex : req.Messages.Count - 1;

    for (var i = 0; i < req.Messages.Count; i++)
    {
        if (i == excludeIndex)
            continue;

        var m = req.Messages[i];
        if (string.Equals(m.Role, "system", StringComparison.OrdinalIgnoreCase)
            || string.Equals(m.Role, "user", StringComparison.OrdinalIgnoreCase)
            || string.Equals(m.Role, "assistant", StringComparison.OrdinalIgnoreCase))
        {
            session.History.Add(new ChatTurn { Role = m.Role.ToLowerInvariant(), Content = m.Content });
        }
    }

    var completionId = $"chatcmpl-{Guid.NewGuid():N}"[..29];
    var created = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
    var model = req.Model ?? config.Llm.Model;

    if (req.Stream)
    {
        // SSE streaming
        ctx.Response.ContentType = "text/event-stream";
        ctx.Response.Headers.CacheControl = "no-cache";
        ctx.Response.Headers.Connection = "keep-alive";

        // Send initial role chunk
        var roleChunk = new OpenAiStreamChunk
        {
            Id = completionId, Created = created, Model = model,
            Choices = [new OpenAiStreamChoice { Index = 0, Delta = new OpenAiDelta { Role = "assistant" } }]
        };
        var roleJson = JsonSerializer.Serialize(roleChunk, CoreJsonContext.Default.OpenAiStreamChunk);
        await ctx.Response.WriteAsync($"data: {roleJson}\n\n", ctx.RequestAborted);
        await ctx.Response.Body.FlushAsync(ctx.RequestAborted);

        await foreach (var evt in agentRuntime.RunStreamingAsync(session, userText, ctx.RequestAborted))
        {
            if (evt.Type == AgentStreamEventType.TextDelta && !string.IsNullOrEmpty(evt.Content))
            {
                var chunk = new OpenAiStreamChunk
                {
                    Id = completionId, Created = created, Model = model,
                    Choices = [new OpenAiStreamChoice { Index = 0, Delta = new OpenAiDelta { Content = evt.Content } }]
                };
                var json = JsonSerializer.Serialize(chunk, CoreJsonContext.Default.OpenAiStreamChunk);
                await ctx.Response.WriteAsync($"data: {json}\n\n", ctx.RequestAborted);
                await ctx.Response.Body.FlushAsync(ctx.RequestAborted);
            }
            else if (evt.Type == AgentStreamEventType.Done)
            {
                var doneChunk = new OpenAiStreamChunk
                {
                    Id = completionId, Created = created, Model = model,
                    Choices = [new OpenAiStreamChoice { Index = 0, Delta = new OpenAiDelta(), FinishReason = "stop" }]
                };
                var doneJson = JsonSerializer.Serialize(doneChunk, CoreJsonContext.Default.OpenAiStreamChunk);
                await ctx.Response.WriteAsync($"data: {doneJson}\n\n", ctx.RequestAborted);
                await ctx.Response.WriteAsync("data: [DONE]\n\n", ctx.RequestAborted);
                await ctx.Response.Body.FlushAsync(ctx.RequestAborted);
            }
        }
    }
    else
    {
        // Non-streaming
        var result = await agentRuntime.RunAsync(session, userText, ctx.RequestAborted);

        var response = new OpenAiChatCompletionResponse
        {
            Id = completionId,
            Created = created,
            Model = model,
            Choices =
            [
                new OpenAiChoice
                {
                    Index = 0,
                    Message = new OpenAiResponseMessage { Role = "assistant", Content = result },
                    FinishReason = "stop"
                }
            ],
            Usage = new OpenAiUsage
            {
                PromptTokens = (int)session.TotalInputTokens,
                CompletionTokens = (int)session.TotalOutputTokens,
                TotalTokens = (int)(session.TotalInputTokens + session.TotalOutputTokens)
            }
        };

        ctx.Response.ContentType = "application/json";
        await ctx.Response.WriteAsync(
            JsonSerializer.Serialize(response, CoreJsonContext.Default.OpenAiChatCompletionResponse),
            ctx.RequestAborted);
    }
});

// POST /v1/responses — OpenAI Responses API compatibility
app.MapPost("/v1/responses", async (HttpContext ctx) =>
{
    if (isNonLoopbackBind)
    {
        var token = GatewaySecurity.GetToken(ctx, config.Security.AllowQueryStringToken);
        if (!GatewaySecurity.IsTokenValid(token, config.AuthToken!))
        {
            ctx.Response.StatusCode = 401;
            return;
        }
    }

    OpenAiResponseRequest? req;
    try
    {
        req = await JsonSerializer.DeserializeAsync(ctx.Request.Body,
            CoreJsonContext.Default.OpenAiResponseRequest, ctx.RequestAborted);
    }
    catch
    {
        ctx.Response.StatusCode = 400;
        await ctx.Response.WriteAsync("Invalid JSON request body.", ctx.RequestAborted);
        return;
    }

    if (req is null || string.IsNullOrWhiteSpace(req.Input))
    {
        ctx.Response.StatusCode = 400;
        await ctx.Response.WriteAsync("Request must include an 'input' field.", ctx.RequestAborted);
        return;
    }

    var requestId = $"oai-resp:{Guid.NewGuid():N}";
    var session = await sessionManager.GetOrCreateAsync("openai-responses", requestId, ctx.RequestAborted);
    if (req.Model is not null)
        session.ModelOverride = req.Model;

    var result = await agentRuntime.RunAsync(session, req.Input, ctx.RequestAborted);

    var responseId = $"resp-{Guid.NewGuid():N}"[..24];
    var msgId = $"msg-{Guid.NewGuid():N}"[..23];

    var response = new OpenAiResponseResponse
    {
        Id = responseId,
        Status = "completed",
        Output =
        [
            new OpenAiResponseOutput
            {
                Id = msgId,
                Role = "assistant",
                Content = [new OpenAiResponseContent { Text = result }]
            }
        ],
        Usage = new OpenAiUsage
        {
            PromptTokens = (int)session.TotalInputTokens,
            CompletionTokens = (int)session.TotalOutputTokens,
            TotalTokens = (int)(session.TotalInputTokens + session.TotalOutputTokens)
        }
    };

    ctx.Response.ContentType = "application/json";
    await ctx.Response.WriteAsync(
        JsonSerializer.Serialize(response, CoreJsonContext.Default.OpenAiResponseResponse),
        ctx.RequestAborted);
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

if (config.Plugins.Native.HomeAssistant.Enabled && config.Plugins.Native.HomeAssistant.Events.Enabled)
{
    var haLogger = app.Services.GetRequiredService<ILogger<HomeAssistantEventBridge>>();
    var haBridge = new HomeAssistantEventBridge(config.Plugins.Native.HomeAssistant, haLogger, pipeline.InboundWriter);
    _ = haBridge.StartAsync(app.Lifetime.ApplicationStopping);
}

if (config.Plugins.Native.Mqtt.Enabled && config.Plugins.Native.Mqtt.Events.Enabled)
{
    var mqttLogger = app.Services.GetRequiredService<ILogger<MqttEventBridge>>();
    var mqttBridge = new MqttEventBridge(config.Plugins.Native.Mqtt, mqttLogger, pipeline.InboundWriter);
    _ = mqttBridge.StartAsync(app.Lifetime.ApplicationStopping);
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
    toolApprovalService,
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
        // Embedded fallback when webchat.html is not packaged
        ctx.Response.ContentType = "text/html";
        await ctx.Response.WriteAsync("""
            <!DOCTYPE html>
            <html lang="en"><head><meta charset="utf-8"><title>OpenClaw.NET</title>
            <style>body{font-family:system-ui,sans-serif;display:flex;align-items:center;justify-content:center;height:100vh;margin:0;background:#0f172a;color:#e2e8f0;}
            .card{text-align:center;max-width:420px;padding:2rem;border:1px solid #334155;border-radius:12px;background:#1e293b;}
            code{background:#334155;padding:2px 6px;border-radius:4px;font-size:0.9em;}
            a{color:#38bdf8;}</style></head>
            <body><div class="card">
            <h1>&#128062; OpenClaw.NET Gateway</h1>
            <p>The WebChat UI is not bundled. Connect via WebSocket at <code>ws://HOST:PORT/ws</code> or use the <a href="https://github.com/openclaw/openclaw.net">Companion app</a>.</p>
            </div></body></html>
            """);
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

static ChannelAllowlistFile GetConfigAllowlist(GatewayConfig config, string channelId)
{
    return channelId switch
    {
        "telegram" => new ChannelAllowlistFile { AllowedFrom = config.Channels.Telegram.AllowedFromUserIds },
        "whatsapp" => new ChannelAllowlistFile { AllowedFrom = config.Channels.WhatsApp.AllowedFromIds },
        "sms" => new ChannelAllowlistFile
        {
            AllowedFrom = config.Channels.Sms.Twilio.AllowedFromNumbers,
            AllowedTo = config.Channels.Sms.Twilio.AllowedToNumbers
        },
        _ => new ChannelAllowlistFile()
    };
}

app.MapGet("/allowlists/{channelId}", (HttpContext ctx, string channelId) =>
{
    if (isNonLoopbackBind)
    {
        var token = GatewaySecurity.GetToken(ctx, config.Security.AllowQueryStringToken);
        if (!GatewaySecurity.IsTokenValid(token, config.AuthToken!))
            return Results.Unauthorized();
    }

    var cfg = GetConfigAllowlist(config, channelId);
    var dyn = allowlists.TryGetDynamic(channelId);
    var effective = allowlists.GetEffective(channelId, cfg);
    return Results.Ok(new
    {
        channelId,
        semantics = allowlistSemantics.ToString().ToLowerInvariant(),
        config = cfg,
        dynamic = dyn,
        effective
    });
});

app.MapPost("/allowlists/{channelId}/add_latest", (HttpContext ctx, string channelId) =>
{
    if (isNonLoopbackBind)
    {
        var token = GatewaySecurity.GetToken(ctx, config.Security.AllowQueryStringToken);
        if (!GatewaySecurity.IsTokenValid(token, config.AuthToken!))
            return Results.Unauthorized();
    }

    var latest = recentSenders.TryGetLatest(channelId);
    if (latest is null)
        return Results.NotFound(new { success = false, error = "No recent sender found for that channel." });

    allowlists.AddAllowedFrom(channelId, latest.SenderId);
    return Results.Ok(new { success = true, senderId = latest.SenderId });
});

app.MapPost("/allowlists/{channelId}/tighten", (HttpContext ctx, string channelId) =>
{
    if (isNonLoopbackBind)
    {
        var token = GatewaySecurity.GetToken(ctx, config.Security.AllowQueryStringToken);
        if (!GatewaySecurity.IsTokenValid(token, config.AuthToken!))
            return Results.Unauthorized();
    }

    var paired = pairingManager.GetApprovedList()
        .Select(s =>
        {
            var idx = s.IndexOf(':', StringComparison.Ordinal);
            if (idx <= 0 || idx + 1 >= s.Length) return (Channel: "", Sender: "");
            return (Channel: s[..idx], Sender: s[(idx + 1)..]);
        })
        .Where(t => string.Equals(t.Channel, channelId, StringComparison.Ordinal) && !string.IsNullOrWhiteSpace(t.Sender))
        .Select(t => t.Sender)
        .Distinct(StringComparer.Ordinal)
        .ToArray();

    if (paired.Length == 0)
        return Results.BadRequest(new { success = false, error = "No paired senders found for that channel." });

    allowlists.SetAllowedFrom(channelId, paired);
    return Results.Ok(new { success = true, count = paired.Length });
});

static string ResolveWorkspaceRoot(string? value)
{
    if (string.IsNullOrWhiteSpace(value))
        return "";

    if (value.StartsWith("env:", StringComparison.OrdinalIgnoreCase))
    {
        var env = value[4..];
        return Environment.GetEnvironmentVariable(env) ?? "";
    }

    return value;
}

static string ToBoolEmoji(bool value) => value ? "yes" : "no";

app.MapGet("/doctor", (HttpContext ctx) =>
{
    if (isNonLoopbackBind)
    {
        var token = GatewaySecurity.GetToken(ctx, config.Security.AllowQueryStringToken);
        if (!GatewaySecurity.IsTokenValid(token, config.AuthToken!))
            return Results.Unauthorized();
    }

    var wsRoot = ResolveWorkspaceRoot(config.Tooling.WorkspaceRoot);
    var wsExists = !string.IsNullOrWhiteSpace(wsRoot) && Directory.Exists(wsRoot);

    var report = new
    {
        nowUtc = DateTimeOffset.UtcNow,
        bind = new
        {
            config.BindAddress,
            config.Port,
            isNonLoopbackBind,
            authEnabled = isNonLoopbackBind,
            authTokenConfigured = !string.IsNullOrWhiteSpace(config.AuthToken)
        },
        tooling = new
        {
            autonomyMode = (config.Tooling.AutonomyMode ?? "full").Trim().ToLowerInvariant(),
            workspaceOnly = config.Tooling.WorkspaceOnly,
            workspaceRoot = wsRoot,
            workspaceRootExists = wsExists,
            forbiddenPathGlobs = config.Tooling.ForbiddenPathGlobs,
            allowedShellCommandGlobs = config.Tooling.AllowedShellCommandGlobs,
            effectiveRequireToolApproval,
            effectiveApprovalRequiredTools,
            toolApprovalTimeoutSeconds = config.Tooling.ToolApprovalTimeoutSeconds
        },
        channels = new
        {
            allowlistSemantics = config.Channels.AllowlistSemantics,
            sms = new { enabled = config.Channels.Sms.Twilio.Enabled, dmPolicy = config.Channels.Sms.DmPolicy },
            telegram = new { enabled = config.Channels.Telegram.Enabled, dmPolicy = config.Channels.Telegram.DmPolicy },
            whatsapp = new { enabled = config.Channels.WhatsApp.Enabled, dmPolicy = config.Channels.WhatsApp.DmPolicy }
        },
        allowlists = new
        {
            sms = new { dynamic = allowlists.TryGetDynamic("sms"), effective = allowlists.GetEffective("sms", GetConfigAllowlist(config, "sms")) },
            telegram = new { dynamic = allowlists.TryGetDynamic("telegram"), effective = allowlists.GetEffective("telegram", GetConfigAllowlist(config, "telegram")) },
            whatsapp = new { dynamic = allowlists.TryGetDynamic("whatsapp"), effective = allowlists.GetEffective("whatsapp", GetConfigAllowlist(config, "whatsapp")) }
        },
        recentSenders = new
        {
            sms = recentSenders.GetSnapshot("sms").Senders.Take(10).ToArray(),
            telegram = recentSenders.GetSnapshot("telegram").Senders.Take(10).ToArray(),
            whatsapp = recentSenders.GetSnapshot("whatsapp").Senders.Take(10).ToArray()
        },
        pairing = new
        {
            approved = pairingManager.GetApprovedList().ToArray()
        },
        memory = new
        {
            provider = config.Memory.Provider,
            storagePath = config.Memory.StoragePath,
            sqlite = new { config.Memory.Sqlite.DbPath, config.Memory.Sqlite.EnableFts, config.Memory.Sqlite.EnableVectors },
            recall = new { config.Memory.Recall.Enabled, config.Memory.Recall.MaxNotes, config.Memory.Recall.MaxChars }
        },
        cron = new
        {
            enabled = config.Cron.Enabled,
            jobs = config.Cron.Jobs.Select(j => new { j.Name, j.CronExpression, j.ChannelId, j.SessionId, j.RunOnStartup }).ToArray()
        },
        runtime = new
        {
            circuitBreaker = agentRuntime.CircuitBreakerState.ToString(),
            activeSessions = sessionManager.ActiveCount
        },
        skills = new
        {
            count = skills.Count,
            names = skills.Select(s => s.Name).ToArray()
        }
    };

    return Results.Ok(report);
});

app.MapGet("/doctor/text", (HttpContext ctx) =>
{
    if (isNonLoopbackBind)
    {
        var token = GatewaySecurity.GetToken(ctx, config.Security.AllowQueryStringToken);
        if (!GatewaySecurity.IsTokenValid(token, config.AuthToken!))
            return Results.Unauthorized();
    }

    var wsRoot = ResolveWorkspaceRoot(config.Tooling.WorkspaceRoot);
    var wsExists = !string.IsNullOrWhiteSpace(wsRoot) && Directory.Exists(wsRoot);

    var sb = new StringBuilder();
    sb.AppendLine("OpenClaw.NET Doctor");
    sb.AppendLine($"- time_utc: {DateTimeOffset.UtcNow:O}");
    sb.AppendLine($"- bind: {config.BindAddress}:{config.Port} non_loopback={ToBoolEmoji(isNonLoopbackBind)} auth_token_set={ToBoolEmoji(!string.IsNullOrWhiteSpace(config.AuthToken))}");
    sb.AppendLine();

    var autonomyMode = (config.Tooling.AutonomyMode ?? "full").Trim().ToLowerInvariant();
    sb.AppendLine("Tooling");
    sb.AppendLine($"- autonomy_mode: {autonomyMode}");
    sb.AppendLine($"- workspace_only: {ToBoolEmoji(config.Tooling.WorkspaceOnly)}");
    sb.AppendLine($"- workspace_root: {wsRoot} exists={ToBoolEmoji(wsExists)}");
    sb.AppendLine($"- approvals_required_effective: {ToBoolEmoji(effectiveRequireToolApproval)}");
    sb.AppendLine($"- approval_timeout_seconds: {config.Tooling.ToolApprovalTimeoutSeconds}");
    sb.AppendLine();

    sb.AppendLine("Allowlists");
    sb.AppendLine($"- semantics: {config.Channels.AllowlistSemantics}");
    foreach (var ch in new[] { "telegram", "sms", "whatsapp" })
    {
        var dyn = allowlists.TryGetDynamic(ch);
        var eff = allowlists.GetEffective(ch, GetConfigAllowlist(config, ch));
        sb.AppendLine($"- {ch}: dynamic_file={ToBoolEmoji(dyn is not null)} allowed_from={eff.AllowedFrom.Length} allowed_to={eff.AllowedTo.Length}");
        var latest = recentSenders.TryGetLatest(ch);
        if (latest is not null)
            sb.AppendLine($"  latest_sender: {latest.SenderId} last_seen_utc={latest.LastSeenUtc:O}");
    }
    sb.AppendLine();

    sb.AppendLine("Pairing");
    var approved = pairingManager.GetApprovedList().ToArray();
    sb.AppendLine($"- approved_pairs: {approved.Length}");
    if (approved.Length > 0)
        sb.AppendLine($"- approved: {string.Join(", ", approved.Take(20))}{(approved.Length > 20 ? ", …" : "")}");
    sb.AppendLine();

    sb.AppendLine("Memory");
    sb.AppendLine($"- provider: {config.Memory.Provider}");
    sb.AppendLine($"- sqlite_fts: {ToBoolEmoji(config.Memory.Sqlite.EnableFts)}");
    sb.AppendLine($"- recall_enabled: {ToBoolEmoji(config.Memory.Recall.Enabled)} max_notes={config.Memory.Recall.MaxNotes} max_chars={config.Memory.Recall.MaxChars}");
    sb.AppendLine();

    sb.AppendLine("Cron");
    sb.AppendLine($"- enabled: {ToBoolEmoji(config.Cron.Enabled)} jobs={config.Cron.Jobs.Count}");
    foreach (var job in config.Cron.Jobs.Take(20))
        sb.AppendLine($"  - {job.Name} cron={job.CronExpression} run_on_startup={ToBoolEmoji(job.RunOnStartup)} session={job.SessionId}");
    if (config.Cron.Jobs.Count > 20)
        sb.AppendLine("  - …");
    sb.AppendLine();

    sb.AppendLine("Skills");
    sb.AppendLine($"- loaded: {skills.Count}");
    if (skills.Count > 0)
        sb.AppendLine($"- names: {string.Join(", ", skills.Select(s => s.Name))}");
    sb.AppendLine();

    sb.AppendLine("Suggested next steps");
    if (config.Channels.AllowlistSemantics.Equals("strict", StringComparison.OrdinalIgnoreCase))
        sb.AppendLine("- If a sender is blocked, run: POST /allowlists/{channel}/add_latest then retry.");
    else
        sb.AppendLine("- Consider setting OpenClaw:Channels:AllowlistSemantics=strict for safer defaults.");
    if (autonomyMode == "supervised")
        sb.AppendLine("- Approvals: when prompted, reply with `/approve <approvalId> yes` (or use POST /tools/approve).");
    if (config.Memory.Provider.Equals("file", StringComparison.OrdinalIgnoreCase))
        sb.AppendLine("- Consider Memory.Provider=sqlite and Memory.Sqlite.EnableFts=true for faster recall.");

    return Results.Text(sb.ToString(), "text/plain; charset=utf-8");
});

app.MapPost("/tools/approve", (HttpContext ctx, string approvalId, bool approved) =>
{
    if (isNonLoopbackBind)
    {
        var token = GatewaySecurity.GetToken(ctx, config.Security.AllowQueryStringToken);
        if (!GatewaySecurity.IsTokenValid(token, config.AuthToken!))
            return Results.Unauthorized();
    }

    if (string.IsNullOrWhiteSpace(approvalId))
        return Results.BadRequest(new { success = false, error = "approvalId is required." });

    var ok = toolApprovalService.TrySetDecision(approvalId, approved);
    return ok
        ? Results.Ok(new { success = true })
        : Results.NotFound(new { success = false, error = "No pending approval found for that id." });
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
    await recentSenders.RecordAsync(msg.ChannelId, msg.SenderId, msg.SenderName, ct);
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
            message.TryGetProperty("chat", out var chat) &&
            chat.TryGetProperty("id", out var chatId))
        {
            var senderIdStr = chatId.GetRawText();

            await recentSenders.RecordAsync("telegram", senderIdStr, senderName: null, ctx.RequestAborted);

            var effective = allowlists.GetEffective("telegram", new ChannelAllowlistFile
            {
                AllowedFrom = config.Channels.Telegram.AllowedFromUserIds
            });
            
            if (!AllowlistPolicy.IsAllowed(effective.AllowedFrom, senderIdStr, allowlistSemantics))
            {
                ctx.Response.StatusCode = 403;
                return;
            }

            string? text = null;
            if (message.TryGetProperty("text", out var textNode))
                text = textNode.GetString();

            string? marker = null;
            if (message.TryGetProperty("photo", out var photoNode) && photoNode.ValueKind == JsonValueKind.Array)
            {
                string? fileId = null;
                foreach (var p in photoNode.EnumerateArray())
                {
                    if (p.TryGetProperty("file_id", out var idNode))
                        fileId = idNode.GetString();
                }

                if (!string.IsNullOrWhiteSpace(fileId))
                    marker = $"[IMAGE:telegram:file_id={fileId}]";
            }

            if (!string.IsNullOrWhiteSpace(marker))
            {
                var caption = message.TryGetProperty("caption", out var capNode) ? capNode.GetString() : null;
                text = string.IsNullOrWhiteSpace(caption) ? marker : marker + "\n" + caption;
            }

            if (!string.IsNullOrWhiteSpace(text) && text.Length > config.Channels.Telegram.MaxInboundChars)
                text = text[..config.Channels.Telegram.MaxInboundChars];

            if (string.IsNullOrWhiteSpace(text))
            {
                ctx.Response.StatusCode = 200;
                await ctx.Response.WriteAsync("OK");
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

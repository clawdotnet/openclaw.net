using OpenClaw.Core.Security;
using OpenClaw.Core.Models;
using OpenClaw.Core.Setup;
using OpenClaw.Core.Plugins;
using OpenClaw.Core.ExternalCli;
using System.Text.RegularExpressions;

namespace OpenClaw.Core.Validation;

/// <summary>
/// Validates <see cref="Models.GatewayConfig"/> at startup and returns any errors.
/// Fail-fast: the gateway should refuse to start with invalid configuration.
/// </summary>
public static class ConfigValidator
{
    private static readonly HashSet<string> BuiltInLlmProviders = new(StringComparer.OrdinalIgnoreCase)
    {
        "openai",
        "anthropic",
        "claude",
        "gemini",
        "google",
        "ollama",
        "azure-openai",
        "openai-compatible",
        "aperture",
        "anthropic-vertex",
        "amazon-bedrock",
        "groq",
        "together",
        "lmstudio",
        "embedded"
    };

    public static IReadOnlyList<string> Validate(Models.GatewayConfig config)
    {
        var errors = new List<string>();

        // Port
        if (config.Port is < 1 or > 65535)
            errors.Add($"Port must be between 1 and 65535 (got {config.Port}).");

        // LLM
        if (string.IsNullOrWhiteSpace(config.Llm.Model))
            errors.Add("Llm.Model must be set.");
        var pluginBackedProvidersPossible =
            config.Plugins.Enabled || config.Plugins.DynamicNative.Enabled || config.Plugins.Mcp.Enabled;
        if (!pluginBackedProvidersPossible && !BuiltInLlmProviders.Contains(config.Llm.Provider))
            errors.Add($"Llm.Provider '{config.Llm.Provider}' is not a supported built-in provider.");
        if (config.Llm.MaxTokens < 1)
            errors.Add($"Llm.MaxTokens must be >= 1 (got {config.Llm.MaxTokens}).");
        if (config.Llm.Temperature is < 0 or > 2)
            errors.Add($"Llm.Temperature must be between 0 and 2 (got {config.Llm.Temperature}).");
        if (config.Llm.TimeoutSeconds < 0)
            errors.Add($"Llm.TimeoutSeconds must be >= 0 (got {config.Llm.TimeoutSeconds}).");
        if (config.Llm.RetryCount < 0)
            errors.Add($"Llm.RetryCount must be >= 0 (got {config.Llm.RetryCount}).");
        if (config.LocalInference.Port is < 0 or > 65535)
            errors.Add($"LocalInference.Port must be between 0 and 65535 (got {config.LocalInference.Port}).");
        if (config.LocalInference.ContextSize < 0)
            errors.Add($"LocalInference.ContextSize must be >= 0 (got {config.LocalInference.ContextSize}).");
        if (config.LocalInference.StartupTimeoutSeconds < 1)
            errors.Add($"LocalInference.StartupTimeoutSeconds must be >= 1 (got {config.LocalInference.StartupTimeoutSeconds}).");
        if (config.LocalInference.ReasoningBudget < -1)
            errors.Add($"LocalInference.ReasoningBudget must be >= -1 (got {config.LocalInference.ReasoningBudget}).");
        if (config.Llm.CircuitBreakerThreshold < 1)
            errors.Add($"Llm.CircuitBreakerThreshold must be >= 1 (got {config.Llm.CircuitBreakerThreshold}).");
        if (config.Llm.CircuitBreakerCooldownSeconds < 1)
            errors.Add($"Llm.CircuitBreakerCooldownSeconds must be >= 1 (got {config.Llm.CircuitBreakerCooldownSeconds}).");
        if (!IsValidProviderAuthMode(config.Llm.AuthMode))
            errors.Add("Llm.AuthMode must be 'bearer' or 'tailnet-identity'.");
        else if (IsTailnetIdentityAuth(config.Llm.AuthMode) && !SupportsTailnetIdentity(config.Llm.Provider))
            errors.Add($"Llm.AuthMode 'tailnet-identity' is not supported for provider '{config.Llm.Provider}'.");
        ValidateApertureProviderConfig("Llm", "Endpoint", config.Llm.Provider, config.Llm.Endpoint, config.Llm.ApiKey, config.Llm.AuthMode, errors);
        ValidatePromptCaching("Llm.PromptCaching", config.Llm.Provider, config.Llm.PromptCaching, errors, isDynamicProvider: false);
        ValidateModelProfiles(config, errors, pluginBackedProvidersPossible);

        // Memory
        if (!string.Equals(config.Memory.Provider, "file", StringComparison.OrdinalIgnoreCase) &&
            !string.Equals(config.Memory.Provider, "sqlite", StringComparison.OrdinalIgnoreCase) &&
            !string.Equals(config.Memory.Provider, "mempalace", StringComparison.OrdinalIgnoreCase))
        {
            errors.Add($"Memory.Provider '{config.Memory.Provider}' must be 'file', 'sqlite', or 'mempalace'.");
        }
        if (string.IsNullOrWhiteSpace(config.Memory.StoragePath))
            errors.Add("Memory.StoragePath must be set.");
        if (config.Memory.MaxHistoryTurns < 1)
            errors.Add($"Memory.MaxHistoryTurns must be >= 1 (got {config.Memory.MaxHistoryTurns}).");
        if (config.Memory.EnableCompaction)
        {
            if (config.Memory.CompactionThreshold < 4)
                errors.Add($"Memory.CompactionThreshold must be >= 4 (got {config.Memory.CompactionThreshold}).");
            if (config.Memory.CompactionKeepRecent < 2)
                errors.Add($"Memory.CompactionKeepRecent must be >= 2 (got {config.Memory.CompactionKeepRecent}).");
            if (config.Memory.CompactionKeepRecent >= config.Memory.CompactionThreshold)
                errors.Add("Memory.CompactionKeepRecent must be less than CompactionThreshold.");
            if (config.Memory.CompactionThreshold <= config.Memory.MaxHistoryTurns)
                errors.Add("Memory.CompactionThreshold must be greater than MaxHistoryTurns when EnableCompaction=true.");
        }
        ValidateFractalMemory(config.Memory.Fractal, errors);

        if (config.Memory.Retention.SweepIntervalMinutes < 5)
            errors.Add($"Memory.Retention.SweepIntervalMinutes must be >= 5 (got {config.Memory.Retention.SweepIntervalMinutes}).");
        if (config.Memory.Retention.SessionTtlDays < 1)
            errors.Add($"Memory.Retention.SessionTtlDays must be >= 1 (got {config.Memory.Retention.SessionTtlDays}).");
        if (config.Memory.Retention.BranchTtlDays < 1)
            errors.Add($"Memory.Retention.BranchTtlDays must be >= 1 (got {config.Memory.Retention.BranchTtlDays}).");
        if (config.Memory.Retention.ArchiveRetentionDays < 1)
            errors.Add($"Memory.Retention.ArchiveRetentionDays must be >= 1 (got {config.Memory.Retention.ArchiveRetentionDays}).");
        if (config.Memory.Retention.MaxItemsPerSweep < 10)
            errors.Add($"Memory.Retention.MaxItemsPerSweep must be >= 10 (got {config.Memory.Retention.MaxItemsPerSweep}).");

        // Sessions
        if (config.MaxConcurrentSessions < 1)
            errors.Add($"MaxConcurrentSessions must be >= 1 (got {config.MaxConcurrentSessions}).");
        if (config.SessionTimeoutMinutes < 1)
            errors.Add($"SessionTimeoutMinutes must be >= 1 (got {config.SessionTimeoutMinutes}).");

        // WebSocket
        if (config.WebSocket.MaxMessageBytes < 256)
            errors.Add($"WebSocket.MaxMessageBytes must be >= 256 (got {config.WebSocket.MaxMessageBytes}).");
        if (config.WebSocket.MaxConnections < 1)
            errors.Add($"WebSocket.MaxConnections must be >= 1 (got {config.WebSocket.MaxConnections}).");
        if (config.WebSocket.MaxConnectionsPerIp < 1)
            errors.Add($"WebSocket.MaxConnectionsPerIp must be >= 1 (got {config.WebSocket.MaxConnectionsPerIp}).");

        // Tooling
        if (config.Tooling.ToolTimeoutSeconds < 0)
            errors.Add($"Tooling.ToolTimeoutSeconds must be >= 0 (got {config.Tooling.ToolTimeoutSeconds}).");
        ValidateExternalCli(config.ExternalCli, errors);

        ValidateUrlSafety("Tooling.UrlSafety", config.Tooling.UrlSafety, errors);
        if (config.Plugins.Native.WebFetch.UrlSafety is not null)
            ValidateUrlSafety("Plugins.Native.WebFetch.UrlSafety", config.Plugins.Native.WebFetch.UrlSafety, errors);

        if (config.Tooling.WorkspaceOnly)
        {
            var resolvedWorkspaceRoot = ResolveConfiguredPath(config.Tooling.WorkspaceRoot);
            if (string.IsNullOrWhiteSpace(resolvedWorkspaceRoot))
            {
                errors.Add("Tooling.WorkspaceRoot must resolve to a non-empty absolute path when WorkspaceOnly=true.");
            }
            else if (!Path.IsPathRooted(resolvedWorkspaceRoot))
            {
                errors.Add("Tooling.WorkspaceRoot must resolve to an absolute path when WorkspaceOnly=true.");
            }
        }

        ValidateRootSet("Tooling.AllowedReadRoots", config.Tooling.AllowedReadRoots, errors);
        ValidateRootSet("Tooling.AllowedWriteRoots", config.Tooling.AllowedWriteRoots, errors);

        // Sandbox
        var sandboxProvider = SandboxProviderNames.Normalize(config.Sandbox.Provider);
        if (!sandboxProvider.Equals(SandboxProviderNames.None, StringComparison.OrdinalIgnoreCase) &&
            !sandboxProvider.Equals(SandboxProviderNames.OpenSandbox, StringComparison.OrdinalIgnoreCase))
        {
            errors.Add("Sandbox.Provider must be 'None' or 'OpenSandbox'.");
        }

        if (config.Sandbox.DefaultTTL < 1)
            errors.Add($"Sandbox.DefaultTTL must be >= 1 (got {config.Sandbox.DefaultTTL}).");

        if (sandboxProvider.Equals(SandboxProviderNames.OpenSandbox, StringComparison.OrdinalIgnoreCase) &&
            string.IsNullOrWhiteSpace(config.Sandbox.Endpoint))
        {
            errors.Add("Sandbox.Endpoint must be set when Sandbox.Provider='OpenSandbox'.");
        }

        foreach (var (toolName, toolConfig) in config.Sandbox.Tools)
        {
            if (!string.IsNullOrWhiteSpace(toolConfig.Mode) &&
                !ToolSandboxPolicy.TryParseMode(toolConfig.Mode, out _))
            {
                errors.Add($"Sandbox.Tools.{toolName}.Mode must be 'None', 'Prefer', or 'Require'.");
            }

            if (toolConfig.TTL is <= 0)
                errors.Add($"Sandbox.Tools.{toolName}.TTL must be >= 1 when set (got {toolConfig.TTL}).");

            if (sandboxProvider.Equals(SandboxProviderNames.OpenSandbox, StringComparison.OrdinalIgnoreCase) &&
                ToolSandboxPolicy.ResolveMode(config, toolName, ToolSandboxMode.None) is not ToolSandboxMode.None &&
                string.IsNullOrWhiteSpace(toolConfig.Template))
            {
                errors.Add($"Sandbox.Tools.{toolName}.Template must be set when sandboxing is enabled for that tool.");
            }
        }

        if (sandboxProvider.Equals(SandboxProviderNames.OpenSandbox, StringComparison.OrdinalIgnoreCase))
        {
            foreach (var (toolName, defaultMode) in ToolSandboxPolicy.EnumerateBuiltInCandidates(config))
            {
                if (ToolSandboxPolicy.ResolveMode(config, toolName, defaultMode) is not ToolSandboxMode.None &&
                    string.IsNullOrWhiteSpace(ToolSandboxPolicy.ResolveTemplate(config, toolName)))
                {
                    errors.Add($"Sandbox.Tools.{toolName}.Template must be set because {toolName} defaults to sandbox mode '{ToolSandboxPolicy.ResolveMode(config, toolName, defaultMode)}'.");
                }
            }
        }

        ValidateCodingBackends(config.CodingBackends, errors);

        // Delegation
        if (config.Delegation.Enabled)
        {
            if (config.Delegation.MaxDepth < 1)
                errors.Add($"Delegation.MaxDepth must be >= 1 (got {config.Delegation.MaxDepth}).");
            if (config.Delegation.Profiles.Count == 0)
                errors.Add("Delegation is enabled but no profiles are configured.");
            foreach (var (name, profile) in config.Delegation.Profiles)
            {
                if (string.IsNullOrWhiteSpace(profile.Name))
                    errors.Add($"Delegation profile '{name}' has no Name.");
                if (profile.MaxIterations < 1)
                    errors.Add($"Delegation profile '{name}' has MaxIterations < 1.");
            }
        }

        ValidateWorkflows(config.Workflows, errors);

        // Middleware
        if (config.SessionTokenBudget < 0)
            errors.Add($"SessionTokenBudget must be >= 0 (got {config.SessionTokenBudget}).");
        if (config.SessionRateLimitPerMinute < 0)
            errors.Add($"SessionRateLimitPerMinute must be >= 0 (got {config.SessionRateLimitPerMinute}).");

        // Plugin bridge transport
        var transportMode = (config.Plugins.Transport.Mode ?? "stdio").Trim();
        if (!transportMode.Equals("stdio", StringComparison.OrdinalIgnoreCase) &&
            !transportMode.Equals("socket", StringComparison.OrdinalIgnoreCase) &&
            !transportMode.Equals("hybrid", StringComparison.OrdinalIgnoreCase))
        {
            errors.Add("Plugins.Transport.Mode must be 'stdio', 'socket', or 'hybrid'.");
        }

        var runtimeMode = RuntimeModeResolver.Normalize(config.Runtime.Mode);
        if (runtimeMode is not ("auto" or "aot" or "jit"))
            errors.Add("Runtime.Mode must be 'auto', 'aot', or 'jit'.");

        var runtimeOrchestrator = RuntimeOrchestrator.Normalize(config.Runtime.Orchestrator);
        if (runtimeOrchestrator is not (RuntimeOrchestrator.Native or RuntimeOrchestrator.Maf))
            errors.Add("Runtime.Orchestrator must be 'native' or 'maf'.");

        ValidateNotionConfig(config.Plugins.Native.Notion, errors);
        // MCP plugin servers
        if (config.Plugins.Mcp.Enabled)
        {
            if (config.Plugins.Mcp.Servers is null)
            {
                errors.Add("Plugins.Mcp.Servers must be provided when MCP is enabled.");
            }
            else
            {
                foreach (var (serverId, server) in config.Plugins.Mcp.Servers)
                {
                    if (!server.Enabled)
                        continue;

                    var transport = server.NormalizeTransport();
                    if (transport is not ("stdio" or "http"))
                    {
                        errors.Add($"Plugins.Mcp.Servers.{serverId}.Transport must be 'stdio' or 'http'.");
                        continue;
                    }

                    if (server.StartupTimeoutSeconds < 1)
                        errors.Add($"Plugins.Mcp.Servers.{serverId}.StartupTimeoutSeconds must be >= 1 (got {server.StartupTimeoutSeconds}).");
                    if (server.RequestTimeoutSeconds < 1)
                        errors.Add($"Plugins.Mcp.Servers.{serverId}.RequestTimeoutSeconds must be >= 1 (got {server.RequestTimeoutSeconds}).");

                    if (transport == "stdio")
                    {
                        if (string.IsNullOrWhiteSpace(server.Command))
                            errors.Add($"Plugins.Mcp.Servers.{serverId}.Command must be set when Transport='stdio'.");
                    }
                    else if (!Uri.TryCreate(server.Url, UriKind.Absolute, out var url) ||
                             (url.Scheme != Uri.UriSchemeHttp && url.Scheme != Uri.UriSchemeHttps))
                    {
                        errors.Add($"Plugins.Mcp.Servers.{serverId}.Url must be an absolute http(s) URL when Transport='http'.");
                    }
                }
            }
        }

        // Channels
        if (config.Channels.Sms.Twilio.MaxInboundChars < 1)
            errors.Add($"Channels.Sms.Twilio.MaxInboundChars must be >= 1 (got {config.Channels.Sms.Twilio.MaxInboundChars}).");
        if (config.Channels.Sms.Twilio.MaxRequestBytes < 1024)
            errors.Add($"Channels.Sms.Twilio.MaxRequestBytes must be >= 1024 (got {config.Channels.Sms.Twilio.MaxRequestBytes}).");

        if (config.Channels.Telegram.MaxInboundChars < 1)
            errors.Add($"Channels.Telegram.MaxInboundChars must be >= 1 (got {config.Channels.Telegram.MaxInboundChars}).");
        if (config.Channels.Telegram.MaxRequestBytes < 1024)
            errors.Add($"Channels.Telegram.MaxRequestBytes must be >= 1024 (got {config.Channels.Telegram.MaxRequestBytes}).");

        if (config.Channels.WhatsApp.MaxInboundChars < 1)
            errors.Add($"Channels.WhatsApp.MaxInboundChars must be >= 1 (got {config.Channels.WhatsApp.MaxInboundChars}).");
        if (config.Channels.WhatsApp.MaxRequestBytes < 1024)
            errors.Add($"Channels.WhatsApp.MaxRequestBytes must be >= 1024 (got {config.Channels.WhatsApp.MaxRequestBytes}).");
        if (config.Channels.WhatsApp.Type is not ("official" or "bridge" or "first_party_worker"))
            errors.Add("Channels.WhatsApp.Type must be 'official', 'bridge', or 'first_party_worker'.");
        if (config.Channels.WhatsApp.ValidateSignature)
        {
            var appSecret = SecretResolver.Resolve(config.Channels.WhatsApp.WebhookAppSecretRef)
                ?? config.Channels.WhatsApp.WebhookAppSecret;
            if (string.IsNullOrWhiteSpace(appSecret))
                errors.Add("Channels.WhatsApp.ValidateSignature is true but WebhookAppSecret/WebhookAppSecretRef is not configured.");
        }
        if (string.Equals(config.Channels.WhatsApp.Type, "first_party_worker", StringComparison.OrdinalIgnoreCase))
        {
            var worker = config.Channels.WhatsApp.FirstPartyWorker;
            if (worker.Driver is not ("baileys" or "baileys_csharp" or "whatsmeow" or "simulated"))
                errors.Add("Channels.WhatsApp.FirstPartyWorker.Driver must be 'baileys', 'baileys_csharp', 'whatsmeow', or 'simulated'.");
            if (worker.Accounts.Count == 0)
                errors.Add("Channels.WhatsApp.FirstPartyWorker.Accounts must contain at least one account.");

            foreach (var account in worker.Accounts)
            {
                if (string.IsNullOrWhiteSpace(account.AccountId))
                    errors.Add("Channels.WhatsApp.FirstPartyWorker.Accounts[].AccountId must be set.");
                if (string.IsNullOrWhiteSpace(account.SessionPath))
                    errors.Add($"Channels.WhatsApp.FirstPartyWorker account '{account.AccountId}' must set SessionPath.");
                if (account.PairingMode is not ("qr" or "pairing_code"))
                    errors.Add($"Channels.WhatsApp.FirstPartyWorker account '{account.AccountId}' PairingMode must be 'qr' or 'pairing_code'.");
                if (string.Equals(account.PairingMode, "pairing_code", StringComparison.OrdinalIgnoreCase) &&
                    string.IsNullOrWhiteSpace(account.PhoneNumber))
                {
                    errors.Add($"Channels.WhatsApp.FirstPartyWorker account '{account.AccountId}' requires PhoneNumber for pairing_code mode.");
                }
            }
        }
        if (config.Channels.Teams.MaxInboundChars < 1)
            errors.Add($"Channels.Teams.MaxInboundChars must be >= 1 (got {config.Channels.Teams.MaxInboundChars}).");
        if (config.Channels.Teams.MaxRequestBytes < 1024)
            errors.Add($"Channels.Teams.MaxRequestBytes must be >= 1024 (got {config.Channels.Teams.MaxRequestBytes}).");
        if (config.Channels.Teams.GroupPolicy is not ("open" or "allowlist" or "disabled"))
            errors.Add("Channels.Teams.GroupPolicy must be 'open', 'allowlist', or 'disabled'.");
        if (config.Channels.Teams.ReplyStyle is not ("thread" or "top-level"))
            errors.Add("Channels.Teams.ReplyStyle must be 'thread' or 'top-level'.");
        if (config.Channels.Teams.ChunkMode is not ("length" or "newline"))
            errors.Add("Channels.Teams.ChunkMode must be 'length' or 'newline'.");
        if (config.Channels.Teams.TextChunkLimit < 1)
            errors.Add($"Channels.Teams.TextChunkLimit must be >= 1 (got {config.Channels.Teams.TextChunkLimit}).");
        if (config.Channels.Teams.Enabled)
        {
            var teamsAppId = SecretResolver.Resolve(config.Channels.Teams.AppIdRef) ?? config.Channels.Teams.AppId;
            var teamsAppPassword = SecretResolver.Resolve(config.Channels.Teams.AppPasswordRef) ?? config.Channels.Teams.AppPassword;
            var teamsTenantId = SecretResolver.Resolve(config.Channels.Teams.TenantIdRef) ?? config.Channels.Teams.TenantId;
            if (string.IsNullOrWhiteSpace(teamsAppId))
                errors.Add("Channels.Teams.AppId/AppIdRef must be configured when Teams is enabled.");
            if (string.IsNullOrWhiteSpace(teamsAppPassword))
                errors.Add("Channels.Teams.AppPassword/AppPasswordRef must be configured when Teams is enabled.");
            if (string.IsNullOrWhiteSpace(teamsTenantId))
                errors.Add("Channels.Teams.TenantId/TenantIdRef must be configured when Teams is enabled.");
        }
        if (!config.Channels.AllowlistSemantics.Equals("legacy", StringComparison.OrdinalIgnoreCase) &&
            !config.Channels.AllowlistSemantics.Equals("strict", StringComparison.OrdinalIgnoreCase))
        {
            errors.Add("Channels.AllowlistSemantics must be 'legacy' or 'strict'.");
        }
        ValidateDmPolicy("Channels.Sms.DmPolicy", config.Channels.Sms.DmPolicy, errors);
        ValidateDmPolicy("Channels.Telegram.DmPolicy", config.Channels.Telegram.DmPolicy, errors);
        ValidateDmPolicy("Channels.WhatsApp.DmPolicy", config.Channels.WhatsApp.DmPolicy, errors);
        ValidateDmPolicy("Channels.Teams.DmPolicy", config.Channels.Teams.DmPolicy, errors);
        ValidateDmPolicy("Channels.Slack.DmPolicy", config.Channels.Slack.DmPolicy, errors);
        ValidateDmPolicy("Channels.Discord.DmPolicy", config.Channels.Discord.DmPolicy, errors);
        ValidateDmPolicy("Channels.Signal.DmPolicy", config.Channels.Signal.DmPolicy, errors);

        // Cron
        if (config.Cron.Enabled)
        {
            foreach (var job in config.Cron.Jobs)
            {
                if (string.IsNullOrWhiteSpace(job.Name))
                    errors.Add("Cron job name must be set.");
                if (string.IsNullOrWhiteSpace(job.Prompt))
                    errors.Add($"Cron job '{job.Name}' prompt must be set.");
                if (!IsValidCronExpression(job.CronExpression))
                    errors.Add($"Cron job '{job.Name}' has invalid CronExpression '{job.CronExpression}'.");
            }
        }

        // Webhooks
        if (config.Webhooks.Enabled)
        {
            foreach (var (name, endpoint) in config.Webhooks.Endpoints)
            {
                if (endpoint.MaxBodyLength < 1)
                    errors.Add($"Webhook endpoint '{name}' MaxBodyLength must be >= 1 (got {endpoint.MaxBodyLength}).");
                if (endpoint.MaxRequestBytes < 1024)
                    errors.Add($"Webhook endpoint '{name}' MaxRequestBytes must be >= 1024 (got {endpoint.MaxRequestBytes}).");
                if (endpoint.ValidateHmac)
                {
                    var secret = SecretResolver.Resolve(endpoint.Secret);
                    if (string.IsNullOrWhiteSpace(secret))
                    {
                        errors.Add(
                            $"Webhook endpoint '{name}' has ValidateHmac=true but no Secret is configured. " +
                            "Set OpenClaw:Webhooks:Endpoints:<name>:Secret.");
                    }
                }
            }
        }

        return errors;
    }

    private static void ValidateFractalMemory(FractalMemoryConfig config, List<string> errors)
    {
        if (!IsOneOf(config.Mode, "mcp"))
            errors.Add("Memory.Fractal.Mode must be 'mcp'.");
        if (config.Enabled && string.IsNullOrWhiteSpace(config.McpCommand))
            errors.Add("Memory.Fractal.McpCommand must be set when Fractal Memory is enabled.");
        if (config.DefaultDepth is < 0 or > 3)
            errors.Add($"Memory.Fractal.DefaultDepth must be between 0 and 3 (got {config.DefaultDepth}).");
        if (!IsOneOf(config.DefaultView, "index", "state", "timeline", "decisions", "children"))
            errors.Add("Memory.Fractal.DefaultView must be one of 'index', 'state', 'timeline', 'decisions', or 'children'.");
        if (!IsOneOf(config.DefaultExportMode, "compact", "standard", "verbose"))
            errors.Add("Memory.Fractal.DefaultExportMode must be one of 'compact', 'standard', or 'verbose'.");
        if (!IsOneOf(config.AutoContextMode, "off", "manual", "pulse", "auto"))
            errors.Add("Memory.Fractal.AutoContextMode must be one of 'off', 'manual', 'pulse', or 'auto'.");
        if (config.MaxContextChars < 1024)
            errors.Add($"Memory.Fractal.MaxContextChars must be >= 1024 (got {config.MaxContextChars}).");
        if (config.MaxContextTokens < 256)
            errors.Add($"Memory.Fractal.MaxContextTokens must be >= 256 (got {config.MaxContextTokens}).");
    }

    private static bool IsOneOf(string? value, params string[] allowed)
        => allowed.Any(candidate => string.Equals(value?.Trim(), candidate, StringComparison.OrdinalIgnoreCase));

    private static void ValidateCodingBackends(CodingBackendsConfig config, List<string> errors)
    {
        var backendIds = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var backend in config.EnumerateConfiguredBackends())
        {
            if (string.IsNullOrWhiteSpace(backend.BackendId))
            {
                errors.Add("CodingBackends entries must set BackendId.");
                continue;
            }

            if (!backendIds.Add(backend.BackendId))
                errors.Add($"CodingBackends backend id '{backend.BackendId}' must be unique.");

            if (backend.TimeoutSeconds < 1)
                errors.Add($"CodingBackends.{backend.BackendId}.TimeoutSeconds must be >= 1 (got {backend.TimeoutSeconds}).");

            if (string.IsNullOrWhiteSpace(backend.Provider))
                errors.Add($"CodingBackends.{backend.BackendId}.Provider must be set.");

            if (!backend.WriteEnabled && !backend.ReadOnlyByDefault)
                errors.Add($"CodingBackends.{backend.BackendId} must set ReadOnlyByDefault=true when WriteEnabled=false.");

            if (backend.RequireWorkspace && !string.IsNullOrWhiteSpace(backend.DefaultWorkspacePath) && !Path.IsPathRooted(backend.DefaultWorkspacePath))
                errors.Add($"CodingBackends.{backend.BackendId}.DefaultWorkspacePath must be absolute when set.");

            var credentialSourceCount = 0;
            if (!string.IsNullOrWhiteSpace(backend.Credentials.SecretRef))
                credentialSourceCount++;
            if (!string.IsNullOrWhiteSpace(backend.Credentials.TokenFilePath))
                credentialSourceCount++;
            if (!string.IsNullOrWhiteSpace(backend.Credentials.ConnectedAccountId))
                credentialSourceCount++;

            if (credentialSourceCount > 1)
                errors.Add($"CodingBackends.{backend.BackendId}.Credentials must specify at most one of SecretRef, TokenFilePath, or ConnectedAccountId.");
        }
    }

    private static void ValidateUrlSafety(string path, UrlSafetyConfig config, List<string> errors)
    {
        foreach (var cidr in config.BlockedCidrs)
        {
            if (string.IsNullOrWhiteSpace(cidr))
                continue;

            var parts = cidr.Split('/', 2, StringSplitOptions.TrimEntries);
            if (parts.Length != 2 ||
                !System.Net.IPAddress.TryParse(parts[0], out var address) ||
                !int.TryParse(parts[1], out var prefixLength))
            {
                errors.Add($"{path}.BlockedCidrs entry '{cidr}' must be a valid CIDR block.");
                continue;
            }

            var maxPrefix = address.AddressFamily == System.Net.Sockets.AddressFamily.InterNetwork ? 32 : 128;
            if (prefixLength < 0 || prefixLength > maxPrefix)
                errors.Add($"{path}.BlockedCidrs entry '{cidr}' has an invalid prefix length.");
        }
    }

    private static void ValidateNotionConfig(NotionConfig config, List<string> errors)
    {
        if (!config.Enabled)
            return;

        if (string.IsNullOrWhiteSpace(SecretResolver.Resolve(config.ApiKeyRef)))
            errors.Add("Plugins.Native.Notion.ApiKeyRef must resolve to a token when Notion is enabled.");

        if (!Uri.TryCreate(config.BaseUrl?.TrimEnd('/'), UriKind.Absolute, out _))
            errors.Add("Plugins.Native.Notion.BaseUrl must be a valid absolute URL when Notion is enabled.");

        if (string.IsNullOrWhiteSpace(config.ApiVersion))
            errors.Add("Plugins.Native.Notion.ApiVersion must be set when Notion is enabled.");

        if (config.MaxSearchResults < 1)
            errors.Add($"Plugins.Native.Notion.MaxSearchResults must be >= 1 (got {config.MaxSearchResults}).");

        var hasAnyTarget =
            !string.IsNullOrWhiteSpace(config.DefaultPageId) ||
            !string.IsNullOrWhiteSpace(config.DefaultDatabaseId) ||
            config.AllowedPageIds.Any(id => !string.IsNullOrWhiteSpace(id)) ||
            config.AllowedDatabaseIds.Any(id => !string.IsNullOrWhiteSpace(id));

        if (!hasAnyTarget)
        {
            errors.Add("Plugins.Native.Notion requires at least one allowed/default page or database id when enabled.");
        }
    }

    private static bool IsValidCronExpression(string expression)
    {
        if (string.IsNullOrWhiteSpace(expression))
            return false;

        expression = NormalizeCronExpression(expression);

        var parts = expression.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length != 5)
            return false;

        return IsValidCronField(parts[0], 0, 59) &&
               IsValidCronField(parts[1], 0, 23) &&
               IsValidCronField(parts[2], 1, 31) &&
               IsValidCronField(parts[3], 1, 12) &&
               IsValidCronField(parts[4], 0, 6);
    }

    private static string NormalizeCronExpression(string expression)
        => expression.Trim().ToLowerInvariant() switch
        {
            "@hourly" => "0 * * * *",
            "@daily" => "0 0 * * *",
            "@weekly" => "0 0 * * 0",
            "@monthly" => "0 0 1 * *",
            _ => expression
        };

    private static bool IsValidCronField(string field, int min, int max)
    {
        if (string.IsNullOrWhiteSpace(field))
            return false;

        if (field == "*")
            return true;

        if (field == "L")
            return min == 1;

        if (int.TryParse(field, out var exact))
            return exact >= min && exact <= max;

        if (field.Contains(','))
        {
            var options = field.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
            return options.Length > 0 && options.All(option => IsValidCronField(option, min, max));
        }

        if (field.Contains('/'))
        {
            var stepParts = field.Split('/');
            if (stepParts.Length != 2 || !int.TryParse(stepParts[1], out var step) || step <= 0)
                return false;

            return stepParts[0] == "*" || IsValidCronField(stepParts[0], min, max);
        }

        if (field.Contains('-'))
        {
            var rangeParts = field.Split('-');
            if (rangeParts.Length != 2 ||
                !int.TryParse(rangeParts[0], out var start) ||
                !int.TryParse(rangeParts[1], out var end))
            {
                return false;
            }

            return start >= min && start <= max && end >= min && end <= max;
        }

        return false;
    }

    private static void ValidateDmPolicy(string field, string? value, ICollection<string> errors)
    {
        if (value is null)
        {
            errors.Add($"{field} must be 'open', 'pairing', or 'closed'.");
            return;
        }

        if (!value.Equals("open", StringComparison.OrdinalIgnoreCase) &&
            !value.Equals("pairing", StringComparison.OrdinalIgnoreCase) &&
            !value.Equals("closed", StringComparison.OrdinalIgnoreCase))
        {
            errors.Add($"{field} must be 'open', 'pairing', or 'closed'.");
        }
    }

    private static void ValidateRootSet(string field, string[] roots, ICollection<string> errors)
    {
        if (roots.Length == 0)
            return;

        var wildcardCount = roots.Count(static root => string.Equals(root, "*", StringComparison.Ordinal));
        if (wildcardCount > 0 && roots.Length > wildcardCount)
            errors.Add($"{field} cannot mix '*' with explicit paths.");

        foreach (var root in roots)
        {
            if (string.Equals(root, "*", StringComparison.Ordinal))
                continue;

            var resolved = ResolveConfiguredPath(root);
            if (string.IsNullOrWhiteSpace(resolved))
            {
                errors.Add($"{field} entries must resolve to non-empty absolute paths.");
                continue;
            }

            if (!Path.IsPathRooted(resolved))
                errors.Add($"{field} entries must be absolute paths (got '{root}').");
        }
    }

    private static void ValidateModelProfiles(GatewayConfig config, List<string> errors, bool pluginBackedProvidersPossible)
    {
        var hasExplicitProfiles = config.Models.Profiles.Count > 0;
        var profileIds = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (var profile in config.Models.Profiles)
        {
            if (string.IsNullOrWhiteSpace(profile.Id))
            {
                errors.Add("Models.Profiles[].Id must be set.");
                continue;
            }

            if (!profileIds.Add(profile.Id))
                errors.Add($"Models.Profiles contains duplicate id '{profile.Id}'.");

            if (string.IsNullOrWhiteSpace(profile.Provider))
                errors.Add($"Models.Profiles.{profile.Id}.Provider must be set.");
            else if (!pluginBackedProvidersPossible && !BuiltInLlmProviders.Contains(profile.Provider))
                errors.Add($"Models.Profiles.{profile.Id}.Provider '{profile.Provider}' is not a supported built-in provider.");

            if (string.IsNullOrWhiteSpace(profile.Model))
                errors.Add($"Models.Profiles.{profile.Id}.Model must be set.");
            if (!string.IsNullOrWhiteSpace(profile.AuthMode) && !IsValidProviderAuthMode(profile.AuthMode))
                errors.Add($"Models.Profiles.{profile.Id}.AuthMode must be 'bearer' or 'tailnet-identity'.");
            else if (IsTailnetIdentityAuth(profile.AuthMode) && !SupportsTailnetIdentity(profile.Provider))
                errors.Add($"Models.Profiles.{profile.Id}.AuthMode 'tailnet-identity' is not supported for provider '{profile.Provider}'.");
            ValidateApertureProviderConfig(
                $"Models.Profiles.{profile.Id}",
                "BaseUrl",
                profile.Provider,
                profile.BaseUrl,
                profile.ApiKey,
                profile.AuthMode,
                errors);
            if (!string.IsNullOrWhiteSpace(profile.PresetId))
            {
                if (!LocalModelPresetCatalog.TryGet(profile.PresetId, out _))
                    errors.Add($"Models.Profiles.{profile.Id}.PresetId '{profile.PresetId}' is not a known local model preset.");
                else if (LocalModelPresetCatalog.TryGet(profile.PresetId, out var preset) &&
                         !string.Equals(profile.Provider, preset?.Provider, StringComparison.OrdinalIgnoreCase))
                    errors.Add($"Models.Profiles.{profile.Id}.PresetId '{profile.PresetId}' requires Provider='{preset?.Provider}'.");
            }
            if (profile.Capabilities?.MaxContextTokens < 0)
                errors.Add($"Models.Profiles.{profile.Id}.Capabilities.MaxContextTokens must be >= 0.");
            if (profile.Capabilities?.MaxOutputTokens < 0)
                errors.Add($"Models.Profiles.{profile.Id}.Capabilities.MaxOutputTokens must be >= 0.");
            ValidatePromptCaching(
                $"Models.Profiles.{profile.Id}.PromptCaching",
                profile.Provider,
                profile.PromptCaching,
                errors,
                isDynamicProvider: pluginBackedProvidersPossible && !BuiltInLlmProviders.Contains(profile.Provider));
        }

        if (!hasExplicitProfiles)
            profileIds.Add("default");

        if (!string.IsNullOrWhiteSpace(config.Models.DefaultProfile) &&
            !profileIds.Contains(config.Models.DefaultProfile))
        {
            errors.Add($"Models.DefaultProfile '{config.Models.DefaultProfile}' does not exist in Models.Profiles.");
        }

        foreach (var profile in config.Models.Profiles)
        {
            foreach (var fallbackId in profile.FallbackProfileIds.Where(static item => !string.IsNullOrWhiteSpace(item)))
            {
                if (!profileIds.Contains(fallbackId))
                    errors.Add($"Models.Profiles.{profile.Id}.FallbackProfileIds contains unknown profile '{fallbackId}'.");
            }
        }

        foreach (var (routeId, route) in config.Routing.Routes)
        {
            if (!string.IsNullOrWhiteSpace(route.ModelProfileId) && !profileIds.Contains(route.ModelProfileId))
                errors.Add($"Routing.Routes.{routeId}.ModelProfileId '{route.ModelProfileId}' does not exist in Models.Profiles.");

            foreach (var fallbackId in route.FallbackModelProfileIds.Where(static item => !string.IsNullOrWhiteSpace(item)))
            {
                if (!profileIds.Contains(fallbackId))
                    errors.Add($"Routing.Routes.{routeId}.FallbackModelProfileIds contains unknown profile '{fallbackId}'.");
            }
        }
    }

    private static string ResolveConfiguredPath(string? path)
        => ConfigPathResolver.Resolve(path);

    private static void ValidateApertureProviderConfig(
        string path,
        string endpointPropertyName,
        string? provider,
        string? endpoint,
        string? apiKey,
        string? authMode,
        ICollection<string> errors)
    {
        if (!string.Equals(provider, "aperture", StringComparison.OrdinalIgnoreCase))
            return;

        if (string.IsNullOrWhiteSpace(endpoint))
        {
            errors.Add($"{path}.{endpointPropertyName} must be set when Provider='aperture'.");
        }
        else if (!Uri.TryCreate(endpoint, UriKind.Absolute, out var uri) ||
                 (uri.Scheme != Uri.UriSchemeHttp && uri.Scheme != Uri.UriSchemeHttps))
        {
            errors.Add($"{path}.{endpointPropertyName} must be an absolute http(s) URL when Provider='aperture'.");
        }

        if (!IsTailnetIdentityAuth(authMode) && string.IsNullOrWhiteSpace(apiKey))
            errors.Add($"{path}.ApiKey must be set when Provider='aperture' and AuthMode is not 'tailnet-identity'.");
    }

    private static void ValidateWorkflows(WorkflowsConfig config, List<string> errors)
    {
        if (!config.Enabled)
            return;

        if (config.Backends.Count == 0)
        {
            errors.Add("Workflows is enabled but no backends are configured.");
            return;
        }

        foreach (var (backendId, backend) in config.Backends)
        {
            var path = $"Workflows.Backends.{backendId}";
            if (string.IsNullOrWhiteSpace(backendId))
            {
                errors.Add("Workflows.Backends contains an empty backend id.");
                path = "Workflows.Backends.<empty>";
            }

            if (!backend.Enabled)
                continue;

            var kind = string.IsNullOrWhiteSpace(backend.Kind)
                ? AgentWorkflowBackendKinds.MafDurableHttp
                : backend.Kind.Trim();
            if (!string.Equals(kind, AgentWorkflowBackendKinds.MafDurableHttp, StringComparison.OrdinalIgnoreCase))
                errors.Add($"{path}.Kind must be '{AgentWorkflowBackendKinds.MafDurableHttp}'.");

            if (!Uri.TryCreate(backend.BaseUrl, UriKind.Absolute, out var baseUrl) ||
                (baseUrl.Scheme != Uri.UriSchemeHttp && baseUrl.Scheme != Uri.UriSchemeHttps))
            {
                errors.Add($"{path}.BaseUrl must be an absolute http(s) URL.");
            }

            if (backend.PollIntervalSeconds < 1)
                errors.Add($"{path}.PollIntervalSeconds must be >= 1 (got {backend.PollIntervalSeconds}).");
            if (backend.TimeoutSeconds < 5)
                errors.Add($"{path}.TimeoutSeconds must be >= 5 (got {backend.TimeoutSeconds}).");
        }
    }

    private static void ValidateExternalCli(ExternalCliOptions config, List<string> errors)
    {
        if (config.DefaultTimeoutSeconds < 1)
            errors.Add($"ExternalCli.DefaultTimeoutSeconds must be >= 1 (got {config.DefaultTimeoutSeconds}).");
        if (config.MaxStdoutBytes < 1)
            errors.Add($"ExternalCli.MaxStdoutBytes must be >= 1 (got {config.MaxStdoutBytes}).");
        if (config.MaxStderrBytes < 1)
            errors.Add($"ExternalCli.MaxStderrBytes must be >= 1 (got {config.MaxStderrBytes}).");
        if (config.AllowFreeformCommands)
            errors.Add("ExternalCli.AllowFreeformCommands is not supported by this native connector; use named allowlisted commands.");

        var presetIds = config.Presets ?? [];
        for (var i = 0; i < presetIds.Length; i++)
        {
            if (string.IsNullOrWhiteSpace(presetIds[i]))
                errors.Add($"ExternalCli.Presets[{i}] must not be empty.");
        }

        foreach (var presetId in ExternalCliPresetCatalog.FindUnknownPresetIds(config))
            errors.Add($"ExternalCli.Presets contains unknown preset '{presetId}'.");

        var effectiveConfig = ExternalCliPresetCatalog.Apply(config);
        foreach (var (connectorName, connector) in effectiveConfig.Connectors)
        {
            if (string.IsNullOrWhiteSpace(connectorName))
                errors.Add("ExternalCli.Connectors contains an empty connector name.");
            if (connector.Enabled && string.IsNullOrWhiteSpace(connector.Executable))
                errors.Add($"ExternalCli.Connectors.{connectorName}.Executable must be set when connector is enabled.");

            var defaultFormat = ExternalCliOutputFormat.Normalize(connector.DefaultOutputFormat);
            if (!string.Equals(defaultFormat, connector.DefaultOutputFormat, StringComparison.OrdinalIgnoreCase))
                errors.Add($"ExternalCli.Connectors.{connectorName}.DefaultOutputFormat must be one of: json, ndjson, csv, text, table.");
            ValidateRegexList($"ExternalCli.Connectors.{connectorName}.RedactionRules", connector.RedactionRules, errors);

            foreach (var (commandName, command) in connector.Commands)
            {
                if (string.IsNullOrWhiteSpace(commandName))
                    errors.Add($"ExternalCli.Connectors.{connectorName}.Commands contains an empty command name.");
                if (command.ArgsTemplate.Length == 0)
                    errors.Add($"ExternalCli.Connectors.{connectorName}.Commands.{commandName}.ArgsTemplate must contain at least one argument.");
                if (command.SupportsDryRun && command.DryRunArgsTemplate.Length == 0)
                    errors.Add($"ExternalCli.Connectors.{connectorName}.Commands.{commandName}.DryRunArgsTemplate must be set when SupportsDryRun=true.");
                if (command.TimeoutSeconds is <= 0)
                    errors.Add($"ExternalCli.Connectors.{connectorName}.Commands.{commandName}.TimeoutSeconds must be >= 1 when set.");

                var risk = ExternalCliRiskLevel.Normalize(command.RiskLevel);
                if (!string.Equals(risk, command.RiskLevel, StringComparison.OrdinalIgnoreCase))
                    errors.Add($"ExternalCli.Connectors.{connectorName}.Commands.{commandName}.RiskLevel must be low, medium, or high.");

                if (!string.IsNullOrWhiteSpace(command.StructuredOutput))
                {
                    var format = ExternalCliOutputFormat.Normalize(command.StructuredOutput);
                    if (!string.Equals(format, command.StructuredOutput, StringComparison.OrdinalIgnoreCase))
                        errors.Add($"ExternalCli.Connectors.{connectorName}.Commands.{commandName}.StructuredOutput must be one of: json, ndjson, csv, text, table.");
                }

                ValidateRegexList($"ExternalCli.Connectors.{connectorName}.Commands.{commandName}.RedactionRules", command.RedactionRules, errors);
                foreach (var (parameterName, parameter) in command.Parameters)
                {
                    if (!string.IsNullOrWhiteSpace(parameter.Pattern))
                        ValidateRegexPattern($"ExternalCli.Connectors.{connectorName}.Commands.{commandName}.Parameters.{parameterName}.Pattern", parameter.Pattern, errors);
                }
            }
        }
    }

    private static void ValidateRegexList(string path, IReadOnlyList<string> patterns, List<string> errors)
    {
        for (var i = 0; i < patterns.Count; i++)
        {
            if (!string.IsNullOrWhiteSpace(patterns[i]))
                ValidateRegexPattern($"{path}[{i}]", patterns[i], errors);
        }
    }

    private static void ValidateRegexPattern(string path, string pattern, List<string> errors)
    {
        try
        {
            _ = new Regex(pattern, RegexOptions.CultureInvariant, TimeSpan.FromMilliseconds(250));
        }
        catch (ArgumentException ex)
        {
            errors.Add($"{path} is not a valid regex: {ex.Message}");
        }
    }

    private static void ValidatePromptCaching(
        string prefix,
        string? providerId,
        PromptCachingConfig? caching,
        List<string> errors,
        bool isDynamicProvider)
    {
        if (caching is null || caching.Enabled != true)
            return;

        var retention = (caching.Retention ?? "auto").Trim().ToLowerInvariant();
        if (retention is not ("none" or "short" or "long" or "auto"))
            errors.Add($"{prefix}.Retention must be one of: none, short, long, auto.");

        var dialect = (caching.Dialect ?? "auto").Trim().ToLowerInvariant();
        if (dialect is not ("auto" or "openai" or "anthropic" or "gemini" or "none"))
            errors.Add($"{prefix}.Dialect must be one of: auto, openai, anthropic, gemini, none.");

        var provider = (providerId ?? string.Empty).Trim();
        var requireExplicitDialect =
            provider.Equals("openai-compatible", StringComparison.OrdinalIgnoreCase)
            || provider.Equals("aperture", StringComparison.OrdinalIgnoreCase)
            || provider.Equals("groq", StringComparison.OrdinalIgnoreCase)
            || provider.Equals("together", StringComparison.OrdinalIgnoreCase)
            || provider.Equals("lmstudio", StringComparison.OrdinalIgnoreCase)
            || isDynamicProvider;

        if (requireExplicitDialect && dialect == "auto")
            errors.Add($"{prefix}.Dialect must be explicit for provider '{provider}'.");

        if (caching.KeepWarmEnabled == true)
        {
            if (caching.KeepWarmIntervalMinutes < 5)
                errors.Add($"{prefix}.KeepWarmIntervalMinutes must be >= 5 when keep-warm is enabled.");

            if (!SupportsExplicitCacheTtl(provider, dialect))
            {
                errors.Add($"{prefix}.KeepWarmEnabled is only valid for providers with explicit cache TTL semantics.");
            }
        }
    }

    private static bool IsValidProviderAuthMode(string? authMode)
    {
        var normalized = string.IsNullOrWhiteSpace(authMode) ? "bearer" : authMode.Trim().ToLowerInvariant();
        return normalized is "bearer" or "tailnet-identity";
    }

    private static bool IsTailnetIdentityAuth(string? authMode)
        => string.Equals(authMode?.Trim(), "tailnet-identity", StringComparison.OrdinalIgnoreCase);

    private static bool SupportsTailnetIdentity(string? provider)
        => provider is not null &&
           (provider.Equals("aperture", StringComparison.OrdinalIgnoreCase) ||
            provider.Equals("openai-compatible", StringComparison.OrdinalIgnoreCase));

    private static bool SupportsExplicitCacheTtl(string? providerId, string? dialect)
    {
        var provider = (providerId ?? string.Empty).Trim();
        var normalizedDialect = (dialect ?? "auto").Trim();
        if (provider.Equals("anthropic", StringComparison.OrdinalIgnoreCase) ||
            provider.Equals("claude", StringComparison.OrdinalIgnoreCase) ||
            provider.Equals("anthropic-vertex", StringComparison.OrdinalIgnoreCase))
            return true;

        if (provider.Equals("amazon-bedrock", StringComparison.OrdinalIgnoreCase))
            return string.Equals(normalizedDialect, "anthropic", StringComparison.OrdinalIgnoreCase)
                || string.Equals(normalizedDialect, "auto", StringComparison.OrdinalIgnoreCase);

        if (provider.Equals("gemini", StringComparison.OrdinalIgnoreCase) ||
            provider.Equals("google", StringComparison.OrdinalIgnoreCase))
        {
            return string.Equals(normalizedDialect, "gemini", StringComparison.OrdinalIgnoreCase)
                || string.Equals(normalizedDialect, "auto", StringComparison.OrdinalIgnoreCase);
        }

        return false;
    }
}

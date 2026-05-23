using System.Text.Json;
using OpenClaw.Client;
using OpenClaw.Core.Abstractions;
using OpenClaw.Core.Governance;
using OpenClaw.Core.Memory;
using OpenClaw.Core.Models;
using OpenClaw.Core.Pipeline;
using OpenClaw.Core.Security;
using OpenClaw.Core.Skills;
using OpenClaw.Core.Validation;
using static OpenClaw.Testing.HarnessRegressionScenarioText;

namespace OpenClaw.Testing;

public static class HarnessRegressionScenarios
{
    public static IReadOnlyList<IHarnessRegressionScenario> CreateDefault() =>
    [
        new QuickstartConfigLoadsScenario(),
        new ProviderConfigShapeScenario(),
        new PublicBindHardeningScenario(),
        new UrlSafetyDefaultsScenario(),
        new ToolApprovalPolicyScenario(),
        new MemoryStoreRoundTripScenario(),
        new SessionStoreRoundTripScenario(),
        new HarnessContractSerializationScenario(),
        new EvidenceBundleSerializationScenario(),
        new GovernanceLedgerSerializationScenario(),
        new McpInitializeShapeScenario(),
        new OpenAiCompatRequestShapeScenario(),
        new LearningProposalReviewFirstScenario(),
        new ManagedSkillValidationScenario(),
        new TailscaleServeProfileNonPublicScenario(),
        new HarnessRegressionDocsScenario()
    ];
}

internal sealed class QuickstartConfigLoadsScenario()
    : HarnessRegressionScenarioBase(
        "onboarding.quickstart_config",
        "Quickstart config loads",
        HarnessRegressionCategory.Onboarding)
{
    protected override ValueTask<HarnessRegressionScenarioResult> EvaluateAsync(
        HarnessRegressionContext context,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();

        if (context.Config is not null)
        {
            return ValueTask.FromResult(Passed(
                "Config loaded successfully.",
                $"Config path: {context.ConfigPath}"));
        }

        if (!context.ConfigPathExplicit && !context.ConfigExists)
        {
            return ValueTask.FromResult(Skipped(
                "No default config was found; run setup or pass --config to check a specific config.",
                $"Expected config path: {context.ConfigPath}"));
        }

        return ValueTask.FromResult(Failed(
            "Config could not be loaded.",
            $"Config path: {context.ConfigPath}",
            context.ConfigLoadError));
    }
}

internal sealed class ProviderConfigShapeScenario()
    : HarnessRegressionScenarioBase(
        "providers.config_shape",
        "Provider config shape",
        HarnessRegressionCategory.Providers)
{
    protected override ValueTask<HarnessRegressionScenarioResult> EvaluateAsync(
        HarnessRegressionContext context,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();

        if (context.Config is null)
            return ValueTask.FromResult(Skipped("Provider shape was skipped because no config is loaded."));

        var config = context.Config;
        var failures = new List<string>();
        var notes = new List<string>();
        var provider = Normalize(config.Llm.Provider);
        var model = Normalize(config.Llm.Model);

        if (string.IsNullOrWhiteSpace(provider))
            failures.Add("OpenClaw:Llm:Provider must be set.");
        if (string.IsNullOrWhiteSpace(model))
            failures.Add("OpenClaw:Llm:Model must be set.");
        if (!IsValidAuthMode(config.Llm.AuthMode))
            failures.Add("OpenClaw:Llm:AuthMode must be bearer or tailnet-identity.");
        if (RequiresEndpoint(provider) && !IsAbsoluteHttpUrl(config.Llm.Endpoint))
            failures.Add($"Provider '{provider}' requires an absolute http(s) endpoint.");

        foreach (var profile in config.Models.Profiles)
        {
            if (string.IsNullOrWhiteSpace(profile.Id))
                failures.Add("Models.Profiles[].Id must be set.");
            if (string.IsNullOrWhiteSpace(profile.Provider))
                failures.Add($"Models.Profiles.{profile.Id}.Provider must be set.");
            if (string.IsNullOrWhiteSpace(profile.Model))
                failures.Add($"Models.Profiles.{profile.Id}.Model must be set.");
            if (!string.IsNullOrWhiteSpace(profile.AuthMode) && !IsValidAuthMode(profile.AuthMode))
                failures.Add($"Models.Profiles.{profile.Id}.AuthMode must be bearer or tailnet-identity.");
            if (RequiresEndpoint(Normalize(profile.Provider)) && !IsAbsoluteHttpUrl(profile.BaseUrl ?? config.Llm.Endpoint))
                failures.Add($"Models.Profiles.{profile.Id} requires an absolute http(s) base URL.");
        }

        if (context.Offline)
            notes.Add("Credential and network checks were skipped because offline mode is enabled.");

        if (failures.Count > 0)
            return ValueTask.FromResult(Failed("Provider/model shape has blocking issue(s).", Join(failures)));

        var details = Join(notes);
        return ValueTask.FromResult(Passed(
            "Provider/model shape is valid without external network calls.",
            string.IsNullOrWhiteSpace(details) ? null : details));
    }

    private static string Normalize(string? value)
        => string.IsNullOrWhiteSpace(value) ? "" : value.Trim().ToLowerInvariant();

    private static bool IsValidAuthMode(string? value)
        => string.IsNullOrWhiteSpace(value) ||
           string.Equals(value, "bearer", StringComparison.OrdinalIgnoreCase) ||
           string.Equals(value, "tailnet-identity", StringComparison.OrdinalIgnoreCase);

    private static bool RequiresEndpoint(string provider)
        => provider is "openai-compatible" or "aperture" or "groq" or "together" or "lmstudio" or "anthropic-vertex" or "amazon-bedrock" or "azure-openai";

    private static bool IsAbsoluteHttpUrl(string? value)
        => Uri.TryCreate(value, UriKind.Absolute, out var uri) &&
           (uri.Scheme == Uri.UriSchemeHttp || uri.Scheme == Uri.UriSchemeHttps);
}

internal sealed class PublicBindHardeningScenario()
    : HarnessRegressionScenarioBase(
        "security.public_bind_hardening",
        "Public bind hardening",
        HarnessRegressionCategory.Security)
{
    protected override ValueTask<HarnessRegressionScenarioResult> EvaluateAsync(
        HarnessRegressionContext context,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();

        if (context.Config is null)
            return ValueTask.FromResult(Skipped("Public bind posture was skipped because no config is loaded."));

        var config = context.Config;
        if (BindAddressClassifier.IsLoopbackBind(config.BindAddress))
            return ValueTask.FromResult(Passed("Gateway bind address is loopback-only."));

        var failures = new List<string>();
        var warnings = new List<string>();

        if (string.IsNullOrWhiteSpace(config.AuthToken))
            failures.Add("AuthToken must be configured for non-loopback binds.");
        if (config.Canvas.Enabled && !config.Canvas.AllowOnPublicBind)
            failures.Add("Canvas command forwarding is enabled on a public bind without Canvas.AllowOnPublicBind.");
        if ((config.Plugins.Enabled || config.Plugins.DynamicNative.Enabled || config.Plugins.Mcp.Enabled) &&
            !config.Security.AllowPluginBridgeOnPublicBind)
        {
            failures.Add("Plugin execution is enabled on a public bind without AllowPluginBridgeOnPublicBind.");
        }
        if (UnsafeLocalToolingExposed(config) && !config.Security.AllowUnsafeToolingOnPublicBind)
            failures.Add("Unsafe local tooling is exposed on a public bind without explicit opt-in.");
        if (!config.Security.RequireRequesterMatchForHttpToolApproval)
            failures.Add("Requester-matched HTTP tool approvals are disabled on a public bind.");

        if (failures.Count > 0)
            return ValueTask.FromResult(Failed("Public/non-loopback bind is missing required hardening.", Join([.. failures, .. warnings])));
        if (warnings.Count > 0)
            return ValueTask.FromResult(Warning("Public bind has posture warning(s).", Join(warnings)));

        return ValueTask.FromResult(Passed("Public bind hardening posture is acceptable."));
    }

    private static bool UnsafeLocalToolingExposed(GatewayConfig config)
    {
        if (IsUnsafeLocalExecutionToolExposed(config, "shell", ToolSandboxMode.Prefer))
            return true;

        if (IsUnsafeLocalExecutionToolExposed(config, "process", ToolSandboxMode.Prefer))
            return true;

        if (config.Plugins.Native.CodeExec.Enabled &&
            IsLocalExecutionRoute(config, "code_exec") &&
            !ToolSandboxPolicy.IsRequireSandboxed(config, "code_exec", ToolSandboxMode.Prefer))
        {
            return true;
        }

        return config.Tooling.AllowedWriteRoots.Contains("*", StringComparer.Ordinal) ||
               config.Tooling.AllowedReadRoots.Contains("*", StringComparer.Ordinal);
    }

    private static bool IsUnsafeLocalExecutionToolExposed(
        GatewayConfig config,
        string toolName,
        ToolSandboxMode defaultMode)
        => config.Tooling.AllowShell &&
           IsLocalExecutionRoute(config, toolName) &&
           !ToolSandboxPolicy.IsRequireSandboxed(config, toolName, defaultMode);

    private static bool IsLocalExecutionRoute(GatewayConfig config, string toolName)
    {
        if (TryResolveExecutionBackend(config, toolName, out var backendName))
            return string.Equals(backendName, ExecutionBackendType.Local, StringComparison.OrdinalIgnoreCase);

        return string.Equals(config.Execution.DefaultBackend, ExecutionBackendType.Local, StringComparison.OrdinalIgnoreCase);
    }

    private static bool TryResolveExecutionBackend(GatewayConfig config, string toolName, out string? backendName)
    {
        backendName = null;
        if (!config.Execution.Enabled)
            return false;

        if (config.Execution.Tools.TryGetValue(toolName, out var route) && !string.IsNullOrWhiteSpace(route.Backend))
        {
            backendName = route.Backend;
            return true;
        }

        if (string.Equals(toolName, "process", StringComparison.OrdinalIgnoreCase) &&
            config.Execution.Tools.TryGetValue("shell", out var shellRoute) &&
            !string.IsNullOrWhiteSpace(shellRoute.Backend))
        {
            backendName = shellRoute.Backend;
            return true;
        }

        return false;
    }
}

internal sealed class UrlSafetyDefaultsScenario()
    : HarnessRegressionScenarioBase(
        "security.url_safety_defaults",
        "URL safety defaults",
        HarnessRegressionCategory.Security)
{
    protected override ValueTask<HarnessRegressionScenarioResult> EvaluateAsync(
        HarnessRegressionContext context,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();

        var defaults = new UrlSafetyConfig();
        var blocked = new[]
        {
            "http://localhost/",
            "http://127.0.0.1/",
            "http://10.0.0.1/",
            "http://169.254.169.254/"
        };
        var allowedFailures = blocked
            .Select(url => new { Url = url, Result = UrlSafetyValidator.ValidateHttpUrl(new Uri(url), defaults, resolveDns: false) })
            .Where(item => item.Result.Allowed)
            .Select(item => item.Url)
            .ToArray();

        if (allowedFailures.Length > 0)
            return ValueTask.FromResult(Failed("Default URL safety did not block private target(s).", Join(allowedFailures)));

        if (context.Config?.Tooling.UrlSafety is { Enabled: false } ||
            context.Config?.Tooling.UrlSafety is { BlockPrivateNetworkTargets: false })
        {
            return ValueTask.FromResult(Warning(
                "Default URL safety blocks private targets, but the loaded config weakens URL safety.",
                "Set Tooling.UrlSafety.Enabled=true and Tooling.UrlSafety.BlockPrivateNetworkTargets=true."));
        }

        return ValueTask.FromResult(Passed("Default URL safety blocks loopback, private, and metadata targets."));
    }
}

internal sealed class ToolApprovalPolicyScenario()
    : HarnessRegressionScenarioBase(
        "approvals.tool_approval_policy",
        "Tool approval policy",
        HarnessRegressionCategory.Approvals)
{
    private static readonly string[] HighRiskTools = ["shell", "code_exec", "git", "external_cli", "payment"];

    protected override ValueTask<HarnessRegressionScenarioResult> EvaluateAsync(
        HarnessRegressionContext context,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();

        var missing = new List<string>();
        foreach (var tool in HighRiskTools)
        {
            var descriptor = ToolGovernanceDescriptorCatalog.Resolve(
                tool,
                description: "",
                ToolActionPolicyResolver.Resolve(tool, "{}"));
            if (!descriptor.RequiresApproval)
                missing.Add(tool);
        }

        if (missing.Count > 0)
            return ValueTask.FromResult(Failed("High-risk tool descriptors no longer require approval.", Join(missing)));

        var supervised = new GatewayConfig
        {
            Tooling =
            {
                AutonomyMode = "supervised",
                RequireToolApproval = true,
                ApprovalRequiredTools = ["shell", "write_file", "code_exec", "git", "external_cli", "payment"]
            }
        };
        var configuredMissing = HighRiskTools
            .Where(tool => !supervised.Tooling.ApprovalRequiredTools.Contains(tool, StringComparer.OrdinalIgnoreCase))
            .ToArray();

        if (configuredMissing.Length > 0)
            return ValueTask.FromResult(Failed("Synthetic supervised approval policy does not cover high-risk tool(s).", Join(configuredMissing)));

        if (context.Config?.Tooling is { AutonomyMode: "supervised", RequireToolApproval: false })
        {
            return ValueTask.FromResult(Warning(
                "High-risk tool descriptors require approval, but the loaded supervised config has RequireToolApproval=false.",
                "Run openclaw admin approvals simulate --tool shell to inspect effective runtime policy."));
        }

        return ValueTask.FromResult(Passed("High-risk tool descriptors and supervised approval policy remain approval-first."));
    }
}

internal sealed class MemoryStoreRoundTripScenario()
    : HarnessRegressionScenarioBase(
        "memory.store_round_trip",
        "Memory store round-trip",
        HarnessRegressionCategory.Memory)
{
    protected override async ValueTask<HarnessRegressionScenarioResult> EvaluateAsync(
        HarnessRegressionContext context,
        CancellationToken cancellationToken)
    {
        var root = HarnessRegressionPaths.Child(context.TempWorkspacePath, "memory");
        await using var store = new FileMemoryStore(root, maxCachedSessions: 4);
        const string key = "harness:regression:note";
        const string content = "Harness regression memory note.";

        await store.SaveNoteAsync(key, content, cancellationToken);
        var loaded = await store.LoadNoteAsync(key, cancellationToken);
        var listed = await store.ListNotesWithPrefixAsync("harness:", cancellationToken);
        var hits = await ((IMemoryNoteSearch)store).SearchNotesAsync("regression memory", "harness:", 5, cancellationToken);

        if (!string.Equals(content, loaded, StringComparison.Ordinal))
            return Failed("Memory note did not load with the saved content.");
        if (!listed.Contains(key, StringComparer.Ordinal))
            return Failed("Memory note prefix listing did not include the saved note.");
        if (!hits.Any(hit => string.Equals(hit.Key, key, StringComparison.Ordinal)))
            return Failed("Memory note search did not find the saved note.");

        return Passed("File memory store saved, loaded, listed, and searched a note.");
    }
}

internal sealed class SessionStoreRoundTripScenario()
    : HarnessRegressionScenarioBase(
        "sessions.store_round_trip",
        "Session store round-trip",
        HarnessRegressionCategory.Sessions)
{
    protected override async ValueTask<HarnessRegressionScenarioResult> EvaluateAsync(
        HarnessRegressionContext context,
        CancellationToken cancellationToken)
    {
        var root = HarnessRegressionPaths.Child(context.TempWorkspacePath, "sessions");
        await using var store = new FileMemoryStore(root, maxCachedSessions: 4);
        var session = new Session
        {
            Id = "sess_harness_regression",
            ChannelId = "harness",
            SenderId = "regression"
        };

        await store.SaveSessionAsync(session, cancellationToken);
        var loaded = await store.GetSessionAsync(session.Id, cancellationToken);

        return loaded is not null &&
               loaded.Id == session.Id &&
               loaded.ChannelId == session.ChannelId &&
               loaded.SenderId == session.SenderId
            ? Passed("File session store saved and loaded a session.")
            : Failed("File session store did not load the saved session.");
    }
}

internal sealed class HarnessContractSerializationScenario()
    : HarnessRegressionScenarioBase(
        "harness.contract_serialization",
        "Harness Contract serialization",
        HarnessRegressionCategory.Harness)
{
    protected override ValueTask<HarnessRegressionScenarioResult> EvaluateAsync(
        HarnessRegressionContext context,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();

        var contract = new HarnessContract
        {
            Id = "hctr_regression",
            Status = HarnessContractStatus.Proposed,
            Goal = "Verify harness model serialization.",
            ApprovalRequired = HarnessContractApprovalRequirements.Required,
            PlannedActions =
            [
                new HarnessContractAction
                {
                    Id = "act_read",
                    Title = "Read docs",
                    ToolName = "read_file",
                    ActionType = "read"
                }
            ],
            VerificationPlan =
            [
                new HarnessContractVerificationStep
                {
                    Id = "verify_json",
                    Title = "JSON round-trip",
                    Kind = "serialization"
                }
            ]
        };

        var json = JsonSerializer.Serialize(contract, CoreJsonContext.Default.HarnessContract);
        var restored = JsonSerializer.Deserialize(json, CoreJsonContext.Default.HarnessContract);

        return restored is not null &&
               restored.Id == contract.Id &&
               restored.PlannedActions.Count == 1 &&
               restored.VerificationPlan.Count == 1
            ? ValueTask.FromResult(Passed("Harness Contract model round-tripped through source-generated JSON."))
            : ValueTask.FromResult(Failed("Harness Contract model did not round-trip correctly."));
    }
}

internal sealed class EvidenceBundleSerializationScenario()
    : HarnessRegressionScenarioBase(
        "harness.evidence_bundle_serialization",
        "Evidence Bundle serialization",
        HarnessRegressionCategory.Harness)
{
    protected override ValueTask<HarnessRegressionScenarioResult> EvaluateAsync(
        HarnessRegressionContext context,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();

        var bundle = new EvidenceBundle
        {
            Id = "evb_regression",
            Title = "Harness regression evidence",
            Summary = "Synthetic evidence bundle for regression serialization.",
            Confidence = EvidenceConfidenceLevels.High,
            HarnessContractId = "hctr_regression",
            Checks =
            [
                new EvidenceCheck
                {
                    Id = "check_round_trip",
                    Name = "Round-trip",
                    Status = EvidenceCheckStatuses.Passed,
                    Summary = "Model round-tripped."
                }
            ]
        };

        var json = JsonSerializer.Serialize(bundle, CoreJsonContext.Default.EvidenceBundle);
        var restored = JsonSerializer.Deserialize(json, CoreJsonContext.Default.EvidenceBundle);

        return restored is not null &&
               restored.Id == bundle.Id &&
               restored.Checks.Count == 1 &&
               restored.HarnessContractId == bundle.HarnessContractId
            ? ValueTask.FromResult(Passed("Evidence Bundle model round-tripped through source-generated JSON."))
            : ValueTask.FromResult(Failed("Evidence Bundle model did not round-trip correctly."));
    }
}

internal sealed class GovernanceLedgerSerializationScenario()
    : HarnessRegressionScenarioBase(
        "harness.governance_ledger_serialization",
        "Governance Ledger serialization",
        HarnessRegressionCategory.Harness)
{
    protected override ValueTask<HarnessRegressionScenarioResult> EvaluateAsync(
        HarnessRegressionContext context,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();

        var entry = new GovernanceLedgerEntry
        {
            Id = "gov_regression",
            Decision = GovernanceDecisions.Approved,
            Status = GovernanceDecisionStatuses.Active,
            Source = GovernanceLedgerSources.ToolApproval,
            ToolName = "shell",
            ActionSummary = "Run a supervised shell command.",
            RiskLevel = GovernanceRiskLevels.High,
            Scope = GovernanceScopes.Session,
            HarnessContractId = "hctr_regression",
            EvidenceBundleId = "evb_regression",
            PolicyHint = new GovernancePolicyHint
            {
                RequiresReview = true,
                Notes = "Review before reuse."
            }
        };

        var json = JsonSerializer.Serialize(entry, CoreJsonContext.Default.GovernanceLedgerEntry);
        var restored = JsonSerializer.Deserialize(json, CoreJsonContext.Default.GovernanceLedgerEntry);

        return restored is not null &&
               restored.Id == entry.Id &&
               restored.Decision == GovernanceDecisions.Approved &&
               restored.PolicyHint is { RequiresReview: true }
            ? ValueTask.FromResult(Passed("Governance Ledger entry round-tripped through source-generated JSON."))
            : ValueTask.FromResult(Failed("Governance Ledger entry did not round-trip correctly."));
    }
}

internal sealed class McpInitializeShapeScenario()
    : HarnessRegressionScenarioBase(
        "mcp.initialize_shape",
        "MCP initialize shape",
        HarnessRegressionCategory.Mcp)
{
    protected override ValueTask<HarnessRegressionScenarioResult> EvaluateAsync(
        HarnessRegressionContext context,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();

        var initialize = new McpInitializeRequest
        {
            ProtocolVersion = "2025-03-26",
            ClientInfo = new McpClientInfo { Name = "openclaw-harness", Version = "1.0.0" }
        };
        var rpc = new McpJsonRpcRequest
        {
            Id = "1",
            Method = "initialize",
            Params = JsonSerializer.SerializeToElement(
                initialize,
                HarnessRegressionJsonContext.Default.McpInitializeRequest)
        };

        var json = JsonSerializer.Serialize(rpc, HarnessRegressionJsonContext.Default.McpJsonRpcRequest);
        var restored = JsonSerializer.Deserialize(json, HarnessRegressionJsonContext.Default.McpJsonRpcRequest);

        return restored is not null &&
               restored.Jsonrpc == "2.0" &&
               restored.Method == "initialize" &&
               restored.Params.TryGetProperty("clientInfo", out var clientInfo) &&
               clientInfo.TryGetProperty("name", out var name) &&
               name.GetString() == "openclaw-harness"
            ? ValueTask.FromResult(Passed("MCP initialize request shape serializes without running a gateway."))
            : ValueTask.FromResult(Failed("MCP initialize request shape did not serialize correctly."));
    }
}

internal sealed class OpenAiCompatRequestShapeScenario()
    : HarnessRegressionScenarioBase(
        "openai_compat.request_shape",
        "OpenAI-compatible request shape",
        HarnessRegressionCategory.OpenAiCompat)
{
    protected override ValueTask<HarnessRegressionScenarioResult> EvaluateAsync(
        HarnessRegressionContext context,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();

        const string json = """
            {
              "model": "gpt-4o-mini",
              "messages": [{ "role": "user", "content": "ping" }],
              "stream": false,
              "max_tokens": 32
            }
            """;

        var request = JsonSerializer.Deserialize(json, CoreJsonContext.Default.OpenAiChatCompletionRequest);
        if (request is null || request.Messages.Count != 1 || request.Messages[0].Content.ToPromptText() != "ping")
            return ValueTask.FromResult(Failed("OpenAI-compatible chat completion request did not parse."));

        var serialized = JsonSerializer.Serialize(request, CoreJsonContext.Default.OpenAiChatCompletionRequest);
        return serialized.Contains("\"max_tokens\"", StringComparison.Ordinal)
            ? ValueTask.FromResult(Passed("OpenAI-compatible request shape parses and serializes without a provider."))
            : ValueTask.FromResult(Failed("OpenAI-compatible request serialization lost max_tokens shape."));
    }
}

internal sealed class LearningProposalReviewFirstScenario()
    : HarnessRegressionScenarioBase(
        "harness.learning_proposal_review_first",
        "Learning proposal review-first default",
        HarnessRegressionCategory.Harness)
{
    protected override ValueTask<HarnessRegressionScenarioResult> EvaluateAsync(
        HarnessRegressionContext context,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();

        var defaults = new LearningConfig();
        if (!defaults.ReviewRequired)
            return ValueTask.FromResult(Failed("LearningConfig defaults no longer require review."));

        if (context.Config?.Learning is { Enabled: true, ReviewRequired: false })
        {
            return ValueTask.FromResult(Warning(
                "Learning defaults require review, but the loaded config disables learning review.",
                "Set Learning:ReviewRequired=true for review-first harness evolution."));
        }

        return ValueTask.FromResult(Passed("Learning proposals default to review-required."));
    }
}

internal sealed class ManagedSkillValidationScenario()
    : HarnessRegressionScenarioBase(
        "tools.managed_skill_validation",
        "Managed skill validation",
        HarnessRegressionCategory.Tools)
{
    protected override async ValueTask<HarnessRegressionScenarioResult> EvaluateAsync(
        HarnessRegressionContext context,
        CancellationToken cancellationToken)
    {
        var skillRoot = HarnessRegressionPaths.Child(context.TempWorkspacePath, "managed-skill");
        Directory.CreateDirectory(skillRoot);
        await File.WriteAllTextAsync(
            Path.Join(skillRoot, "SKILL.md"),
            """
            ---
            name: harness-regression-demo
            description: Deterministic managed skill validation fixture.
            metadata: {"always":true}
            ---
            Use this deterministic skill for harness regression checks.
            """,
            cancellationToken);

        var inspection = SkillInspector.InspectPath(skillRoot, SkillSource.Managed);
        return inspection.Success &&
               inspection.Definition is not null &&
               inspection.Definition.Name == "harness-regression-demo"
            ? Passed("Managed SKILL.md draft parsed successfully.", $"Skill file: {inspection.SkillFilePath}")
            : Failed("Managed SKILL.md draft validation failed.", error: inspection.ErrorMessage);
    }
}

internal sealed class TailscaleServeProfileNonPublicScenario()
    : HarnessRegressionScenarioBase(
        "deployment.tailscale_serve_profile_non_public",
        "Tailscale Serve profile non-public bind",
        HarnessRegressionCategory.Deployment)
{
    protected override ValueTask<HarnessRegressionScenarioResult> EvaluateAsync(
        HarnessRegressionContext context,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();

        var synthetic = new GatewayConfig();
        synthetic.Deployment.Mode = "tailscale-serve";
        if (!TailscaleServeAdvisor.IsTailscaleServeConfigured(synthetic) ||
            !BindAddressClassifier.IsLoopbackBind(synthetic.BindAddress))
        {
            return ValueTask.FromResult(Failed("Tailscale Serve profile default is no longer loopback-only."));
        }

        if (context.Config is not null &&
            TailscaleServeAdvisor.IsTailscaleServeConfigured(context.Config) &&
            !BindAddressClassifier.IsLoopbackBind(context.Config.BindAddress))
        {
            return ValueTask.FromResult(Failed(
                "Loaded Tailscale Serve config is public-bound.",
                "Tailscale Serve should proxy to a loopback OpenClaw gateway by default."));
        }

        return ValueTask.FromResult(Passed("Tailscale Serve profile remains loopback-bound by default."));
    }
}

internal sealed class HarnessRegressionDocsScenario()
    : HarnessRegressionScenarioBase(
        "docs.harness_regression_docs",
        "Harness regression docs",
        HarnessRegressionCategory.Docs)
{
    protected override ValueTask<HarnessRegressionScenarioResult> EvaluateAsync(
        HarnessRegressionContext context,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();

        var root = FindRepositoryRoot();
        if (root is null)
        {
            return ValueTask.FromResult(NotApplicable(
                "Repository docs tree was not found from this runtime location.",
                "Installed binaries may not include source documentation."));
        }

        var docPath = Path.Join(root, "docs", "HARNESS_REGRESSION.md");
        var indexPath = Path.Join(root, "docs", "README.md");
        var siteMapPath = Path.Join(root, "docs", "SITE_MAP.md");
        var missing = new List<string>();

        if (!File.Exists(docPath))
            missing.Add("docs/HARNESS_REGRESSION.md");
        if (!File.Exists(indexPath) || !File.ReadAllText(indexPath).Contains("HARNESS_REGRESSION.md", StringComparison.Ordinal))
            missing.Add("docs/README.md link");
        if (!File.Exists(siteMapPath) || !File.ReadAllText(siteMapPath).Contains("HARNESS_REGRESSION.md", StringComparison.Ordinal))
            missing.Add("docs/SITE_MAP.md link");

        return missing.Count == 0
            ? ValueTask.FromResult(Passed("Harness regression documentation is present and indexed."))
            : ValueTask.FromResult(Failed("Harness regression documentation is missing or not indexed.", Join(missing)));
    }

    private static string? FindRepositoryRoot()
    {
        foreach (var start in new[] { Directory.GetCurrentDirectory(), AppContext.BaseDirectory })
        {
            var candidate = new DirectoryInfo(start);
            while (candidate is not null)
            {
                if (File.Exists(Path.Join(candidate.FullName, "README.md")) &&
                    Directory.Exists(Path.Join(candidate.FullName, "docs")))
                {
                    return candidate.FullName;
                }

                candidate = candidate.Parent;
            }
        }

        return null;
    }
}

internal static class HarnessRegressionScenarioText
{
    public static string Join(IEnumerable<string> values)
        => string.Join(Environment.NewLine, values.Where(static value => !string.IsNullOrWhiteSpace(value)));
}

internal static class HarnessRegressionPaths
{
    public static string Child(string root, string childName)
    {
        var fullRoot = Path.GetFullPath(root);
        var fullChild = Path.GetFullPath(Path.Join(fullRoot, Path.GetFileName(childName)));
        var rootPrefix = fullRoot.EndsWith(Path.DirectorySeparatorChar)
            ? fullRoot
            : fullRoot + Path.DirectorySeparatorChar;

        if (!fullChild.StartsWith(rootPrefix, StringComparison.Ordinal))
            throw new InvalidOperationException($"Resolved harness path escaped the temp workspace: {childName}");

        return fullChild;
    }
}

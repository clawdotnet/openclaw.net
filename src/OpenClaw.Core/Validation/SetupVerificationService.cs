using System.Collections.Frozen;
using System.Net.Sockets;
using System.Text;
using OpenClaw.Core.Abstractions;
using OpenClaw.Core.Models;
using OpenClaw.Core.Plugins;
using OpenClaw.Core.Security;

namespace OpenClaw.Core.Validation;

public sealed class SetupVerificationRequest
{
    public required GatewayConfig Config { get; init; }
    public required GatewayRuntimeState RuntimeState { get; init; }
    public OrganizationPolicySnapshot? Policy { get; init; }
    public int OperatorAccountCount { get; init; }
    public bool Offline { get; init; }
    public bool RequireProvider { get; init; }
    public string? WorkspacePath { get; init; }
    public ModelSelectionDoctorResponse? ModelDoctor { get; init; }
    public IModelProfileRegistry? ModelProfiles { get; init; }
    public ProviderSmokeRegistry? ProviderSmokeRegistry { get; init; }
    public ConfigSourceDiagnostics? ConfigSources { get; init; }
}

public sealed class DoctorReportRequest
{
    public required GatewayConfig Config { get; init; }
    public required GatewayRuntimeState RuntimeState { get; init; }
    public OrganizationPolicySnapshot? Policy { get; init; }
    public int OperatorAccountCount { get; init; }
    public bool Offline { get; init; }
    public bool RequireProvider { get; init; }
    public bool CheckPortAvailability { get; init; }
    public string? WorkspacePath { get; init; }
    public ModelSelectionDoctorResponse? ModelDoctor { get; init; }
    public IModelProfileRegistry? ModelProfiles { get; init; }
    public ProviderSmokeRegistry? ProviderSmokeRegistry { get; init; }
    public ConfigSourceDiagnostics? ConfigSources { get; init; }
}

public static class SetupVerificationService
{
    private static readonly FrozenSet<string> SetupVerificationCheckIds = new[]
    {
        "config",
        "workspace",
        "security_posture",
        "browser_capability",
        "operator_readiness",
        "model_doctor",
        "provider_smoke"
    }.ToFrozenSet(StringComparer.Ordinal);

    public static async Task<SetupVerificationResponse> VerifyAsync(SetupVerificationRequest request, CancellationToken ct)
    {
        var report = await BuildDoctorReportAsync(new DoctorReportRequest
        {
            Config = request.Config,
            RuntimeState = request.RuntimeState,
            Policy = request.Policy,
            OperatorAccountCount = request.OperatorAccountCount,
            Offline = request.Offline,
            RequireProvider = request.RequireProvider,
            CheckPortAvailability = false,
            WorkspacePath = request.WorkspacePath,
            ModelDoctor = request.ModelDoctor,
            ModelProfiles = request.ModelProfiles,
            ProviderSmokeRegistry = request.ProviderSmokeRegistry,
            ConfigSources = request.ConfigSources
        }, ct);

        return BuildSetupVerificationResponse(report);
    }

    public static async Task<DoctorReportResponse> BuildDoctorReportAsync(DoctorReportRequest request, CancellationToken ct)
    {
        var config = request.Config;
        var publicBind = !BindAddressClassifier.IsLoopbackBind(config.BindAddress);
        var policy = request.Policy ?? new OrganizationPolicySnapshot();
        var workspacePath = ResolveConfiguredPath(request.WorkspacePath ?? config.Tooling.WorkspaceRoot);
        var browser = BrowserToolCapabilityEvaluator.Evaluate(config, request.RuntimeState);
        var modelDoctor = request.ModelDoctor ?? ModelDoctorEvaluator.Build(config, request.ModelProfiles);
        var checks = new List<DoctorCheckItem>
        {
            BuildRuntimeCheck(request.RuntimeState),
            BuildConfigCheck(config),
            BuildConfigSourceDiagnosticsCheck(request.ConfigSources),
            BuildPromptCacheCheck(config),
            BuildModelProfileConsistencyCheck(config),
            BuildWorkspaceCheck(config, workspacePath),
            BuildFilesystemRootPolicyCheck(config),
            BuildSecurityPostureCheck(config, publicBind),
            BuildBrowserCheck(browser),
            BuildOperatorReadinessCheck(publicBind, policy, request.OperatorAccountCount),
            BuildModelDoctorCheck(modelDoctor),
            BuildPluginRuntimeCheck(config, request.RuntimeState),
            BuildPluginDependencyCheck(config)
        };

        checks.AddRange(BuildMcpChecks(config));
        checks.AddRange(BuildChannelChecks(config));
        checks.AddRange(await BuildOpenSandboxChecksAsync(config, request.Offline, ct));
        checks.Add(BuildStorageWritableCheck(config));
        checks.Add(BuildStorageFreeSpaceCheck(config));

        if (request.CheckPortAvailability)
            checks.Add(await BuildPortAvailabilityCheckAsync(config));

        if (request.Offline)
        {
            checks.Add(new DoctorCheckItem
            {
                Id = "provider_smoke",
                Label = "Provider smoke",
                Category = DoctorCheckCategories.ProviderSmoke,
                Status = SetupCheckStates.Skip,
                Summary = "Provider smoke skipped because offline mode is enabled.",
                NextStep = "Re-run without --offline when network and credentials are available."
            });
        }
        else
        {
            var providerProbe = await ProviderSmokeProbe.ProbeAsync(config.Llm, request.ProviderSmokeRegistry, ct);
            checks.Add(BuildProviderSmokeCheck(providerProbe, request.RequireProvider));
        }

        var hasFailures = checks.Any(static item => item.Status == SetupCheckStates.Fail);
        var hasWarnings = checks.Any(static item => item.Status == SetupCheckStates.Warn);
        var hasSkips = checks.Any(static item => item.Status == SetupCheckStates.Skip);

        return new DoctorReportResponse
        {
            OverallStatus = hasFailures
                ? SetupCheckStates.Fail
                : hasWarnings
                    ? SetupCheckStates.Warn
                    : SetupCheckStates.Pass,
            HasFailures = hasFailures,
            HasWarnings = hasWarnings,
            HasSkips = hasSkips,
            Checks = checks,
            RecommendedNextActions = BuildRecommendedNextActions(checks)
        };
    }

    public static SetupVerificationResponse BuildSetupVerificationResponse(DoctorReportResponse report)
    {
        var checks = report.Checks
            .Where(item => SetupVerificationCheckIds.Contains(item.Id))
            .Select(static item => new SetupVerificationCheck
            {
                Id = item.Id,
                Label = item.Label,
                Category = item.Category,
                Status = item.Status,
                Summary = item.Summary,
                Detail = item.Detail,
                NextStep = item.NextStep
            })
            .ToArray();

        var hasFailures = checks.Any(static item => item.Status == SetupCheckStates.Fail);
        var hasWarnings = checks.Any(static item => item.Status == SetupCheckStates.Warn);
        var hasSkips = checks.Any(static item => item.Status == SetupCheckStates.Skip);

        return new SetupVerificationResponse
        {
            OverallStatus = hasFailures
                ? SetupCheckStates.Fail
                : hasWarnings
                    ? SetupCheckStates.Warn
                    : SetupCheckStates.Pass,
            HasFailures = hasFailures,
            HasWarnings = hasWarnings,
            HasSkips = hasSkips,
            Checks = checks,
            RecommendedNextActions = BuildRecommendedNextActions(checks)
        };
    }

    public static string RenderDoctorText(DoctorReportResponse report)
    {
        var sb = new StringBuilder();
        sb.AppendLine("OpenClaw.NET Doctor");
        sb.AppendLine($"- generated_at_utc: {report.GeneratedAtUtc:O}");
        sb.AppendLine($"- overall_status: {report.OverallStatus}");
        sb.AppendLine();

        WriteDoctorSection(sb, "Failed checks", report.Checks.Where(static item => item.Status == SetupCheckStates.Fail));
        WriteDoctorSection(sb, "Warnings", report.Checks.Where(static item => item.Status == SetupCheckStates.Warn));
        WriteDoctorSection(sb, "Skipped checks", report.Checks.Where(static item => item.Status == SetupCheckStates.Skip));
        WriteDoctorSection(sb, "Passing checks", report.Checks.Where(static item => item.Status == SetupCheckStates.Pass));

        if (report.RecommendedNextActions.Count > 0)
        {
            sb.AppendLine("Recommended next actions");
            foreach (var action in report.RecommendedNextActions)
                sb.AppendLine($"- {action}");
        }

        return sb.ToString().TrimEnd();
    }

    public static string GetModelDoctorStatus(ModelSelectionDoctorResponse? response)
    {
        if (response is null)
            return SetupCheckStates.Skip;
        if (response.Errors.Count > 0)
            return SetupCheckStates.Fail;
        if (response.Warnings.Count > 0)
            return SetupCheckStates.Warn;
        return SetupCheckStates.Pass;
    }

    public static ModelSelectionDoctorResponse EvaluateBasicModelDoctor(GatewayConfig config)
        => ModelDoctorEvaluator.Build(config);

    public static string GetBootstrapGuidanceState(bool publicBind, bool bootstrapTokenEnabled, int operatorAccountCount)
    {
        if (!publicBind)
            return "not_applicable";
        if (operatorAccountCount <= 0)
            return "create_first_operator";
        return bootstrapTokenEnabled ? "disable_recommended" : "complete";
    }

    public static IReadOnlyList<string> BuildRecommendedNextActions(
        GatewayConfig config,
        OrganizationPolicySnapshot policy,
        int operatorAccountCount,
        BrowserToolCapabilitySummary browser,
        bool workspaceExists,
        bool publicBind,
        ProviderSmokeRegistry? providerSmokeRegistry = null)
    {
        var actions = new List<string>();

        if (!workspaceExists)
            actions.Add("Create the configured workspace directory before using workspace-only file tools.");

        if (!ProviderSmokeProbe.IsProviderConfigured(config.Llm, providerSmokeRegistry))
            actions.Add("Resolve the configured provider credentials before running live chat turns.");

        if (browser.ConfiguredEnabled && !browser.Registered)
            actions.Add("Configure a non-local browser execution backend or disable the browser tool for this runtime.");

        if (publicBind && operatorAccountCount <= 0)
            actions.Add("Create a named admin operator account before exposing the admin UI publicly.");

        if (publicBind && policy.BootstrapTokenEnabled && operatorAccountCount > 0)
            actions.Add("Disable the bootstrap token after confirming account-token and browser-session login work.");

        if (publicBind)
            actions.Add("Put the gateway behind TLS termination and a reverse proxy before Internet exposure.");

        return actions.Distinct(StringComparer.Ordinal).ToArray();
    }

    public static string GetCheckStatus(SetupVerificationSnapshot? snapshot, string checkId)
    {
        if (snapshot?.Verification is null)
            return SetupCheckStates.NotRun;

        return snapshot.Verification.Checks
            .FirstOrDefault(item => string.Equals(item.Id, checkId, StringComparison.Ordinal))
            ?.Status
            ?? SetupCheckStates.NotRun;
    }

    private static void WriteDoctorSection(StringBuilder sb, string title, IEnumerable<DoctorCheckItem> checks)
    {
        var items = checks.ToArray();
        sb.AppendLine(title);
        if (items.Length == 0)
        {
            sb.AppendLine("- none");
            sb.AppendLine();
            return;
        }

        foreach (var check in items)
        {
            sb.AppendLine($"- [{check.Status}] {check.Category}/{check.Id}: {check.Label} -> {check.Summary}");
            if (!string.IsNullOrWhiteSpace(check.Detail))
                sb.AppendLine($"  detail: {check.Detail}");
            if (!string.IsNullOrWhiteSpace(check.NextStep))
                sb.AppendLine($"  next_step: {check.NextStep}");
        }

        sb.AppendLine();
    }

    private static DoctorCheckItem BuildRuntimeCheck(GatewayRuntimeState runtimeState)
        => new()
        {
            Id = "runtime_mode",
            Label = "Runtime mode",
            Category = DoctorCheckCategories.Runtime,
            Status = SetupCheckStates.Pass,
            Summary = $"requested={runtimeState.RequestedMode}, effective={runtimeState.EffectiveModeName}, dynamic_code_supported={runtimeState.DynamicCodeSupported}"
        };

    private static DoctorCheckItem BuildConfigCheck(GatewayConfig config)
    {
        var errors = ConfigValidator.Validate(config);
        if (errors.Count == 0)
        {
            return new DoctorCheckItem
            {
                Id = "config",
                Label = "Config and static validation",
                Category = DoctorCheckCategories.Config,
                Status = SetupCheckStates.Pass,
                Summary = "Static configuration validation passed."
            };
        }

        return new DoctorCheckItem
        {
            Id = "config",
            Label = "Config and static validation",
            Category = DoctorCheckCategories.Config,
            Status = SetupCheckStates.Fail,
            Summary = $"Static configuration validation found {errors.Count} issue(s).",
            Detail = string.Join(Environment.NewLine, errors.Take(8).Select(static item => $"- {item}")),
            NextStep = "Fix the configuration validation errors and rerun doctor or setup verify."
        };
    }

    private static DoctorCheckItem BuildConfigSourceDiagnosticsCheck(ConfigSourceDiagnostics? diagnostics)
    {
        if (diagnostics is null || diagnostics.Items.Count == 0)
        {
            return new DoctorCheckItem
            {
                Id = "config_sources",
                Label = "Effective configuration sources",
                Category = DoctorCheckCategories.Config,
                Status = SetupCheckStates.Skip,
                Summary = "Configuration source diagnostics were not supplied."
            };
        }

        return new DoctorCheckItem
        {
            Id = "config_sources",
            Label = "Effective configuration sources",
            Category = DoctorCheckCategories.Config,
            Status = SetupCheckStates.Pass,
            Summary = "Effective bind, storage, provider, and secret-source winners are listed.",
            Detail = string.Join(Environment.NewLine, diagnostics.Items.Select(static item =>
                $"- {item.Label}: {FormatConfigDiagnosticValue(item)} (source: {item.Source})"))
        };
    }

    private static string FormatConfigDiagnosticValue(ConfigSourceDiagnosticItem item)
        => item.Redacted ? "configured (redacted)" : item.EffectiveValue;

    private static DoctorCheckItem BuildPromptCacheCheck(GatewayConfig config)
        => HasValidPromptCacheConfiguration(config)
            ? new DoctorCheckItem
            {
                Id = "prompt_cache",
                Label = "Prompt cache compatibility",
                Category = DoctorCheckCategories.Config,
                Status = SetupCheckStates.Pass,
                Summary = "Prompt cache settings are compatible with the configured providers."
            }
            : new DoctorCheckItem
            {
                Id = "prompt_cache",
                Label = "Prompt cache compatibility",
                Category = DoctorCheckCategories.Config,
                Status = SetupCheckStates.Warn,
                Summary = "Prompt cache settings are not compatible with the configured providers.",
                Detail = "OpenAI-compatible and dynamic providers require an explicit cache dialect. Keep-warm is only supported for Anthropic-family and Gemini profiles.",
                NextStep = "Set an explicit prompt-cache dialect or disable unsupported keep-warm settings."
            };

    private static DoctorCheckItem BuildModelProfileConsistencyCheck(GatewayConfig config)
        => HasValidModelProfileConfiguration(config)
            ? new DoctorCheckItem
            {
                Id = "model_profile_configuration",
                Label = "Model profile configuration",
                Category = DoctorCheckCategories.ModelDoctor,
                Status = SetupCheckStates.Pass,
                Summary = "Model profile references are internally consistent."
            }
            : new DoctorCheckItem
            {
                Id = "model_profile_configuration",
                Label = "Model profile configuration",
                Category = DoctorCheckCategories.ModelDoctor,
                Status = SetupCheckStates.Fail,
                Summary = "Model profile configuration is internally inconsistent.",
                Detail = "Check Models.DefaultProfile, duplicate profile ids, route profile references, and fallback profile references.",
                NextStep = "Fix profile ids and fallback references before starting chat traffic."
            };

    private static DoctorCheckItem BuildWorkspaceCheck(GatewayConfig config, string? workspacePath)
    {
        if (!config.Tooling.WorkspaceOnly)
        {
            return new DoctorCheckItem
            {
                Id = "workspace",
                Label = "Workspace readiness",
                Category = DoctorCheckCategories.Workspace,
                Status = SetupCheckStates.Warn,
                Summary = "Workspace-only mode is disabled, so filesystem access is broader than the first-run defaults.",
                Detail = "Re-enable Tooling.WorkspaceOnly if you want the safer local-first defaults."
            };
        }

        if (!string.IsNullOrWhiteSpace(workspacePath) && Path.IsPathRooted(workspacePath) && Directory.Exists(workspacePath))
        {
            return new DoctorCheckItem
            {
                Id = "workspace",
                Label = "Workspace readiness",
                Category = DoctorCheckCategories.Workspace,
                Status = SetupCheckStates.Pass,
                Summary = $"Workspace is ready at '{workspacePath}'."
            };
        }

        return new DoctorCheckItem
        {
            Id = "workspace",
            Label = "Workspace readiness",
            Category = DoctorCheckCategories.Workspace,
            Status = SetupCheckStates.Fail,
            Summary = "The configured workspace directory does not exist.",
            Detail = workspacePath,
            NextStep = "Create the workspace directory or update Tooling.WorkspaceRoot."
        };
    }

    private static DoctorCheckItem BuildFilesystemRootPolicyCheck(GatewayConfig config)
        => HasValidRootSet(config.Tooling.AllowedReadRoots) && HasValidRootSet(config.Tooling.AllowedWriteRoots)
            ? new DoctorCheckItem
            {
                Id = "filesystem_root_policy",
                Label = "Filesystem root policy",
                Category = DoctorCheckCategories.Workspace,
                Status = SetupCheckStates.Pass,
                Summary = "Filesystem roots are well-formed."
            }
            : new DoctorCheckItem
            {
                Id = "filesystem_root_policy",
                Label = "Filesystem root policy",
                Category = DoctorCheckCategories.Workspace,
                Status = SetupCheckStates.Fail,
                Summary = "Filesystem root policy is not well-formed.",
                Detail = "Do not mix '*' with explicit roots, and use absolute paths for explicit filesystem roots.",
                NextStep = "Fix AllowedReadRoots/AllowedWriteRoots before exposing filesystem tools."
            };

    private static DoctorCheckItem BuildSecurityPostureCheck(GatewayConfig config, bool publicBind)
    {
        var issues = new List<string>();
        var warnings = new List<string>();

        if (publicBind && string.IsNullOrWhiteSpace(config.AuthToken))
            issues.Add("Public bind is enabled without an auth token.");
        if (publicBind && !config.Security.RequireRequesterMatchForHttpToolApproval)
            warnings.Add("Requester-matched HTTP tool approvals are disabled.");
        if (publicBind && config.Canvas.Enabled && !config.Canvas.AllowOnPublicBind)
            issues.Add("Canvas command forwarding is enabled on a public bind without Canvas.AllowOnPublicBind.");
        if (publicBind && !config.Security.TrustForwardedHeaders)
            warnings.Add("Forwarded headers are not trusted, so browser session cookies may not be marked secure behind TLS termination.");
        if (publicBind &&
            config.Tooling.AllowShell &&
            !ToolSandboxPolicy.IsRequireSandboxed(config, "shell", ToolSandboxMode.Prefer))
        {
            warnings.Add("Shell is enabled on a public bind without required sandboxing.");
        }

        if (issues.Count > 0)
        {
            return new DoctorCheckItem
            {
                Id = "security_posture",
                Label = "Security posture",
                Category = DoctorCheckCategories.Security,
                Status = SetupCheckStates.Fail,
                Summary = string.Join(" ", issues),
                Detail = warnings.Count == 0 ? null : string.Join(Environment.NewLine, warnings.Select(static item => $"- {item}")),
                NextStep = "Fix the public-bind security issues before treating the deployment as ready."
            };
        }

        if (warnings.Count > 0)
        {
            return new DoctorCheckItem
            {
                Id = "security_posture",
                Label = "Security posture",
                Category = DoctorCheckCategories.Security,
                Status = SetupCheckStates.Warn,
                Summary = warnings[0],
                Detail = string.Join(Environment.NewLine, warnings.Select(static item => $"- {item}"))
            };
        }

        return new DoctorCheckItem
        {
            Id = "security_posture",
            Label = "Security posture",
            Category = DoctorCheckCategories.Security,
            Status = SetupCheckStates.Pass,
            Summary = publicBind
                ? "Public-bind guardrails are in place."
                : "Loopback mode keeps the first-run surface local."
        };
    }

    private static DoctorCheckItem BuildBrowserCheck(BrowserToolCapabilitySummary browser)
    {
        if (!browser.ConfiguredEnabled)
        {
            return new DoctorCheckItem
            {
                Id = "browser_capability",
                Label = "Browser capability",
                Category = DoctorCheckCategories.Browser,
                Status = SetupCheckStates.Pass,
                Summary = "Browser tool is disabled.",
                Detail = BuildBrowserCapabilityDetail(browser)
            };
        }

        if (browser.Registered)
        {
            return new DoctorCheckItem
            {
                Id = "browser_capability",
                Label = "Browser capability",
                Category = DoctorCheckCategories.Browser,
                Status = SetupCheckStates.Pass,
                Summary = browser.ExecutionBackendConfigured && !browser.LocalExecutionSupported
                    ? "Browser tool is available through a configured execution backend."
                    : "Browser tool is available in this runtime.",
                Detail = BuildBrowserCapabilityDetail(browser)
            };
        }

        return new DoctorCheckItem
        {
            Id = "browser_capability",
            Label = "Browser capability",
            Category = DoctorCheckCategories.Browser,
            Status = SetupCheckStates.Fail,
            Summary = "Browser tool is enabled but unavailable in this runtime.",
            Detail = $"{BuildBrowserCapabilityDetail(browser)}{Environment.NewLine}Configure a non-local execution backend or sandbox for the browser tool, or disable Tooling.EnableBrowserTool.",
            NextStep = "Configure a non-local execution backend or sandbox for the browser tool, or disable Tooling.EnableBrowserTool."
        };
    }

    private static DoctorCheckItem BuildOperatorReadinessCheck(bool publicBind, OrganizationPolicySnapshot policy, int operatorAccountCount)
    {
        if (!publicBind)
        {
            return new DoctorCheckItem
            {
                Id = "operator_readiness",
                Label = "Operator readiness",
                Category = DoctorCheckCategories.Operator,
                Status = SetupCheckStates.Pass,
                Summary = "Loopback mode does not require named operator accounts for first run."
            };
        }

        if (operatorAccountCount <= 0)
        {
            return new DoctorCheckItem
            {
                Id = "operator_readiness",
                Label = "Operator readiness",
                Category = DoctorCheckCategories.Operator,
                Status = SetupCheckStates.Warn,
                Summary = "No named operator accounts exist yet.",
                Detail = "Create an admin account, sign in with a browser session, then retire the bootstrap token.",
                NextStep = "Use the admin UI wizard to create the first admin operator account."
            };
        }

        if (policy.BootstrapTokenEnabled)
        {
            return new DoctorCheckItem
            {
                Id = "operator_readiness",
                Label = "Operator readiness",
                Category = DoctorCheckCategories.Operator,
                Status = SetupCheckStates.Warn,
                Summary = "Bootstrap token is still enabled after operator accounts were created.",
                Detail = "Keep it only until account-token and browser-session authentication are confirmed.",
                NextStep = "Disable the bootstrap token from the admin policy once browser sign-in works."
            };
        }

        return new DoctorCheckItem
        {
            Id = "operator_readiness",
            Label = "Operator readiness",
            Category = DoctorCheckCategories.Operator,
            Status = SetupCheckStates.Pass,
            Summary = $"Operator accounts are configured ({operatorAccountCount})."
        };
    }

    private static DoctorCheckItem BuildModelDoctorCheck(ModelSelectionDoctorResponse response)
    {
        var status = GetModelDoctorStatus(response);
        if (status == SetupCheckStates.Fail)
        {
            return new DoctorCheckItem
            {
                Id = "model_doctor",
                Label = "Model doctor",
                Category = DoctorCheckCategories.ModelDoctor,
                Status = SetupCheckStates.Fail,
                Summary = $"Model doctor reported {response.Errors.Count} error(s).",
                Detail = string.Join(Environment.NewLine, response.Errors.Take(8).Select(static item => $"- {item}")),
                NextStep = "Fix the model/provider configuration before using chat surfaces."
            };
        }

        if (status == SetupCheckStates.Warn)
        {
            return new DoctorCheckItem
            {
                Id = "model_doctor",
                Label = "Model doctor",
                Category = DoctorCheckCategories.ModelDoctor,
                Status = SetupCheckStates.Warn,
                Summary = $"Model doctor reported {response.Warnings.Count} warning(s).",
                Detail = string.Join(Environment.NewLine, response.Warnings.Take(8).Select(static item => $"- {item}"))
            };
        }

        return new DoctorCheckItem
        {
            Id = "model_doctor",
            Label = "Model doctor",
            Category = DoctorCheckCategories.ModelDoctor,
            Status = SetupCheckStates.Pass,
            Summary = "Model doctor did not find blocking configuration issues."
        };
    }

    private static DoctorCheckItem BuildProviderSmokeCheck(ProviderSmokeProbeResult result, bool requireProvider)
    {
        if (result.Status == SetupCheckStates.Skip && requireProvider)
        {
            return new DoctorCheckItem
            {
                Id = "provider_smoke",
                Label = "Provider smoke",
                Category = DoctorCheckCategories.ProviderSmoke,
                Status = SetupCheckStates.Fail,
                Summary = "Provider smoke was required but could not be completed.",
                Detail = $"{result.Summary}{(string.IsNullOrWhiteSpace(result.Detail) ? string.Empty : $"{Environment.NewLine}{result.Detail}")}",
                NextStep = "Ensure credentials and network access are available, then rerun setup verify --require-provider."
            };
        }

        return new DoctorCheckItem
        {
            Id = "provider_smoke",
            Label = "Provider smoke",
            Category = DoctorCheckCategories.ProviderSmoke,
            Status = result.Status,
            Summary = result.Summary,
            Detail = result.Detail,
            NextStep = result.Status == SetupCheckStates.Fail
                ? "Fix provider credentials/model/endpoint settings and rerun setup verify."
                : null
        };
    }

    private static DoctorCheckItem BuildPluginRuntimeCheck(GatewayConfig config, GatewayRuntimeState runtimeState)
    {
        if (!config.Plugins.DynamicNative.Enabled)
        {
            return new DoctorCheckItem
            {
                Id = "plugin_runtime",
                Label = "Plugin runtime mode",
                Category = DoctorCheckCategories.Plugins,
                Status = SetupCheckStates.Pass,
                Summary = "Dynamic native plugins are disabled."
            };
        }

        return runtimeState.EffectiveMode == GatewayRuntimeMode.Jit
            ? new DoctorCheckItem
            {
                Id = "plugin_runtime",
                Label = "Plugin runtime mode",
                Category = DoctorCheckCategories.Plugins,
                Status = SetupCheckStates.Pass,
                Summary = "Dynamic native plugins are enabled with a JIT-capable runtime."
            }
            : new DoctorCheckItem
            {
                Id = "plugin_runtime",
                Label = "Plugin runtime mode",
                Category = DoctorCheckCategories.Plugins,
                Status = SetupCheckStates.Fail,
                Summary = "Dynamic native plugins require JIT mode.",
                Detail = "Disable OpenClaw:Plugins:DynamicNative:Enabled or run a JIT-capable artifact / mode.",
                NextStep = "Disable dynamic native plugins or switch to a JIT-capable runtime mode."
            };
    }

    private static DoctorCheckItem BuildPluginDependencyCheck(GatewayConfig config)
    {
        if (!config.Plugins.Enabled)
        {
            return new DoctorCheckItem
            {
                Id = "plugin_bridge_dependency",
                Label = "Bridge plugin host dependency",
                Category = DoctorCheckCategories.Plugins,
                Status = SetupCheckStates.Pass,
                Summary = "Bridge plugins are disabled."
            };
        }

        return IsNodeAvailable()
            ? new DoctorCheckItem
            {
                Id = "plugin_bridge_dependency",
                Label = "Bridge plugin host dependency",
                Category = DoctorCheckCategories.Plugins,
                Status = SetupCheckStates.Pass,
                Summary = "Node.js is available for bridge plugins."
            }
            : new DoctorCheckItem
            {
                Id = "plugin_bridge_dependency",
                Label = "Bridge plugin host dependency",
                Category = DoctorCheckCategories.Plugins,
                Status = SetupCheckStates.Warn,
                Summary = "Node.js is not available for bridge plugins.",
                Detail = "Install Node.js or disable bridge plugins.",
                NextStep = "Install Node.js or disable bridge plugins."
            };
    }

    private static IEnumerable<DoctorCheckItem> BuildMcpChecks(GatewayConfig config)
    {
        if (!config.Plugins.Mcp.Enabled)
        {
            yield return new DoctorCheckItem
            {
                Id = "mcp_servers",
                Label = "MCP servers",
                Category = DoctorCheckCategories.Plugins,
                Status = SetupCheckStates.Pass,
                Summary = "MCP servers are disabled."
            };
            yield break;
        }

        foreach (var (serverId, server) in config.Plugins.Mcp.Servers)
        {
            if (!server.Enabled)
                continue;

            var transport = server.NormalizeTransport();
            if (transport == "http")
            {
                yield return Uri.TryCreate(server.Url, UriKind.Absolute, out _)
                    ? new DoctorCheckItem
                    {
                        Id = $"mcp_server_{serverId}",
                        Label = $"MCP server '{serverId}'",
                        Category = DoctorCheckCategories.Plugins,
                        Status = SetupCheckStates.Pass,
                        Summary = "HTTP MCP server URL is configured."
                    }
                    : new DoctorCheckItem
                    {
                        Id = $"mcp_server_{serverId}",
                        Label = $"MCP server '{serverId}'",
                        Category = DoctorCheckCategories.Plugins,
                        Status = SetupCheckStates.Warn,
                        Summary = "HTTP MCP server URL is missing or invalid.",
                        Detail = "Set a valid absolute HTTP(S) URL for the MCP server.",
                        NextStep = $"Fix MCP server '{serverId}' URL or disable the server."
                    };
            }
            else
            {
                yield return !string.IsNullOrWhiteSpace(server.Command)
                    ? new DoctorCheckItem
                    {
                        Id = $"mcp_server_{serverId}",
                        Label = $"MCP server '{serverId}'",
                        Category = DoctorCheckCategories.Plugins,
                        Status = SetupCheckStates.Pass,
                        Summary = "MCP server command is configured."
                    }
                    : new DoctorCheckItem
                    {
                        Id = $"mcp_server_{serverId}",
                        Label = $"MCP server '{serverId}'",
                        Category = DoctorCheckCategories.Plugins,
                        Status = SetupCheckStates.Warn,
                        Summary = "MCP server command is missing.",
                        Detail = "Set Plugins:Mcp:Servers:<id>:Command or disable the server.",
                        NextStep = $"Fix MCP server '{serverId}' command or disable the server."
                    };
            }
        }
    }

    private static IEnumerable<DoctorCheckItem> BuildChannelChecks(GatewayConfig config)
    {
        if (config.Plugins.Native.Mqtt.Enabled)
        {
            yield return !string.IsNullOrWhiteSpace(config.Plugins.Native.Mqtt.Host) && config.Plugins.Native.Mqtt.Port > 0
                ? new DoctorCheckItem
                {
                    Id = "mqtt_integration",
                    Label = "MQTT integration",
                    Category = DoctorCheckCategories.Channels,
                    Status = SetupCheckStates.Pass,
                    Summary = "MQTT integration host and port are configured."
                }
                : new DoctorCheckItem
                {
                    Id = "mqtt_integration",
                    Label = "MQTT integration",
                    Category = DoctorCheckCategories.Channels,
                    Status = SetupCheckStates.Warn,
                    Summary = "MQTT integration host or port is missing.",
                    Detail = "Set Plugins:Native:Mqtt:Host and a valid Port, or disable MQTT.",
                    NextStep = "Configure MQTT host/port or disable MQTT."
                };
        }

        if (config.Plugins.Native.Email.Enabled)
        {
            yield return !string.IsNullOrWhiteSpace(config.Plugins.Native.Email.SmtpHost) && config.Plugins.Native.Email.SmtpPort > 0
                ? new DoctorCheckItem
                {
                    Id = "email_smtp",
                    Label = "Email SMTP integration",
                    Category = DoctorCheckCategories.Channels,
                    Status = SetupCheckStates.Pass,
                    Summary = "Email SMTP settings are configured."
                }
                : new DoctorCheckItem
                {
                    Id = "email_smtp",
                    Label = "Email SMTP integration",
                    Category = DoctorCheckCategories.Channels,
                    Status = SetupCheckStates.Warn,
                    Summary = "Email SMTP settings are incomplete.",
                    Detail = "Set Plugins:Native:Email:SmtpHost/SmtpPort for outbound mail.",
                    NextStep = "Configure SMTP settings or disable the email integration."
                };

            yield return !string.IsNullOrWhiteSpace(config.Plugins.Native.Email.ImapHost) && config.Plugins.Native.Email.ImapPort > 0
                ? new DoctorCheckItem
                {
                    Id = "email_imap",
                    Label = "Email IMAP integration",
                    Category = DoctorCheckCategories.Channels,
                    Status = SetupCheckStates.Pass,
                    Summary = "Email IMAP settings are configured."
                }
                : new DoctorCheckItem
                {
                    Id = "email_imap",
                    Label = "Email IMAP integration",
                    Category = DoctorCheckCategories.Channels,
                    Status = SetupCheckStates.Warn,
                    Summary = "Email IMAP settings are incomplete.",
                    Detail = "Set Plugins:Native:Email:ImapHost/ImapPort for inbox monitoring.",
                    NextStep = "Configure IMAP settings or disable the email integration."
                };
        }

        if (config.Channels.Sms.Twilio.Enabled)
        {
            yield return !string.IsNullOrWhiteSpace(config.Channels.Sms.Twilio.AccountSid) &&
                !string.IsNullOrWhiteSpace(config.Channels.Sms.Twilio.AuthTokenRef)
                ? new DoctorCheckItem
                {
                    Id = "twilio_sms",
                    Label = "Twilio SMS channel",
                    Category = DoctorCheckCategories.Channels,
                    Status = SetupCheckStates.Pass,
                    Summary = "Twilio account SID and token reference are configured."
                }
                : new DoctorCheckItem
                {
                    Id = "twilio_sms",
                    Label = "Twilio SMS channel",
                    Category = DoctorCheckCategories.Channels,
                    Status = SetupCheckStates.Warn,
                    Summary = "Twilio SMS credentials are incomplete.",
                    NextStep = "Set Twilio AccountSid/AuthTokenRef or disable the SMS channel."
                };
        }

        if (config.Channels.Telegram.Enabled)
        {
            yield return !string.IsNullOrWhiteSpace(config.Channels.Telegram.BotTokenRef) ||
                !string.IsNullOrWhiteSpace(config.Channels.Telegram.BotToken)
                ? new DoctorCheckItem
                {
                    Id = "telegram_channel",
                    Label = "Telegram channel",
                    Category = DoctorCheckCategories.Channels,
                    Status = SetupCheckStates.Pass,
                    Summary = "Telegram bot token is configured."
                }
                : new DoctorCheckItem
                {
                    Id = "telegram_channel",
                    Label = "Telegram channel",
                    Category = DoctorCheckCategories.Channels,
                    Status = SetupCheckStates.Warn,
                    Summary = "Telegram bot token is missing.",
                    NextStep = "Set BotToken or BotTokenRef, or disable Telegram."
                };
        }

        var allowlistLines = new List<string>();
        AppendAllowlistLine(allowlistLines, "teams", config.Channels.Teams.AllowedFromIds);
        AppendAllowlistLine(allowlistLines, "slack", config.Channels.Slack.AllowedFromUserIds);
        AppendAllowlistLine(allowlistLines, "discord", config.Channels.Discord.AllowedFromUserIds);
        AppendAllowlistLine(allowlistLines, "signal", config.Channels.Signal.AllowedFromNumbers);
        AppendAllowlistLine(allowlistLines, "telegram", config.Channels.Telegram.AllowedFromUserIds);
        AppendAllowlistLine(allowlistLines, "whatsapp", config.Channels.WhatsApp.AllowedFromIds);
        AppendAllowlistLine(allowlistLines, "sms", config.Channels.Sms.Twilio.AllowedFromNumbers);

        if (allowlistLines.Count > 0)
        {
            yield return new DoctorCheckItem
            {
                Id = "channel_allowlists",
                Label = "Configured channel allowlists",
                Category = DoctorCheckCategories.Channels,
                Status = SetupCheckStates.Pass,
                Summary = "Static sender allowlists are configured for one or more channels.",
                Detail = string.Join(Environment.NewLine, allowlistLines)
            };
        }
    }

    private static async Task<IReadOnlyList<DoctorCheckItem>> BuildOpenSandboxChecksAsync(
        GatewayConfig config,
        bool offline,
        CancellationToken ct)
    {
        if (!ToolSandboxPolicy.IsOpenSandboxProviderConfigured(config))
        {
            return
            [
                new DoctorCheckItem
                {
                    Id = "opensandbox",
                    Label = "OpenSandbox connectivity",
                    Category = DoctorCheckCategories.Network,
                    Status = SetupCheckStates.Skip,
                    Summary = "OpenSandbox is not configured."
                }
            ];
        }

        var checks = new List<DoctorCheckItem>
        {
            Uri.TryCreate(config.Sandbox.Endpoint, UriKind.Absolute, out _)
                ? new DoctorCheckItem
                {
                    Id = "opensandbox_endpoint",
                    Label = "OpenSandbox endpoint",
                    Category = DoctorCheckCategories.Network,
                    Status = SetupCheckStates.Pass,
                    Summary = "OpenSandbox endpoint is configured."
                }
                : new DoctorCheckItem
                {
                    Id = "opensandbox_endpoint",
                    Label = "OpenSandbox endpoint",
                    Category = DoctorCheckCategories.Network,
                    Status = SetupCheckStates.Fail,
                    Summary = "OpenSandbox endpoint is missing or invalid.",
                    NextStep = "Set OpenClaw:Sandbox:Endpoint to a valid absolute URL."
                }
        };

        if (!Uri.TryCreate(config.Sandbox.Endpoint, UriKind.Absolute, out _))
            return checks;

        if (offline)
        {
            checks.Add(new DoctorCheckItem
            {
                Id = "opensandbox_reachability",
                Label = "OpenSandbox reachability",
                Category = DoctorCheckCategories.Network,
                Status = SetupCheckStates.Skip,
                Summary = "OpenSandbox reachability was skipped because offline mode is enabled."
            });
            return checks;
        }

        checks.Add(await PingOpenSandboxAsync(config, ct)
            ? new DoctorCheckItem
            {
                Id = "opensandbox_reachability",
                Label = "OpenSandbox reachability",
                Category = DoctorCheckCategories.Network,
                Status = SetupCheckStates.Pass,
                Summary = "OpenSandbox endpoint is reachable."
            }
            : new DoctorCheckItem
            {
                Id = "opensandbox_reachability",
                Label = "OpenSandbox reachability",
                Category = DoctorCheckCategories.Network,
                Status = SetupCheckStates.Fail,
                Summary = "OpenSandbox endpoint is not reachable.",
                Detail = "Verify OpenClaw:Sandbox:Endpoint and the OpenSandbox service/API key.",
                NextStep = "Fix the OpenSandbox endpoint or credentials before relying on sandbox-required tools."
            });
        return checks;
    }

    private static DoctorCheckItem BuildStorageWritableCheck(GatewayConfig config)
    {
        try
        {
            Directory.CreateDirectory(config.Memory.StoragePath);
            var testFile = Path.Combine(config.Memory.StoragePath, ".doctor-test");
            File.WriteAllText(testFile, "test");
            File.Delete(testFile);
            return new DoctorCheckItem
            {
                Id = "storage_writable",
                Label = "Storage path writability",
                Category = DoctorCheckCategories.Storage,
                Status = SetupCheckStates.Pass,
                Summary = "Storage path exists and is writable."
            };
        }
        catch
        {
            return new DoctorCheckItem
            {
                Id = "storage_writable",
                Label = "Storage path writability",
                Category = DoctorCheckCategories.Storage,
                Status = SetupCheckStates.Fail,
                Summary = "Storage path is not writable.",
                NextStep = "Fix Memory.StoragePath permissions before starting the gateway."
            };
        }
    }

    private static DoctorCheckItem BuildStorageFreeSpaceCheck(GatewayConfig config)
    {
        try
        {
            var root = Path.GetPathRoot(Path.GetFullPath(config.Memory.StoragePath));
            if (string.IsNullOrWhiteSpace(root))
            {
                return new DoctorCheckItem
                {
                    Id = "storage_free_space",
                    Label = "Storage free space",
                    Category = DoctorCheckCategories.Storage,
                    Status = SetupCheckStates.Pass,
                    Summary = "Storage volume could not be resolved; skipping low-space warning."
                };
            }

            var drive = new DriveInfo(root);
            return drive.AvailableFreeSpace > 100L * 1024L * 1024L
                ? new DoctorCheckItem
                {
                    Id = "storage_free_space",
                    Label = "Storage free space",
                    Category = DoctorCheckCategories.Storage,
                    Status = SetupCheckStates.Pass,
                    Summary = "Storage volume has sufficient free space."
                }
                : new DoctorCheckItem
                {
                    Id = "storage_free_space",
                    Label = "Storage free space",
                    Category = DoctorCheckCategories.Storage,
                    Status = SetupCheckStates.Warn,
                    Summary = "Storage volume has less than 100 MB free space.",
                    NextStep = "Free disk space before running heavier workloads."
                };
        }
        catch
        {
            return new DoctorCheckItem
            {
                Id = "storage_free_space",
                Label = "Storage free space",
                Category = DoctorCheckCategories.Storage,
                Status = SetupCheckStates.Skip,
                Summary = "Storage free space could not be determined."
            };
        }
    }

    private static async Task<DoctorCheckItem> BuildPortAvailabilityCheckAsync(GatewayConfig config)
    {
        try
        {
            using var tcpClient = new TcpClient();
            await tcpClient.ConnectAsync(NormalizePortProbeHost(config.BindAddress), config.Port);
            return new DoctorCheckItem
            {
                Id = "port_availability",
                Label = "TCP port availability",
                Category = DoctorCheckCategories.Network,
                Status = SetupCheckStates.Fail,
                Summary = "The configured TCP port is already in use.",
                NextStep = "Free the port or change OpenClaw:Port before launching the gateway."
            };
        }
        catch (SocketException)
        {
            return new DoctorCheckItem
            {
                Id = "port_availability",
                Label = "TCP port availability",
                Category = DoctorCheckCategories.Network,
                Status = SetupCheckStates.Pass,
                Summary = "The configured TCP port is available."
            };
        }
        catch (Exception ex)
        {
            return new DoctorCheckItem
            {
                Id = "port_availability",
                Label = "TCP port availability",
                Category = DoctorCheckCategories.Network,
                Status = SetupCheckStates.Skip,
                Summary = "TCP port availability could not be determined.",
                Detail = ex.Message
            };
        }
    }

    private static IReadOnlyList<string> BuildRecommendedNextActions(IReadOnlyList<DoctorCheckItem> checks)
        => checks
            .Where(static item => item.Status is SetupCheckStates.Fail or SetupCheckStates.Warn)
            .Select(static item => item.NextStep)
            .Where(static item => !string.IsNullOrWhiteSpace(item))
            .Cast<string>()
            .Distinct(StringComparer.Ordinal)
            .ToArray();

    private static IReadOnlyList<string> BuildRecommendedNextActions(IReadOnlyList<SetupVerificationCheck> checks)
        => checks
            .Where(static item => item.Status is SetupCheckStates.Fail or SetupCheckStates.Warn)
            .Select(static item => item.NextStep)
            .Where(static item => !string.IsNullOrWhiteSpace(item))
            .Cast<string>()
            .Distinct(StringComparer.Ordinal)
            .ToArray();

    private static string BuildBrowserCapabilityDetail(BrowserToolCapabilitySummary browser)
        => $"- browser_tool: configured={ToBoolWord(browser.ConfiguredEnabled)} registered={ToBoolWord(browser.Registered)} local_supported={ToBoolWord(browser.LocalExecutionSupported)} backend_configured={ToBoolWord(browser.ExecutionBackendConfigured)}";

    private static void AppendAllowlistLine(List<string> lines, string channelId, IReadOnlyList<string> values)
    {
        if (values.Count == 0)
            return;

        lines.Add($"- {channelId}: {string.Join(", ", values)}");
    }

    private static string ToBoolWord(bool value) => value ? "yes" : "no";

    private static string NormalizePortProbeHost(string? bindAddress)
    {
        if (string.IsNullOrWhiteSpace(bindAddress))
            return "127.0.0.1";

        return bindAddress.Trim() switch
        {
            "0.0.0.0" => "127.0.0.1",
            "::" or "[::]" => "::1",
            _ => bindAddress
        };
    }

    private static bool HasValidPromptCacheConfiguration(GatewayConfig config)
    {
        static bool RequiresExplicitDialect(string provider)
            => provider.Equals("openai-compatible", StringComparison.OrdinalIgnoreCase)
                || provider.Equals("groq", StringComparison.OrdinalIgnoreCase)
                || provider.Equals("together", StringComparison.OrdinalIgnoreCase)
                || provider.Equals("lmstudio", StringComparison.OrdinalIgnoreCase);

        static bool SupportsKeepWarm(string provider, string dialect)
            => (dialect.Equals("anthropic", StringComparison.OrdinalIgnoreCase) &&
                (provider.Equals("anthropic", StringComparison.OrdinalIgnoreCase)
                 || provider.Equals("claude", StringComparison.OrdinalIgnoreCase)
                 || provider.Equals("anthropic-vertex", StringComparison.OrdinalIgnoreCase)
                 || provider.Equals("amazon-bedrock", StringComparison.OrdinalIgnoreCase)))
               || (dialect.Equals("gemini", StringComparison.OrdinalIgnoreCase) &&
                   (provider.Equals("gemini", StringComparison.OrdinalIgnoreCase)
                    || provider.Equals("google", StringComparison.OrdinalIgnoreCase)));

        static bool IsValid(string provider, PromptCachingConfig? caching)
        {
            if (caching is null || caching.Enabled != true)
                return true;

            var dialect = (caching.Dialect ?? "auto").Trim();
            if (RequiresExplicitDialect(provider) && dialect.Equals("auto", StringComparison.OrdinalIgnoreCase))
                return false;

            return caching.KeepWarmEnabled != true || SupportsKeepWarm(provider, dialect);
        }

        if (!IsValid(config.Llm.Provider, config.Llm.PromptCaching))
            return false;

        foreach (var profile in config.Models.Profiles)
        {
            if (!IsValid(profile.Provider, profile.PromptCaching))
                return false;
        }

        return true;
    }

    private static bool HasValidRootSet(string[] roots)
    {
        var wildcardCount = roots.Count(static root => string.Equals(root, "*", StringComparison.Ordinal));
        if (wildcardCount > 0 && roots.Length > wildcardCount)
            return false;

        foreach (var root in roots)
        {
            if (string.Equals(root, "*", StringComparison.Ordinal))
                continue;

            var resolved = ResolveConfiguredPath(root);
            if (string.IsNullOrWhiteSpace(resolved) || !Path.IsPathRooted(resolved))
                return false;
        }

        return true;
    }

    private static bool HasValidModelProfileConfiguration(GatewayConfig config)
    {
        var profileIds = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var profile in config.Models.Profiles)
        {
            if (string.IsNullOrWhiteSpace(profile.Id) || !profileIds.Add(profile.Id))
                return false;
        }

        if (profileIds.Count == 0)
            profileIds.Add("default");

        if (!string.IsNullOrWhiteSpace(config.Models.DefaultProfile) && !profileIds.Contains(config.Models.DefaultProfile))
            return false;

        foreach (var profile in config.Models.Profiles)
        {
            if (profile.FallbackProfileIds.Any(fallbackId => !string.IsNullOrWhiteSpace(fallbackId) && !profileIds.Contains(fallbackId)))
                return false;
        }

        foreach (var route in config.Routing.Routes.Values)
        {
            if (!string.IsNullOrWhiteSpace(route.ModelProfileId) && !profileIds.Contains(route.ModelProfileId))
                return false;

            if (route.FallbackModelProfileIds.Any(fallbackId => !string.IsNullOrWhiteSpace(fallbackId) && !profileIds.Contains(fallbackId)))
                return false;
        }

        return true;
    }

    private static async Task<bool> PingOpenSandboxAsync(GatewayConfig config, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(config.Sandbox.Endpoint) ||
            !Uri.TryCreate(config.Sandbox.Endpoint, UriKind.Absolute, out var endpoint))
        {
            return false;
        }

        var apiKey = config.Sandbox.ApiKey;
        if (!string.IsNullOrWhiteSpace(apiKey) &&
            (apiKey.StartsWith("env:", StringComparison.OrdinalIgnoreCase) ||
             apiKey.StartsWith("raw:", StringComparison.OrdinalIgnoreCase)))
        {
            apiKey = SecretResolver.Resolve(apiKey);
        }

        var baseUri = endpoint.AbsoluteUri.TrimEnd('/');
        if (!baseUri.EndsWith("/v1", StringComparison.OrdinalIgnoreCase))
            baseUri += "/v1";

        var pingUri = new Uri(baseUri + "/ping", UriKind.Absolute);

        using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(5) };
        using var request = new HttpRequestMessage(HttpMethod.Get, pingUri);
        if (!string.IsNullOrWhiteSpace(apiKey))
            request.Headers.TryAddWithoutValidation("OPEN-SANDBOX-API-KEY", apiKey);

        try
        {
            using var response = await http.SendAsync(request, ct);
            return response.IsSuccessStatusCode;
        }
        catch
        {
            return false;
        }
    }

    private static bool IsNodeAvailable()
    {
        var path = Environment.GetEnvironmentVariable("PATH");
        if (string.IsNullOrWhiteSpace(path))
            return false;

        var candidates = OperatingSystem.IsWindows()
            ? new[] { "node.exe", "node.cmd", "node.bat" }
            : new[] { "node" };
        foreach (var dir in path.Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            foreach (var candidate in candidates)
            {
                try
                {
                    if (File.Exists(Path.Combine(dir, candidate)))
                        return true;
                }
                catch
                {
                }
            }
        }

        return false;
    }

    private static string? ResolveConfiguredPath(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
            return null;

        var resolved = SecretResolver.Resolve(value) ?? value;
        return Path.IsPathRooted(resolved) ? resolved : Path.GetFullPath(resolved);
    }
}

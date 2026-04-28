using System.Buffers;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization.Metadata;
using System.Globalization;
using Microsoft.Extensions.Logging.Abstractions;
using OpenClaw.Agent;
using OpenClaw.Agent.Execution;
using OpenClaw.Agent.Plugins;
using OpenClaw.Channels;
using OpenClaw.Core.Abstractions;
using OpenClaw.Core.Models;
using OpenClaw.Core.Observability;
using OpenClaw.Core.Pipeline;
using OpenClaw.Core.Plugins;
using OpenClaw.Core.Security;
using OpenClaw.Core.Skills;
using OpenClaw.Core.Validation;
using OpenClaw.Gateway;
using OpenClaw.Gateway.Bootstrap;
using OpenClaw.Gateway.Composition;
using OpenClaw.Gateway.Models;
using QRCoder;

namespace OpenClaw.Gateway.Endpoints;

internal static partial class AdminEndpoints
{
    private const int MaxAdminJsonBodyBytes = 256 * 1024;

    private sealed class AdminEndpointServices
    {
        public GatewayStartupContext Startup { get; init; } = null!;
        public GatewayAppRuntime Runtime { get; init; } = null!;
        public BrowserSessionAuthService BrowserSessions { get; init; } = null!;
        public OperatorAccountService OperatorAccounts { get; init; } = null!;
        public OrganizationPolicyService OrganizationPolicy { get; init; } = null!;
        public AdminSettingsService AdminSettings { get; init; } = null!;
        public PluginAdminSettingsService PluginAdminSettings { get; init; } = null!;
        public HeartbeatService Heartbeat { get; init; } = null!;
        public IMemoryStore MemoryStore { get; init; } = null!;
        public IMemoryNoteSearch? MemorySearch { get; init; }
        public IMemoryNoteCatalog? MemoryCatalog { get; init; }
        public IUserProfileStore ProfileStore { get; init; } = null!;
        public ILearningProposalStore ProposalStore { get; init; } = null!;
        public GatewayAutomationService AutomationService { get; init; } = null!;
        public LearningService LearningService { get; init; } = null!;
        public IntegrationApiFacade Facade { get; init; } = null!;
        public ToolPresetResolver ToolPresetResolver { get; init; } = null!;
        public AdminObservabilityService Observability { get; init; } = null!;
        public GatewayMaintenanceRuntimeService Maintenance { get; init; } = null!;
        public ISessionAdminStore SessionAdminStore { get; init; } = null!;
        public RuntimeOperationsState Operations { get; init; } = null!;
        public ProviderSmokeRegistry ProviderSmokeRegistry { get; init; } = null!;
        public SetupVerificationSnapshotStore SetupVerificationSnapshots { get; init; } = null!;
        public IModelProfileRegistry ModelProfiles { get; init; } = null!;
        public ModelEvaluationRunner ModelEvaluationRunner { get; init; } = null!;
    }

    private static async Task<JsonBodyReadResult<T>> ReadJsonBodyAsync<T>(HttpContext ctx, JsonTypeInfo<T> typeInfo)
        where T : class
    {
        if (ctx.Request.ContentLength is > MaxAdminJsonBodyBytes)
            return new(default, Results.StatusCode(StatusCodes.Status413PayloadTooLarge));

        if (ctx.Request.ContentLength is 0)
            return new(default, null);

        var buffer = ArrayPool<byte>.Shared.Rent(16 * 1024);
        try
        {
            await using var payload = new MemoryStream();
            while (true)
            {
                var read = await ctx.Request.Body.ReadAsync(buffer.AsMemory(0, buffer.Length), ctx.RequestAborted);
                if (read == 0)
                    break;

                if (payload.Length + read > MaxAdminJsonBodyBytes)
                    return new(default, Results.StatusCode(StatusCodes.Status413PayloadTooLarge));

                await payload.WriteAsync(buffer.AsMemory(0, read), ctx.RequestAborted);
            }

            if (payload.Length == 0)
                return new(default, null);

            payload.Position = 0;
            var value = await JsonSerializer.DeserializeAsync(payload, typeInfo, ctx.RequestAborted);
            return new(value, null);
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(buffer);
        }
    }

    private static AdminSettingsResponse BuildSettingsResponse(
        GatewayStartupContext startup,
        AdminSettingsService adminSettings,
        AdminSettingsSnapshot? snapshot = null,
        AdminSettingsPersistenceInfo? persistence = null,
        bool restartRequired = false,
        IReadOnlyList<string>? restartRequiredFields = null,
        string? message = null,
        IReadOnlyList<string>? extraWarnings = null)
    {
        var readiness = MapChannelReadiness(ChannelReadinessEvaluator.Evaluate(startup.Config, startup.IsNonLoopbackBind));
        var warnings = GetChannelWarnings(readiness);
        if (extraWarnings is { Count: > 0 })
            warnings.AddRange(extraWarnings);

        return new AdminSettingsResponse
        {
            Settings = snapshot ?? adminSettings.GetSnapshot(),
            Persistence = persistence ?? adminSettings.GetPersistence(),
            Message = message ?? "Settings loaded.",
            RestartRequired = restartRequired,
            RestartRequiredFields = restartRequiredFields ?? [],
            ImmediateFieldKeys = AdminSettingsService.ImmediateFieldKeys,
            RestartFieldKeys = AdminSettingsService.RestartFieldKeys,
            Warnings = warnings,
            ChannelReadiness = readiness
        };
    }

    private static IReadOnlyList<ChannelReadinessDto> MapChannelReadiness(IReadOnlyList<ChannelReadinessState> states)
        => states.Select(static state => new ChannelReadinessDto
        {
            ChannelId = state.ChannelId,
            DisplayName = state.DisplayName,
            Mode = state.Mode,
            Status = state.Status,
            Enabled = state.Enabled,
            Ready = state.Ready,
            MissingRequirements = state.MissingRequirements,
            Warnings = state.Warnings,
            FixGuidance = state.FixGuidance.Select(static item => new ChannelFixGuidanceDto
            {
                Label = item.Label,
                Href = item.Href,
                Reference = item.Reference
            }).ToArray()
        }).ToArray();

    private static List<string> GetChannelWarnings(IReadOnlyList<ChannelReadinessDto> readiness)
        => readiness
            .SelectMany(static item => item.Warnings.Select(warning => $"{item.DisplayName}: {warning}"))
            .Distinct(StringComparer.Ordinal)
            .ToList();

    private static (EndpointHelpers.OperatorAuthorizationResult? Authorization, IResult? Failure) AuthorizeOperator(
        HttpContext ctx,
        GatewayStartupContext startup,
        BrowserSessionAuthService browserSessions,
        RuntimeOperationsState operations,
        bool requireCsrf,
        string endpointScope)
        => EndpointHelpers.AuthorizeOperatorEndpoint(ctx, startup, browserSessions, operations, requireCsrf, endpointScope);

    private static void RecordOperatorAudit(
        HttpContext ctx,
        RuntimeOperationsState operations,
        EndpointHelpers.OperatorAuthorizationResult auth,
        string actionType,
        string targetId,
        string summary,
        bool success,
        object? before,
        object? after)
    {
        operations.OperatorAudit.Append(new OperatorAuditEntry
        {
            Id = $"audit_{Guid.NewGuid():N}"[..20],
            ActorId = EndpointHelpers.GetOperatorActorId(ctx, auth),
            AuthMode = auth.AuthMode,
            ActorRole = auth.Role,
            ActorDisplayName = auth.DisplayName ?? auth.Username,
            ActionType = actionType,
            TargetId = string.IsNullOrWhiteSpace(targetId) ? "unknown" : targetId,
            Summary = summary,
            Before = SerializeAuditValue(before),
            After = SerializeAuditValue(after),
            Success = success
        });
    }

    private static AuthSessionResponse MapAuthSessionResponse(
        EndpointHelpers.OperatorAuthorizationResult auth,
        GatewayStartupContext startup,
        GatewayAppRuntime runtime,
        OrganizationPolicySnapshot policy,
        ToolPresetResolver toolPresetResolver)
    {
        var browser = BrowserToolCapabilityEvaluator.Evaluate(startup.Config, startup.RuntimeState);
        var preset = toolPresetResolver.Resolve(
            new Session
            {
                Id = "webchat:status",
                ChannelId = "websocket",
                SenderId = "webchat-status"
            },
            runtime.RegisteredToolNames);

        return new AuthSessionResponse
        {
            AuthMode = auth.AuthMode,
            CsrfToken = auth.BrowserSession?.CsrfToken,
            ExpiresAtUtc = auth.BrowserSession?.ExpiresAtUtc,
            Persistent = auth.BrowserSession?.Persistent ?? false,
            Role = auth.Role,
            AccountId = auth.AccountId,
            Username = auth.Username,
            DisplayName = auth.DisplayName,
            IsBootstrapAdmin = auth.IsBootstrapAdmin,
            PublicBind = startup.IsNonLoopbackBind,
            AllowedAuthModes = [.. policy.AllowedAuthModes],
            EffectiveToolSurface = preset.Surface,
            EffectiveToolPresetId = preset.PresetId,
            EffectiveToolPresetDescription = preset.Description,
            BrowserToolRegistered = browser.Registered,
            BrowserExecutionBackendConfigured = browser.ExecutionBackendConfigured,
            BrowserCapabilityReason = browser.Reason,
            CapabilitySummary = BuildCapabilitySummary(preset, browser)
        };
    }

    private static DateTimeOffset? GetQueryDateTimeOffset(HttpRequest request, string key)
    {
        if (!request.Query.TryGetValue(key, out var raw) || string.IsNullOrWhiteSpace(raw))
            return null;

        return DateTimeOffset.TryParse(raw.ToString(), CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind, out var parsed)
            ? parsed
            : null;
    }

    private static int? GetQueryInt32(HttpRequest request, string key)
    {
        if (!request.Query.TryGetValue(key, out var raw) || string.IsNullOrWhiteSpace(raw))
            return null;

        return int.TryParse(raw.ToString(), NumberStyles.Integer, CultureInfo.InvariantCulture, out var parsed)
            ? parsed
            : null;
    }

    private static bool? GetQueryBool(HttpRequest request, string key)
    {
        if (!request.Query.TryGetValue(key, out var raw) || string.IsNullOrWhiteSpace(raw))
            return null;

        return raw.ToString().Trim().ToLowerInvariant() switch
        {
            "1" or "true" or "yes" or "y" => true,
            "0" or "false" or "no" or "n" => false,
            _ => null
        };
    }

    private static async Task<SetupStatusResponse> BuildSetupStatusResponseAsync(
        GatewayStartupContext startup,
        OrganizationPolicyService organizationPolicy,
        OperatorAccountService operatorAccounts,
        IModelProfileRegistry modelProfiles,
        ProviderSmokeRegistry providerSmokeRegistry,
        SetupVerificationSnapshotStore setupVerificationSnapshots,
        GatewayMaintenanceRuntimeService maintenance,
        bool includeReliability,
        CancellationToken ct)
    {
        var policy = organizationPolicy.GetSnapshot();
        var publicBind = startup.IsNonLoopbackBind;
        var configDirectory = Path.GetDirectoryName(startup.Config.Memory.StoragePath)
            ?? startup.Config.Memory.StoragePath;
        var deployDirectory = Path.Combine(configDirectory, "deploy");
        var workspacePath = ResolveConfiguredPath(startup.Config.Tooling.WorkspaceRoot);
        var workspaceExists = !string.IsNullOrWhiteSpace(workspacePath) && Directory.Exists(workspacePath);
        var operatorAccountCount = operatorAccounts.List().Count;
        var browser = BrowserToolCapabilityEvaluator.Evaluate(startup.Config, startup.RuntimeState);
        var modelDoctor = ModelDoctorEvaluator.Build(startup.Config, modelProfiles);
        var snapshot = setupVerificationSnapshots.Load();
        var warnings = new List<string>();
        if (publicBind)
            warnings.Add("Reverse proxy and TLS are recommended for public bind deployments.");
        if (string.IsNullOrWhiteSpace(startup.Config.AuthToken))
            warnings.Add("No auth token is configured.");

        var status = new SetupStatusResponse
        {
            Profile = publicBind ? "public" : "local",
            BindAddress = startup.Config.BindAddress,
            Port = startup.Config.Port,
            PublicBind = publicBind,
            AuthTokenConfigured = !string.IsNullOrWhiteSpace(startup.Config.AuthToken),
            BootstrapTokenEnabled = policy.BootstrapTokenEnabled,
            AllowedAuthModes = [.. policy.AllowedAuthModes],
            MinimumPluginTrustLevel = policy.MinimumPluginTrustLevel,
            ReverseProxyRecommended = publicBind,
            ReachableBaseUrl = BuildReachableBaseUrl(startup.Config.BindAddress, startup.Config.Port),
            WorkspacePath = workspacePath,
            WorkspaceExists = workspaceExists,
            HasOperatorAccounts = operatorAccountCount > 0,
            OperatorAccountCount = operatorAccountCount,
            ProviderConfigured = ProviderSmokeProbe.IsProviderConfigured(startup.Config.Llm, providerSmokeRegistry),
            ProviderSmokeStatus = SetupVerificationService.GetCheckStatus(snapshot, "provider_smoke"),
            ModelDoctorStatus = SetupVerificationService.GetModelDoctorStatus(modelDoctor),
            BrowserToolRegistered = browser.Registered,
            BrowserExecutionBackendConfigured = browser.ExecutionBackendConfigured,
            BrowserCapabilityReason = browser.Reason,
            LastVerificationAtUtc = snapshot?.RecordedAtUtc,
            LastVerificationSource = snapshot?.Source,
            LastVerificationStatus = snapshot?.Verification.OverallStatus ?? SetupCheckStates.NotRun,
            LastVerificationHasFailures = snapshot?.Verification.HasFailures ?? false,
            LastVerificationHasWarnings = snapshot?.Verification.HasWarnings ?? false,
            BootstrapGuidanceState = SetupVerificationService.GetBootstrapGuidanceState(publicBind, policy.BootstrapTokenEnabled, operatorAccountCount),
            RecommendedNextActions = SetupVerificationService.BuildRecommendedNextActions(
                startup.Config,
                policy,
                operatorAccountCount,
                browser,
                workspaceExists,
                publicBind,
                providerSmokeRegistry),
            ChannelReadiness = MapChannelReadiness(ChannelReadinessEvaluator.Evaluate(startup.Config, startup.IsNonLoopbackBind)),
            Artifacts = BuildSetupArtifacts(deployDirectory),
            Warnings = warnings
        };

        if (!includeReliability)
            return status;

        var maintenanceReport = await maintenance.ScanAsync(status, ct);
        return new SetupStatusResponse
        {
            Profile = status.Profile,
            BindAddress = status.BindAddress,
            Port = status.Port,
            PublicBind = status.PublicBind,
            AuthTokenConfigured = status.AuthTokenConfigured,
            BootstrapTokenEnabled = status.BootstrapTokenEnabled,
            AllowedAuthModes = status.AllowedAuthModes,
            MinimumPluginTrustLevel = status.MinimumPluginTrustLevel,
            ReverseProxyRecommended = status.ReverseProxyRecommended,
            ReachableBaseUrl = status.ReachableBaseUrl,
            WorkspacePath = status.WorkspacePath,
            WorkspaceExists = status.WorkspaceExists,
            HasOperatorAccounts = status.HasOperatorAccounts,
            OperatorAccountCount = status.OperatorAccountCount,
            ProviderConfigured = status.ProviderConfigured,
            ProviderSmokeStatus = status.ProviderSmokeStatus,
            ModelDoctorStatus = status.ModelDoctorStatus,
            BrowserToolRegistered = status.BrowserToolRegistered,
            BrowserExecutionBackendConfigured = status.BrowserExecutionBackendConfigured,
            BrowserCapabilityReason = status.BrowserCapabilityReason,
            LastVerificationAtUtc = status.LastVerificationAtUtc,
            LastVerificationSource = status.LastVerificationSource,
            LastVerificationStatus = status.LastVerificationStatus,
            LastVerificationHasFailures = status.LastVerificationHasFailures,
            LastVerificationHasWarnings = status.LastVerificationHasWarnings,
            BootstrapGuidanceState = status.BootstrapGuidanceState,
            RecommendedNextActions = status.RecommendedNextActions,
            ChannelReadiness = status.ChannelReadiness,
            Artifacts = status.Artifacts,
            Warnings = status.Warnings,
            Reliability = maintenanceReport.Reliability
        };
    }

    private static IReadOnlyList<SetupArtifactStatusItem> BuildSetupArtifacts(string deployDirectory)
    {
        return new[]
        {
            ("gateway-systemd", "Gateway systemd unit", Path.Combine(deployDirectory, "openclaw-gateway.service")),
            ("companion-systemd", "Companion systemd unit", Path.Combine(deployDirectory, "openclaw-companion.service")),
            ("gateway-launchd", "Gateway launchd plist", Path.Combine(deployDirectory, "ai.openclaw.gateway.plist")),
            ("companion-launchd", "Companion launchd plist", Path.Combine(deployDirectory, "ai.openclaw.companion.plist")),
            ("caddy", "Caddy reverse proxy recipe", Path.Combine(deployDirectory, "Caddyfile"))
        }.Select(static item => new SetupArtifactStatusItem
        {
            Id = item.Item1,
            Label = item.Item2,
            Path = item.Item3,
            Exists = File.Exists(item.Item3),
            Status = File.Exists(item.Item3) ? "present" : "missing"
        }).ToArray();
    }

    private static string BuildReachableBaseUrl(string bindAddress, int port)
    {
        if (string.Equals(bindAddress, "0.0.0.0", StringComparison.Ordinal) ||
            string.Equals(bindAddress, "::", StringComparison.Ordinal) ||
            string.Equals(bindAddress, "[::]", StringComparison.Ordinal))
        {
            return $"http://127.0.0.1:{port.ToString(CultureInfo.InvariantCulture)}";
        }

        if (bindAddress.Contains(':') && !bindAddress.StartsWith("[", StringComparison.Ordinal))
            return $"http://[{bindAddress}]:{port.ToString(CultureInfo.InvariantCulture)}";

        return $"http://{bindAddress}:{port.ToString(CultureInfo.InvariantCulture)}";
    }

    private static string[] BuildCapabilitySummary(ResolvedToolPreset preset, BrowserToolCapabilitySummary browser)
    {
        var items = new List<string>();
        if (!preset.AllowedTools.Contains("shell") &&
            !preset.AllowedTools.Contains("process") &&
            !preset.AllowedTools.Contains("write_file"))
        {
            items.Add($"Preset '{preset.PresetId}' keeps coding and mutation tools off on the chat surface.");
        }

        if (!preset.AllowedTools.Contains("browser"))
        {
            items.Add($"Preset '{preset.PresetId}' does not allow the browser tool on this surface.");
        }
        else if (!browser.Registered)
        {
            items.Add("Browser is unavailable in this runtime because no supported execution backend is configured.");
        }

        if (preset.RequireToolApproval)
            items.Add("This surface keeps approval requirements enabled for restricted tools.");

        return items.ToArray();
    }

    private static string? ResolveConfiguredPath(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
            return null;

        var resolved = SecretResolver.Resolve(value) ?? value;
        return Path.IsPathRooted(resolved) ? resolved : Path.GetFullPath(resolved);
    }

    private static async Task<IReadOnlyList<MemoryNoteItem>> ListMemoryNotesAsync(
        IMemoryNoteCatalog catalog,
        string? prefix,
        string? memoryClass,
        string? projectId,
        int limit,
        CancellationToken ct)
    {
        var normalizedClass = NormalizeMemoryClass(memoryClass);
        var normalizedProjectId = NormalizeOptionalValue(projectId);
        var prefixFilter = BuildMemoryPrefix(normalizedClass, normalizedProjectId, NormalizeOptionalValue(prefix));
        var entries = await catalog.ListNotesAsync(prefixFilter, Math.Clamp(limit, 1, 500), ct);
        return entries
            .Select(static entry => MapMemoryNoteItem(entry.Key, entry.PreviewContent, entry.UpdatedAt))
            .Where(item => MatchesMemoryNoteFilter(item, normalizedClass, normalizedProjectId))
            .ToArray();
    }

    private static async Task<IReadOnlyList<MemoryNoteItem>> MaterializeMemoryNoteItemsAsync(
        IMemoryStore memoryStore,
        IReadOnlyList<MemoryNoteCatalogEntry> entries,
        bool includeContent,
        CancellationToken ct)
    {
        var items = new List<MemoryNoteItem>(entries.Count);
        foreach (var entry in entries)
        {
            ct.ThrowIfCancellationRequested();
            string? content = null;
            if (includeContent)
                content = await memoryStore.LoadNoteAsync(entry.Key, ct) ?? entry.PreviewContent;

            items.Add(MapMemoryNoteItem(entry.Key, content ?? entry.PreviewContent, entry.UpdatedAt, includeContent));
        }

        return items;
    }

    private static SkillHealthSnapshot MapSkillHealthSnapshot(SkillDefinition skill)
    {
        var warnings = new List<string>();
        if (string.IsNullOrWhiteSpace(skill.Description))
            warnings.Add("Description is empty.");
        if (skill.Metadata.RequireBins.Length == 0 &&
            skill.Metadata.RequireAnyBins.Length == 0 &&
            skill.Metadata.RequireEnv.Length == 0 &&
            skill.Metadata.RequireConfig.Length == 0)
        {
            warnings.Add("No host requirements declared.");
        }

        var trustLevel = skill.Source == SkillSource.Bundled ? "first-party" : "upstream-compatible";
        var trustReason = skill.Source == SkillSource.Bundled
            ? "Skill ships with OpenClaw.NET."
            : "Skill document parsed successfully and follows the OpenClaw skill format.";

        return new SkillHealthSnapshot
        {
            Name = skill.Name,
            Description = skill.Description,
            Source = skill.Source.ToString().ToLowerInvariant(),
            Location = skill.Location,
            TrustLevel = trustLevel,
            TrustReason = trustReason,
            Always = skill.Metadata.Always,
            UserInvocable = skill.UserInvocable,
            DisableModelInvocation = skill.DisableModelInvocation,
            CommandDispatch = skill.CommandDispatch,
            CommandTool = skill.CommandTool,
            CommandArgMode = skill.CommandArgMode,
            Homepage = skill.Metadata.Homepage,
            PrimaryEnv = skill.Metadata.PrimaryEnv,
            RequiredBins = skill.Metadata.RequireBins,
            RequiredAnyBins = skill.Metadata.RequireAnyBins,
            RequiredEnv = skill.Metadata.RequireEnv,
            RequiredConfig = skill.Metadata.RequireConfig,
            Warnings = warnings.ToArray()
        };
    }

    private static MemoryNoteItem MapMemoryNoteItem(string key, string content, DateTimeOffset updatedAt, bool includeContent = false)
    {
        var classification = ClassifyMemoryNoteKey(key);
        var preview = content.Length <= 512 ? content : content[..512] + "…";
        return new MemoryNoteItem
        {
            Key = key,
            DisplayKey = classification.DisplayKey,
            MemoryClass = classification.MemoryClass,
            ProjectId = classification.ProjectId,
            Preview = preview,
            Content = includeContent ? content : null,
            UpdatedAtUtc = updatedAt
        };
    }

    private static bool MatchesMemoryNoteFilter(MemoryNoteItem item, string? memoryClass, string? projectId)
    {
        if (!string.IsNullOrWhiteSpace(memoryClass) &&
            !string.Equals(item.MemoryClass, memoryClass, StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        if (!string.IsNullOrWhiteSpace(projectId) &&
            !string.Equals(item.ProjectId, projectId, StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        return true;
    }

    private static string? NormalizeMemoryClass(string? memoryClass)
    {
        if (string.IsNullOrWhiteSpace(memoryClass))
            return null;

        return memoryClass.Trim().ToLowerInvariant() switch
        {
            MemoryNoteClass.General => MemoryNoteClass.General,
            MemoryNoteClass.ProjectFact => MemoryNoteClass.ProjectFact,
            MemoryNoteClass.OperationalRunbook => MemoryNoteClass.OperationalRunbook,
            MemoryNoteClass.ApprovedSkill => MemoryNoteClass.ApprovedSkill,
            MemoryNoteClass.ApprovedAutomation => MemoryNoteClass.ApprovedAutomation,
            _ => null
        };
    }

    private static string? NormalizeOptionalValue(string? value)
        => string.IsNullOrWhiteSpace(value) ? null : value.Trim();

    private static string BuildMemoryPrefix(string? memoryClass, string? projectId, string? prefixSuffix)
    {
        var prefix = memoryClass switch
        {
            MemoryNoteClass.ProjectFact when !string.IsNullOrWhiteSpace(projectId) => $"project:{projectId}:",
            MemoryNoteClass.ProjectFact => "project:",
            MemoryNoteClass.OperationalRunbook => "runbook:",
            MemoryNoteClass.ApprovedSkill => "skill:",
            MemoryNoteClass.ApprovedAutomation => "automation:",
            _ => string.Empty
        };

        if (string.IsNullOrWhiteSpace(prefixSuffix))
            return prefix;

        return string.Concat(prefix, prefixSuffix.Trim());
    }

    private static string? BuildMemoryNoteKey(string? key, string? memoryClass, string? projectId, out string? error)
    {
        error = null;
        var normalizedClass = memoryClass ?? MemoryNoteClass.General;
        var normalizedKey = NormalizeOptionalValue(key);
        var normalizedProjectId = NormalizeOptionalValue(projectId);

        if (normalizedClass == MemoryNoteClass.ProjectFact)
        {
            if (string.IsNullOrWhiteSpace(normalizedProjectId))
            {
                error = "projectId is required for project_fact memory.";
                return null;
            }

            var projectError = InputSanitizer.CheckMemoryKey(normalizedProjectId);
            if (projectError is not null)
            {
                error = projectError;
                return null;
            }
        }

        if (string.IsNullOrWhiteSpace(normalizedKey))
        {
            error = "key is required.";
            return null;
        }

        var keyError = InputSanitizer.CheckMemoryKey(normalizedKey);
        if (keyError is not null)
        {
            error = keyError;
            return null;
        }

        return normalizedClass switch
        {
            MemoryNoteClass.ProjectFact => $"project:{normalizedProjectId}:{normalizedKey}",
            MemoryNoteClass.OperationalRunbook => $"runbook:{normalizedKey}",
            MemoryNoteClass.ApprovedSkill => $"skill:{normalizedKey}",
            MemoryNoteClass.ApprovedAutomation => $"automation:{normalizedKey}",
            _ => normalizedKey
        };
    }

    private static (string MemoryClass, string? ProjectId, string DisplayKey) ClassifyMemoryNoteKey(string key)
    {
        if (key.StartsWith("project:", StringComparison.Ordinal))
        {
            var segments = key.Split(':', 3, StringSplitOptions.None);
            if (segments.Length == 3)
            {
                return (MemoryNoteClass.ProjectFact, segments[1], segments[2]);
            }
        }

        if (key.StartsWith("runbook:", StringComparison.Ordinal))
            return (MemoryNoteClass.OperationalRunbook, null, key["runbook:".Length..]);

        if (key.StartsWith("skill:", StringComparison.Ordinal))
            return (MemoryNoteClass.ApprovedSkill, null, key["skill:".Length..]);

        if (key.StartsWith("automation:", StringComparison.Ordinal))
            return (MemoryNoteClass.ApprovedAutomation, null, key["automation:".Length..]);

        return (MemoryNoteClass.General, null, key);
    }

    private static string NormalizeSessionPromotionTarget(string? value)
        => string.IsNullOrWhiteSpace(value)
            ? SessionPromotionTarget.Automation
            : value.Trim().ToLowerInvariant() switch
            {
                SessionPromotionTarget.Automation => SessionPromotionTarget.Automation,
                SessionPromotionTarget.ProviderPolicy => SessionPromotionTarget.ProviderPolicy,
                SessionPromotionTarget.SkillDraft => SessionPromotionTarget.SkillDraft,
                _ => value.Trim()
            };

    private static AutomationDefinition BuildAutomationPromotion(Session session, SessionPromotionRequest request)
    {
        var now = DateTimeOffset.UtcNow;
        var name = string.IsNullOrWhiteSpace(request.Name)
            ? BuildSessionPromotionName(session, fallbackPrefix: "Session Workflow")
            : request.Name.Trim();
        var slug = SlugifyValue(name, maxLength: 40);
        var deliveryChannelId = !string.IsNullOrWhiteSpace(request.DeliveryChannelId)
            ? request.DeliveryChannelId.Trim()
            : string.Equals(session.ChannelId, "delegation", StringComparison.OrdinalIgnoreCase)
                ? "cron"
                : session.ChannelId;
        var deliveryRecipientId = !string.IsNullOrWhiteSpace(request.DeliveryRecipientId)
            ? request.DeliveryRecipientId.Trim()
            : string.Equals(deliveryChannelId, "cron", StringComparison.OrdinalIgnoreCase)
                ? null
                : session.SenderId;
        var tags = (request.Tags ?? [])
            .Where(static item => !string.IsNullOrWhiteSpace(item))
            .Select(static item => item.Trim())
            .Concat(["promoted", "session"])
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();
        var automationId = TrimToMaxLength($"promoted:auto:{slug}:{Guid.NewGuid():N}", 60);

        return new AutomationDefinition
        {
            Id = automationId,
            Name = name,
            Enabled = false,
            Schedule = string.IsNullOrWhiteSpace(request.Schedule) ? "@daily" : request.Schedule.Trim(),
            Prompt = string.IsNullOrWhiteSpace(request.Prompt) ? BuildAutomationPromptFromSession(session) : request.Prompt.Trim(),
            SessionId = session.Id,
            DeliveryChannelId = deliveryChannelId,
            DeliveryRecipientId = deliveryRecipientId,
            DeliverySubject = string.IsNullOrWhiteSpace(request.DeliverySubject) ? null : request.DeliverySubject.Trim(),
            Tags = tags,
            IsDraft = true,
            Source = "session-promotion",
            TemplateKey = "custom",
            CreatedAtUtc = now,
            UpdatedAtUtc = now
        };
    }

    private static ProviderPolicyRule BuildProviderPolicyPromotion(
        Session session,
        SessionPromotionRequest request,
        ProviderUsageTracker providerUsage)
    {
        var latestTurn = providerUsage.RecentTurns(session.Id, limit: 20)
            .OrderByDescending(static item => item.TimestampUtc)
            .FirstOrDefault();
        var providerId = string.IsNullOrWhiteSpace(request.ProviderId)
            ? latestTurn?.ProviderId
            : request.ProviderId.Trim();
        var modelId = string.IsNullOrWhiteSpace(request.ModelId)
            ? latestTurn?.ModelId ?? session.ModelOverride
            : request.ModelId.Trim();

        if (string.IsNullOrWhiteSpace(providerId))
            throw new InvalidOperationException("Provider policy promotion requires a providerId or at least one recorded provider turn for the session.");
        if (string.IsNullOrWhiteSpace(modelId))
            throw new InvalidOperationException("Provider policy promotion requires a modelId or at least one recorded provider turn for the session.");

        var scope = NormalizeOptionalValue(request.Scope)?.ToLowerInvariant() ?? "session";
        var (channelId, senderId, sessionId) = scope switch
        {
            "global" => (null, null, null),
            "channel" => (session.ChannelId, null, null),
            "actor" => (session.ChannelId, session.SenderId, null),
            _ => (session.ChannelId, session.SenderId, session.Id)
        };
        var slug = SlugifyValue(string.IsNullOrWhiteSpace(request.Name) ? session.Id : request.Name, maxLength: 32);
        var policyId = TrimToMaxLength($"pp_{slug}_{Guid.NewGuid():N}", 20);

        return new ProviderPolicyRule
        {
            Id = policyId,
            Priority = request.Priority,
            Enabled = request.Enabled,
            ChannelId = channelId,
            SenderId = senderId,
            SessionId = sessionId,
            ProviderId = providerId!,
            ModelId = modelId!,
            FallbackModels = (request.FallbackModels ?? [])
                .Where(static item => !string.IsNullOrWhiteSpace(item))
                .Select(static item => item.Trim())
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToArray(),
            CreatedAtUtc = DateTimeOffset.UtcNow
        };
    }

    private static LearningProposal BuildSkillPromotion(Session session, SessionPromotionRequest request)
    {
        var skillName = SlugifyValue(
            string.IsNullOrWhiteSpace(request.Name) ? BuildSessionPromotionName(session, fallbackPrefix: "Session Skill") : request.Name,
            maxLength: 48);
        if (string.IsNullOrWhiteSpace(skillName))
            skillName = $"session-skill-{Guid.NewGuid():N}"[..24];

        var draftContent = BuildSkillDraftFromSession(session, skillName, request);
        return new LearningProposal
        {
            Id = $"lp_{Guid.NewGuid():N}"[..20],
            Kind = LearningProposalKind.SkillDraft,
            Status = LearningProposalStatus.Pending,
            ActorId = $"{session.ChannelId}:{session.SenderId}",
            Title = string.IsNullOrWhiteSpace(request.Name) ? $"Skill draft from {session.Id}" : request.Name.Trim(),
            Summary = string.IsNullOrWhiteSpace(request.Summary)
                ? "Operator-promoted skill draft from a successful session."
                : request.Summary.Trim(),
            SkillName = skillName,
            DraftContent = draftContent,
            DraftContentHash = ComputeDraftHash(draftContent),
            SourceSessionIds = CollectPromotionSourceSessionIds(session),
            Confidence = 0.9f
        };
    }

    private static string[] CollectPromotionSourceSessionIds(Session session)
        => session.DelegatedSessions.Count == 0
            ? [session.Id]
            : [session.Id, .. session.DelegatedSessions.Select(static item => item.SessionId).Distinct(StringComparer.Ordinal)];

    private static string BuildAutomationPromptFromSession(Session session)
    {
        var lastUserMessage = session.History.LastOrDefault(static item => string.Equals(item.Role, "user", StringComparison.OrdinalIgnoreCase))?.Content?.Trim();
        if (!string.IsNullOrWhiteSpace(lastUserMessage))
            return lastUserMessage!;

        if (session.Delegation is { RequestedTask.Length: > 0 } delegation)
            return delegation.RequestedTask;

        return $"Repeat the successful workflow captured in session '{session.Id}' and report the outcome.";
    }

    private static string BuildSkillDraftFromSession(Session session, string skillName, SessionPromotionRequest request)
    {
        var taskSummary = string.IsNullOrWhiteSpace(request.Prompt)
            ? BuildAutomationPromptFromSession(session)
            : request.Prompt.Trim();
        var localToolSequence = session.History
            .SelectMany(static turn => turn.ToolCalls ?? [])
            .Select(static call => call.ToolName)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();
        var delegatedProfiles = session.DelegatedSessions
            .Select(static item => item.Profile)
            .Where(static item => !string.IsNullOrWhiteSpace(item))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();

        var builder = new StringBuilder();
        builder.AppendLine("---");
        builder.AppendLine($"name: {skillName}");
        builder.AppendLine($"description: Operator-promoted workflow from session {session.Id}");
        builder.AppendLine("---");
        builder.AppendLine();
        builder.AppendLine("Use this skill when the task resembles the following request:");
        builder.AppendLine($"- {taskSummary}");
        builder.AppendLine();
        builder.AppendLine("Session provenance:");
        builder.AppendLine($"- Source session: {session.Id}");
        builder.AppendLine($"- Channel: {session.ChannelId}");
        builder.AppendLine($"- Sender: {session.SenderId}");
        builder.AppendLine();

        if (localToolSequence.Length > 0)
        {
            builder.AppendLine("Preferred local tool usage:");
            foreach (var toolName in localToolSequence)
                builder.AppendLine($"- {toolName}");
            builder.AppendLine();
        }

        if (delegatedProfiles.Length > 0)
        {
            builder.AppendLine("Delegated profiles involved:");
            foreach (var profile in delegatedProfiles)
                builder.AppendLine($"- {profile}");
            builder.AppendLine();
        }

        if (session.DelegatedSessions.Count > 0)
        {
            builder.AppendLine("Delegated work summaries:");
            foreach (var child in session.DelegatedSessions)
            {
                builder.AppendLine($"- {child.Profile}: {child.TaskPreview}");
                foreach (var tool in child.ToolUsage.Take(3))
                    builder.AppendLine($"  tool: {tool.ToolName} ({tool.Count})");
            }
            builder.AppendLine();
        }

        builder.AppendLine("Expected behavior:");
        builder.AppendLine("- Recreate the successful workflow with minimal extra steps.");
        builder.AppendLine("- Preserve the task intent and output shape from the original session.");
        builder.AppendLine("- Escalate when the task requires unavailable credentials, tools, or approvals.");
        return builder.ToString().TrimEnd();
    }

    private static string BuildSessionPromotionName(Session session, string fallbackPrefix)
    {
        var lastUser = session.History.LastOrDefault(static item => string.Equals(item.Role, "user", StringComparison.OrdinalIgnoreCase))?.Content;
        if (!string.IsNullOrWhiteSpace(lastUser))
            return lastUser!.Length <= 60 ? lastUser : lastUser[..60];

        if (session.Delegation is { Profile.Length: > 0 } delegation)
            return $"{fallbackPrefix} {delegation.Profile}";

        return $"{fallbackPrefix} {session.ChannelId}";
    }

    private static string SlugifyValue(string? value, int maxLength)
    {
        if (string.IsNullOrWhiteSpace(value))
            return "session";

        Span<char> buffer = stackalloc char[Math.Min(maxLength, value.Length)];
        var length = 0;
        var previousDash = false;
        foreach (var ch in value.Trim().ToLowerInvariant())
        {
            if (length >= buffer.Length)
                break;

            if (char.IsLetterOrDigit(ch))
            {
                buffer[length++] = ch;
                previousDash = false;
            }
            else if (!previousDash && length > 0)
            {
                buffer[length++] = '-';
                previousDash = true;
            }
        }

        while (length > 0 && buffer[length - 1] == '-')
            length--;

        return length == 0 ? "session" : new string(buffer[..length]);
    }

    private static string ComputeDraftHash(string draftContent)
        => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(draftContent)));

    private static string TrimToMaxLength(string value, int maxLength)
        => value.Length <= maxLength ? value : value[..maxLength];

    private static string GetManagedSkillRoot()
        => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".openclaw", "skills");

    private static async Task<IReadOnlyList<ManagedSkillBundleItem>> ListManagedSkillBundleItemsAsync(CancellationToken ct)
    {
        var root = GetManagedSkillRoot();
        if (!Directory.Exists(root))
            return [];

        var items = new List<ManagedSkillBundleItem>();
        foreach (var directory in Directory.EnumerateDirectories(root))
        {
            ct.ThrowIfCancellationRequested();
            var skillPath = Path.Combine(directory, "SKILL.md");
            if (!File.Exists(skillPath))
                continue;

            var content = await File.ReadAllTextAsync(skillPath, ct);
            ParseManagedSkillFrontmatter(content, out var name, out var description);
            var info = new FileInfo(skillPath);
            items.Add(new ManagedSkillBundleItem
            {
                Name = string.IsNullOrWhiteSpace(name) ? Path.GetFileName(directory) : name,
                Slug = Path.GetFileName(directory),
                Description = description ?? string.Empty,
                Content = content,
                RootPath = directory,
                UpdatedAtUtc = info.Exists ? new DateTimeOffset(info.LastWriteTimeUtc, TimeSpan.Zero) : null
            });
        }

        return items
            .OrderBy(static item => item.Name, StringComparer.OrdinalIgnoreCase)
            .ToArray();
    }

    private static async Task SaveManagedSkillBundleItemAsync(ManagedSkillBundleItem item, CancellationToken ct)
    {
        var slug = SlugifySkillName(item.Slug, item.Name);
        var root = Path.Combine(GetManagedSkillRoot(), slug);
        Directory.CreateDirectory(root);
        await File.WriteAllTextAsync(Path.Combine(root, "SKILL.md"), item.Content, ct);
    }

    private static void ParseManagedSkillFrontmatter(string content, out string? name, out string? description)
    {
        name = null;
        description = null;

        var lines = content.Replace("\r\n", "\n", StringComparison.Ordinal).Split('\n');
        if (lines.Length < 3 || !string.Equals(lines[0], "---", StringComparison.Ordinal))
            return;

        for (var i = 1; i < lines.Length; i++)
        {
            var line = lines[i];
            if (string.Equals(line, "---", StringComparison.Ordinal))
                break;

            if (line.StartsWith("name:", StringComparison.OrdinalIgnoreCase))
                name = line["name:".Length..].Trim();
            else if (line.StartsWith("description:", StringComparison.OrdinalIgnoreCase))
                description = line["description:".Length..].Trim();
        }
    }

    private static string SlugifySkillName(string? preferredSlug, string? fallbackName)
    {
        var source = !string.IsNullOrWhiteSpace(preferredSlug) ? preferredSlug : fallbackName;
        if (string.IsNullOrWhiteSpace(source))
            return "skill";

        Span<char> buffer = stackalloc char[source.Length];
        var length = 0;
        var previousDash = false;

        foreach (var ch in source)
        {
            if (char.IsLetterOrDigit(ch))
            {
                buffer[length++] = char.ToLowerInvariant(ch);
                previousDash = false;
            }
            else if (!previousDash && length > 0)
            {
                buffer[length++] = '-';
                previousDash = true;
            }
        }

        while (length > 0 && buffer[length - 1] == '-')
            length--;

        return length == 0 ? "skill" : new string(buffer[..length]);
    }

    private static IReadOnlyList<SkillDefinition> LoadCurrentSkillDefinitions(
        GatewayStartupContext startup,
        GatewayAppRuntime runtime)
    {
        var pluginSkillDirs = runtime.LoadedSkills
            .Where(static skill => skill.Source == SkillSource.Plugin && !string.IsNullOrWhiteSpace(skill.Location))
            .Select(static skill => skill.Location)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();

        var loaded = SkillLoader.LoadAll(
            startup.Config.Skills,
            startup.WorkspacePath,
            NullLogger.Instance,
            pluginSkillDirs);

        var byName = loaded.ToDictionary(static skill => skill.Name, StringComparer.OrdinalIgnoreCase);
        foreach (var skill in runtime.LoadedSkills)
        {
            if (!byName.ContainsKey(skill.Name))
                byName[skill.Name] = skill;
        }

        return byName.Values
            .OrderBy(static skill => skill.Name, StringComparer.OrdinalIgnoreCase)
            .ToArray();
    }

    private static bool ProposalMatchesActor(LearningProposal proposal, string actorId)
    {
        if (string.Equals(proposal.ActorId, actorId, StringComparison.OrdinalIgnoreCase))
            return true;

        if (string.Equals(proposal.ProfileUpdate?.ActorId, actorId, StringComparison.OrdinalIgnoreCase))
            return true;

        return string.Equals(proposal.AppliedProfileBefore?.ActorId, actorId, StringComparison.OrdinalIgnoreCase);
    }

    private static UserProfile NormalizeProfile(UserProfile profile)
    {
        var normalizedActorId = string.IsNullOrWhiteSpace(profile.ActorId) ? $"{profile.ChannelId}:{profile.SenderId}" : profile.ActorId.Trim();
        var parts = normalizedActorId.Split(':', 2, StringSplitOptions.TrimEntries);
        var channelId = !string.IsNullOrWhiteSpace(profile.ChannelId)
            ? profile.ChannelId.Trim()
            : (parts.Length > 0 ? parts[0] : "unknown");
        var senderId = !string.IsNullOrWhiteSpace(profile.SenderId)
            ? profile.SenderId.Trim()
            : (parts.Length > 1 ? parts[1] : normalizedActorId);

        return new UserProfile
        {
            ActorId = normalizedActorId,
            ChannelId = channelId,
            SenderId = senderId,
            Summary = profile.Summary,
            Tone = profile.Tone,
            Facts = profile.Facts,
            Preferences = profile.Preferences,
            ActiveProjects = profile.ActiveProjects,
            RecentIntents = profile.RecentIntents,
            UpdatedAtUtc = DateTimeOffset.UtcNow
        };
    }

    private static ChannelAuthStatusItem MapChannelAuthStatusItem(BridgeChannelAuthEvent evt)
        => new()
        {
            ChannelId = evt.ChannelId,
            State = evt.State,
            Data = evt.Data,
            AccountId = evt.AccountId,
            UpdatedAtUtc = evt.UpdatedAtUtc
        };

    private static WhatsAppSetupResponse BuildWhatsAppSetupResponse(
        GatewayStartupContext startup,
        GatewayAppRuntime runtime,
        AdminSettingsService adminSettings,
        PluginAdminSettingsService pluginAdminSettings,
        string message = "",
        bool restartRequired = false,
        IReadOnlyList<string>? validationErrors = null,
        string? pluginWarningOverride = null)
    {
        var snapshot = adminSettings.GetSnapshot();
        var readiness = MapChannelReadiness(ChannelReadinessEvaluator.Evaluate(startup.Config, startup.IsNonLoopbackBind))
            .FirstOrDefault(static item => string.Equals(item.ChannelId, "whatsapp", StringComparison.Ordinal));
        var isFirstPartyWorker = string.Equals(snapshot.WhatsAppType, "first_party_worker", StringComparison.OrdinalIgnoreCase)
            || runtime.WhatsAppWorkerHost is not null;
        var pluginTarget = isFirstPartyWorker
            ? new WhatsAppPluginTarget(null, null, null)
            : ResolveWhatsAppPluginTarget(startup, runtime, pluginIdOverride: null);
        var pluginId = pluginTarget.PluginId;
        var pluginEntry = pluginId is null ? null : pluginAdminSettings.GetEntry(pluginId);
        if (pluginEntry is null && pluginId is not null && startup.Config.Plugins.Entries.TryGetValue(pluginId, out var configuredPluginEntry))
            pluginEntry = configuredPluginEntry;

        var warnings = new List<string>();
        if (!string.IsNullOrWhiteSpace(pluginWarningOverride))
            warnings.Add(pluginWarningOverride);
        else if (!string.IsNullOrWhiteSpace(pluginTarget.Warning))
            warnings.Add(pluginTarget.Warning);
        if (readiness is not null)
            warnings.AddRange(readiness.Warnings);
        warnings.Add("WhatsApp secrets are redacted on read. Leave secret values blank to preserve them, or clear both the value and its corresponding *Ref field to remove them.");

        var restartSupported = runtime.ChannelAdapters.TryGetValue("whatsapp", out var adapter)
            && adapter is IRestartableChannelAdapter;

        return new WhatsAppSetupResponse
        {
            ActiveBackend = DetermineActiveWhatsAppBackend(runtime, snapshot),
            ConfiguredType = snapshot.WhatsAppType,
            Message = message,
            RestartRequired = restartRequired,
            Enabled = snapshot.WhatsAppEnabled,
            DmPolicy = snapshot.WhatsAppDmPolicy,
            WebhookPath = snapshot.WhatsAppWebhookPath,
            WebhookPublicBaseUrl = snapshot.WhatsAppWebhookPublicBaseUrl,
            WebhookVerifyToken = "",
            WebhookVerifyTokenConfigured = HasConfiguredSecretValue(snapshot.WhatsAppWebhookVerifyToken, snapshot.WhatsAppWebhookVerifyTokenRef),
            WebhookVerifyTokenRef = snapshot.WhatsAppWebhookVerifyTokenRef,
            ValidateSignature = snapshot.WhatsAppValidateSignature,
            WebhookAppSecret = null,
            WebhookAppSecretConfigured = HasConfiguredSecretValue(snapshot.WhatsAppWebhookAppSecret, snapshot.WhatsAppWebhookAppSecretRef),
            WebhookAppSecretRef = snapshot.WhatsAppWebhookAppSecretRef,
            CloudApiToken = null,
            CloudApiTokenConfigured = HasConfiguredSecretValue(snapshot.WhatsAppCloudApiToken, snapshot.WhatsAppCloudApiTokenRef),
            CloudApiTokenRef = snapshot.WhatsAppCloudApiTokenRef,
            PhoneNumberId = snapshot.WhatsAppPhoneNumberId,
            BusinessAccountId = snapshot.WhatsAppBusinessAccountId,
            BridgeUrl = snapshot.WhatsAppBridgeUrl,
            BridgeToken = null,
            BridgeTokenConfigured = HasConfiguredSecretValue(snapshot.WhatsAppBridgeToken, snapshot.WhatsAppBridgeTokenRef),
            BridgeTokenRef = snapshot.WhatsAppBridgeTokenRef,
            BridgeSuppressSendExceptions = snapshot.WhatsAppBridgeSuppressSendExceptions,
            FirstPartyWorker = snapshot.WhatsAppFirstPartyWorker,
            FirstPartyWorkerConfigJson = PrettyJson(JsonSerializer.SerializeToElement(snapshot.WhatsAppFirstPartyWorker, CoreJsonContext.Default.WhatsAppFirstPartyWorkerConfig)),
            FirstPartyWorkerConfigSchemaJson = GetWhatsAppFirstPartyWorkerConfigSchemaJson(),
            PluginDetected = pluginId is not null,
            PluginId = pluginId,
            PluginConfigJson = pluginEntry?.Config is { } pluginConfig ? PrettyJson(pluginConfig) : null,
            PluginConfigSchemaJson = pluginTarget.Plugin?.Manifest.ConfigSchema is { } pluginSchema ? PrettyJson(pluginSchema) : null,
            PluginUiHintsJson = pluginTarget.Plugin?.Manifest.UiHints is { } uiHints ? PrettyJson(uiHints) : null,
            PluginWarning = pluginWarningOverride ?? pluginTarget.Warning,
            RestartSupported = restartSupported,
            RestartHint = restartSupported
                ? "Runtime restart is available for the active plugin-backed WhatsApp channel."
                : "Built-in WhatsApp configuration changes require a gateway restart.",
            DerivedWebhookUrl = BuildDerivedWebhookUrl(snapshot.WhatsAppWebhookPublicBaseUrl, snapshot.WhatsAppWebhookPath),
            Readiness = readiness,
            AuthStates = runtime.ChannelAuthEvents.GetAll("whatsapp").Select(MapChannelAuthStatusItem).ToArray(),
            Warnings = warnings.Distinct(StringComparer.Ordinal).ToArray(),
            ValidationErrors = validationErrors?.ToArray() ?? []
        };
    }

    private static List<string> ValidateWhatsAppPluginConfig(
        GatewayStartupContext startup,
        GatewayAppRuntime runtime,
        WhatsAppSetupRequest request,
        out string? pluginId,
        out JsonElement? pluginConfig,
        out string? pluginWarning)
    {
        pluginId = null;
        pluginConfig = null;
        pluginWarning = null;

        if (string.IsNullOrWhiteSpace(request.PluginConfigJson) && string.IsNullOrWhiteSpace(request.PluginId))
            return [];

        var pluginTarget = ResolveWhatsAppPluginTarget(startup, runtime, request.PluginId);
        pluginWarning = pluginTarget.Warning;
        pluginId = pluginTarget.PluginId;
        if (pluginId is null)
            return [pluginWarning ?? "No unique plugin-backed WhatsApp channel is available for bridge configuration."];

        if (!string.IsNullOrWhiteSpace(request.PluginConfigJson))
        {
            try
            {
                using var document = JsonDocument.Parse(request.PluginConfigJson);
                pluginConfig = document.RootElement.Clone();
            }
            catch (Exception ex)
            {
                return [$"Plugin config JSON is invalid: {ex.Message}"];
            }
        }

        if (pluginTarget.Plugin?.Manifest is { } manifest)
        {
            var diagnostics = PluginConfigValidator.Validate(manifest, pluginConfig);
            var errors = diagnostics
                .Where(static diagnostic => !string.Equals(diagnostic.Severity, "warning", StringComparison.OrdinalIgnoreCase))
                .Select(static diagnostic => diagnostic.Message)
                .Distinct(StringComparer.Ordinal)
                .ToList();
            if (errors.Count > 0)
                return errors;
        }
        return [];
    }

    private static string DetermineActiveWhatsAppBackend(GatewayAppRuntime runtime, AdminSettingsSnapshot snapshot)
    {
        if (runtime.ChannelAdapters.TryGetValue("whatsapp", out var adapter))
        {
            if (runtime.WhatsAppWorkerHost is not null && adapter is BridgedChannelAdapter)
                return "first_party_worker";

            return adapter switch
            {
                BridgedChannelAdapter => "plugin_bridge",
                WhatsAppBridgeChannel => "built_in_bridge",
                WhatsAppChannel => "official",
                _ => snapshot.WhatsAppType
            };
        }

        if (!snapshot.WhatsAppEnabled)
            return "disabled";

        if (string.Equals(snapshot.WhatsAppType, "first_party_worker", StringComparison.OrdinalIgnoreCase))
            return "first_party_worker";

        return string.Equals(snapshot.WhatsAppType, "bridge", StringComparison.OrdinalIgnoreCase)
            ? "built_in_bridge"
            : "official";
    }

    private static (WhatsAppSetupRequest Request, List<string> Errors) NormalizeWhatsAppSetupRequest(WhatsAppSetupRequest request)
    {
        var errors = new List<string>();
        var workerConfig = request.FirstPartyWorker;
        if (!string.IsNullOrWhiteSpace(request.FirstPartyWorkerConfigJson))
        {
            try
            {
                workerConfig = JsonSerializer.Deserialize(
                    request.FirstPartyWorkerConfigJson,
                    CoreJsonContext.Default.WhatsAppFirstPartyWorkerConfig);
            }
            catch (Exception ex)
            {
                errors.Add($"First-party worker config JSON is invalid: {ex.Message}");
            }
        }

        return (new WhatsAppSetupRequest
        {
            Enabled = request.Enabled,
            Type = request.Type,
            DmPolicy = request.DmPolicy,
            WebhookPath = request.WebhookPath,
            WebhookPublicBaseUrl = request.WebhookPublicBaseUrl,
            WebhookVerifyToken = request.WebhookVerifyToken,
            WebhookVerifyTokenRef = request.WebhookVerifyTokenRef,
            ValidateSignature = request.ValidateSignature,
            WebhookAppSecret = request.WebhookAppSecret,
            WebhookAppSecretRef = request.WebhookAppSecretRef,
            CloudApiToken = request.CloudApiToken,
            CloudApiTokenRef = request.CloudApiTokenRef,
            PhoneNumberId = request.PhoneNumberId,
            BusinessAccountId = request.BusinessAccountId,
            BridgeUrl = request.BridgeUrl,
            BridgeToken = request.BridgeToken,
            BridgeTokenRef = request.BridgeTokenRef,
            BridgeSuppressSendExceptions = request.BridgeSuppressSendExceptions,
            PluginId = request.PluginId,
            PluginConfigJson = request.PluginConfigJson,
            FirstPartyWorker = workerConfig,
            FirstPartyWorkerConfigJson = request.FirstPartyWorkerConfigJson
        }, errors);
    }

    private static string? BuildDerivedWebhookUrl(string? publicBaseUrl, string webhookPath)
    {
        if (string.IsNullOrWhiteSpace(publicBaseUrl) || string.IsNullOrWhiteSpace(webhookPath))
            return null;

        try
        {
            var baseUri = new Uri(publicBaseUrl.TrimEnd('/') + "/", UriKind.Absolute);
            return new Uri(baseUri, webhookPath.TrimStart('/')).ToString();
        }
        catch
        {
            return null;
        }
    }

    private static string PrettyJson(JsonElement value)
        => value.GetRawText();

    private static string GetWhatsAppFirstPartyWorkerConfigSchemaJson()
        => """
           {
             "type": "object",
             "properties": {
               "driver": { "type": "string", "enum": ["baileys", "baileys_csharp", "whatsmeow", "simulated"] },
               "executablePath": { "type": "string" },
               "workingDirectory": { "type": "string" },
               "storagePath": { "type": "string" },
               "mediaCachePath": { "type": "string" },
               "historySync": { "type": "boolean" },
               "proxy": { "type": "string" },
               "accounts": {
                 "type": "array",
                 "items": {
                   "type": "object",
                   "properties": {
                     "accountId": { "type": "string" },
                     "sessionPath": { "type": "string" },
                     "deviceName": { "type": "string" },
                     "pairingMode": { "type": "string", "enum": ["qr", "pairing_code"] },
                     "phoneNumber": { "type": "string" },
                     "sendReadReceipts": { "type": "boolean" },
                     "ackReaction": { "type": "boolean" },
                     "mediaCachePath": { "type": "string" },
                     "historySync": { "type": "boolean" },
                     "proxy": { "type": "string" }
                   },
                   "required": ["accountId", "sessionPath", "pairingMode"]
                 }
               }
             },
             "required": ["driver", "accounts"]
           }
           """;

    private static WhatsAppPluginTarget ResolveWhatsAppPluginTarget(
        GatewayStartupContext startup,
        GatewayAppRuntime runtime,
        string? pluginIdOverride)
    {
        if (runtime.PluginHost is null)
            return new(null, pluginIdOverride is null ? null : $"Plugin '{pluginIdOverride}' is not loaded.", null);

        var registrations = runtime.PluginHost.ChannelRegistrations
            .Where(registration => string.Equals(registration.ChannelId, "whatsapp", StringComparison.Ordinal))
            .ToArray();

        if (!string.IsNullOrWhiteSpace(pluginIdOverride))
            registrations = registrations
                .Where(registration => string.Equals(registration.PluginId, pluginIdOverride, StringComparison.Ordinal))
                .ToArray();

        if (registrations.Length == 0)
        {
            return new(
                null,
                pluginIdOverride is null
                    ? null
                    : $"Plugin '{pluginIdOverride}' is not currently loaded for channel 'whatsapp'.",
                null);
        }

        if (registrations.Length > 1)
            return new(null, "Multiple plugins register channel 'whatsapp'. Configure a specific plugin id.", null);

        var pluginId = registrations[0].PluginId;
        var discovered = PluginDiscovery.DiscoverWithDiagnostics(startup.Config.Plugins, startup.WorkspacePath).Plugins
            .FirstOrDefault(plugin => string.Equals(plugin.Manifest.Id, pluginId, StringComparison.Ordinal));
        return new(pluginId, null, discovered);
    }

    private sealed record WhatsAppPluginTarget(string? PluginId, string? Warning, DiscoveredPlugin? Plugin);

    private static string? SerializeAuditValue(object? value)
    {
        if (value is null)
            return null;

        return value switch
        {
            ProviderPolicyRule item => JsonSerializer.Serialize(item, CoreJsonContext.Default.ProviderPolicyRule),
            PluginOperatorState item => JsonSerializer.Serialize(item, CoreJsonContext.Default.PluginOperatorState),
            ToolApprovalGrant item => JsonSerializer.Serialize(item, CoreJsonContext.Default.ToolApprovalGrant),
            SessionMetadataSnapshot item => JsonSerializer.Serialize(item, CoreJsonContext.Default.SessionMetadataSnapshot),
            ActorRateLimitPolicy item => JsonSerializer.Serialize(item, CoreJsonContext.Default.ActorRateLimitPolicy),
            WebhookDeadLetterEntry item => JsonSerializer.Serialize(item, CoreJsonContext.Default.WebhookDeadLetterEntry),
            AdminSettingsSnapshot item => JsonSerializer.Serialize(item, CoreJsonContext.Default.AdminSettingsSnapshot),
            HeartbeatConfigDto item => JsonSerializer.Serialize(item, CoreJsonContext.Default.HeartbeatConfigDto),
            _ => value.ToString()
        };
    }

    private static SessionState? ParseSessionState(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
            return null;

        return Enum.TryParse<SessionState>(value, ignoreCase: true, out var state)
            ? state
            : null;
    }

    private static string BuildTranscript(Session session)
    {
        var sb = new StringBuilder();
        sb.AppendLine($"Session: {session.Id}");
        sb.AppendLine($"Channel: {session.ChannelId}");
        sb.AppendLine($"Sender: {session.SenderId}");
        sb.AppendLine($"Created: {session.CreatedAt:O}");
        sb.AppendLine($"LastActive: {session.LastActiveAt:O}");
        sb.AppendLine();

        foreach (var turn in session.History)
        {
            sb.AppendLine($"[{turn.Timestamp:O}] {turn.Role}:");
            sb.AppendLine(turn.Content);
            if (turn.ToolCalls is { Count: > 0 })
            {
                sb.AppendLine("Tools:");
                foreach (var call in turn.ToolCalls)
                {
                    sb.AppendLine($"- {call.ToolName}");
                    sb.AppendLine($"  args: {call.Arguments}");
                    if (!string.IsNullOrWhiteSpace(call.Result))
                        sb.AppendLine($"  result: {call.Result}");
                }
            }

            sb.AppendLine();
        }

        return sb.ToString();
    }

    private static string? TryExtractSessionIdFromBranchId(string branchId)
    {
        var marker = ":branch:";
        var index = branchId.IndexOf(marker, StringComparison.Ordinal);
        return index > 0 ? branchId[..index] : null;
    }

    private static async Task<ApprovalSimulationResponse> SimulateApprovalAsync(
        GatewayStartupContext startup,
        GatewayAppRuntime runtime,
        ApprovalSimulationRequest request,
        IServiceProvider services,
        CancellationToken ct)
    {
        var effectiveTooling = CloneToolingConfig(startup.Config.Tooling, request.AutonomyMode);
        var argumentsJson = string.IsNullOrWhiteSpace(request.ArgumentsJson) ? "{}" : request.ArgumentsJson;
        var normalizedToolName = NormalizeApprovalToolName(request.ToolName!);
        var requireToolApproval = request.RequireToolApproval ?? runtime.EffectiveRequireToolApproval;
        var approvalRequiredTools = (request.ApprovalRequiredTools is { Length: > 0 }
                ? request.ApprovalRequiredTools
                : runtime.EffectiveApprovalRequiredTools.ToArray())
            .Where(static item => !string.IsNullOrWhiteSpace(item))
            .Select(NormalizeApprovalToolName)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();
        var execution = ExplainExecutionRoute(startup.Config, normalizedToolName, services);
        var approvalRequired = requireToolApproval && approvalRequiredTools.Contains(normalizedToolName, StringComparer.OrdinalIgnoreCase);

        var autonomyHook = new AutonomyHook(effectiveTooling, NullLogger.Instance);
        var allowed = await autonomyHook.BeforeExecuteAsync(normalizedToolName, argumentsJson, ct);
        if (!allowed)
        {
            return new ApprovalSimulationResponse
            {
                Decision = "deny",
                Reason = "Autonomy or path policy would deny this tool execution.",
                ToolName = request.ToolName!,
                AutonomyMode = (effectiveTooling.AutonomyMode ?? "full").Trim().ToLowerInvariant(),
                AutonomyAllowed = false,
                RequireToolApproval = requireToolApproval,
                ApprovalRequired = approvalRequired,
                BlockingPolicy = "autonomy",
                ExecutionBackend = execution?.BackendName,
                ExecutionFallbackBackend = execution?.FallbackBackend,
                ExecutionTemplate = execution?.Template,
                ExecutionSandboxMode = execution is null ? null : execution.SandboxMode.ToString().ToLowerInvariant(),
                ExecutionRequireWorkspace = execution?.RequireWorkspace,
                ApprovalRequiredTools = approvalRequiredTools
            };
        }

        if (approvalRequired)
        {
            return new ApprovalSimulationResponse
            {
                Decision = "requires_approval",
                Reason = "The effective approval policy requires approval for this tool.",
                ToolName = request.ToolName!,
                AutonomyMode = (effectiveTooling.AutonomyMode ?? "full").Trim().ToLowerInvariant(),
                AutonomyAllowed = true,
                RequireToolApproval = requireToolApproval,
                ApprovalRequired = true,
                BlockingPolicy = "approval",
                ExecutionBackend = execution?.BackendName,
                ExecutionFallbackBackend = execution?.FallbackBackend,
                ExecutionTemplate = execution?.Template,
                ExecutionSandboxMode = execution is null ? null : execution.SandboxMode.ToString().ToLowerInvariant(),
                ExecutionRequireWorkspace = execution?.RequireWorkspace,
                ApprovalRequiredTools = approvalRequiredTools
            };
        }

        return new ApprovalSimulationResponse
        {
            Decision = "allow",
            Reason = "The tool passes autonomy checks and is not currently approval-gated.",
            ToolName = request.ToolName!,
            AutonomyMode = (effectiveTooling.AutonomyMode ?? "full").Trim().ToLowerInvariant(),
            AutonomyAllowed = true,
            RequireToolApproval = requireToolApproval,
            ApprovalRequired = false,
            BlockingPolicy = null,
            ExecutionBackend = execution?.BackendName,
            ExecutionFallbackBackend = execution?.FallbackBackend,
            ExecutionTemplate = execution?.Template,
            ExecutionSandboxMode = execution is null ? null : execution.SandboxMode.ToString().ToLowerInvariant(),
            ExecutionRequireWorkspace = execution?.RequireWorkspace,
            ApprovalRequiredTools = approvalRequiredTools
        };
    }

    private static ToolExecutionRouter.ExecutionRouteResolution? ExplainExecutionRoute(
        GatewayConfig config,
        string normalizedToolName,
        IServiceProvider services)
    {
        var router = services.GetService<ToolExecutionRouter>()
            ?? new ToolExecutionRouter(
                config,
                services.GetService<IToolSandbox>(),
                services.GetService<ILoggerFactory>()?.CreateLogger<ToolExecutionRouter>());

        return normalizedToolName switch
        {
            "process" => router.ResolveBackendForProcess(),
            "shell" => ResolveShellExecutionRoute(config),
            _ => null
        };
    }

    private static ToolExecutionRouter.ExecutionRouteResolution ResolveShellExecutionRoute(GatewayConfig config)
    {
        if (config.Execution.Enabled &&
            config.Execution.Tools.TryGetValue("shell", out var shellRoute) &&
            !string.IsNullOrWhiteSpace(shellRoute.Backend))
        {
            return new ToolExecutionRouter.ExecutionRouteResolution(
                shellRoute.Backend,
                shellRoute.FallbackBackend,
                config.Execution.Profiles.TryGetValue(shellRoute.Backend, out var shellProfile) ? shellProfile.Image : null,
                shellRoute.RequireWorkspace,
                ToolSandboxMode.None);
        }

        var sandboxMode = ToolSandboxPolicy.ResolveMode(config, "shell", ToolSandboxMode.Prefer);
        if (sandboxMode != ToolSandboxMode.None && ToolSandboxPolicy.IsOpenSandboxProviderConfigured(config))
        {
            return new ToolExecutionRouter.ExecutionRouteResolution(
                "opensandbox",
                null,
                ToolSandboxPolicy.ResolveTemplate(config, "shell"),
                RequireWorkspace: false,
                sandboxMode);
        }

        var backendName = config.Execution.DefaultBackend;
        var requireWorkspace = config.Execution.Profiles.TryGetValue(backendName, out var profile) &&
            (profile.Type.Equals(ExecutionBackendType.Docker, StringComparison.OrdinalIgnoreCase) ||
             profile.Type.Equals(ExecutionBackendType.Ssh, StringComparison.OrdinalIgnoreCase));
        return new ToolExecutionRouter.ExecutionRouteResolution(
            backendName,
            null,
            config.Execution.Profiles.TryGetValue(backendName, out profile) ? profile.Image : null,
            requireWorkspace,
            sandboxMode);
    }

    private static ToolingConfig CloneToolingConfig(ToolingConfig source, string? autonomyModeOverride)
        => new()
        {
            AutonomyMode = string.IsNullOrWhiteSpace(autonomyModeOverride) ? source.AutonomyMode : autonomyModeOverride,
            WorkspaceRoot = source.WorkspaceRoot,
            WorkspaceOnly = source.WorkspaceOnly,
            AllowedShellCommandGlobs = source.AllowedShellCommandGlobs,
            ForbiddenPathGlobs = source.ForbiddenPathGlobs,
            AllowShell = source.AllowShell,
            ReadOnlyMode = source.ReadOnlyMode,
            AllowedReadRoots = source.AllowedReadRoots,
            AllowedWriteRoots = source.AllowedWriteRoots,
            ToolTimeoutSeconds = source.ToolTimeoutSeconds,
            ParallelToolExecution = source.ParallelToolExecution,
            RequireToolApproval = source.RequireToolApproval,
            ApprovalRequiredTools = source.ApprovalRequiredTools,
            ToolApprovalTimeoutSeconds = source.ToolApprovalTimeoutSeconds,
            EnableBrowserTool = source.EnableBrowserTool,
            AllowBrowserEvaluate = source.AllowBrowserEvaluate,
            BrowserHeadless = source.BrowserHeadless,
            BrowserTimeoutSeconds = source.BrowserTimeoutSeconds
        };

    private static string NormalizeApprovalToolName(string toolName)
        => string.Equals(toolName.Trim(), "file_write", StringComparison.Ordinal)
            ? "write_file"
            : toolName.Trim();

    private static ApprovalHistoryEntry RedactApprovalHistory(ApprovalHistoryEntry entry)
        => new()
        {
            EventType = entry.EventType,
            ApprovalId = entry.ApprovalId,
            SessionId = entry.SessionId,
            ChannelId = entry.ChannelId,
            SenderId = entry.SenderId,
            ToolName = entry.ToolName,
            ArgumentsPreview = RedactSensitiveText(entry.ArgumentsPreview),
            TimestampUtc = entry.TimestampUtc,
            DecisionAtUtc = entry.DecisionAtUtc,
            ActorChannelId = entry.ActorChannelId,
            ActorSenderId = entry.ActorSenderId,
            DecisionSource = entry.DecisionSource,
            Approved = entry.Approved
        };

    private static RuntimeEventEntry RedactRuntimeEvent(RuntimeEventEntry entry)
        => new()
        {
            Id = entry.Id,
            TimestampUtc = entry.TimestampUtc,
            SessionId = entry.SessionId,
            ChannelId = entry.ChannelId,
            SenderId = entry.SenderId,
            CorrelationId = entry.CorrelationId,
            Component = entry.Component,
            Action = entry.Action,
            Severity = entry.Severity,
            Summary = RedactSensitiveText(entry.Summary),
            Metadata = entry.Metadata?.ToDictionary(
                static kvp => kvp.Key,
                static kvp => ShouldRedactKey(kvp.Key) ? "[redacted]" : RedactSensitiveText(kvp.Value),
                StringComparer.Ordinal)
        };

    private static WebhookDeadLetterEntry RedactDeadLetter(WebhookDeadLetterEntry entry)
        => new()
        {
            Id = entry.Id,
            Source = entry.Source,
            DeliveryKey = entry.DeliveryKey,
            EndpointName = entry.EndpointName,
            ChannelId = entry.ChannelId,
            SenderId = entry.SenderId,
            SessionId = entry.SessionId,
            CreatedAtUtc = entry.CreatedAtUtc,
            Error = RedactSensitiveText(entry.Error),
            PayloadPreview = RedactSensitiveText(entry.PayloadPreview),
            Discarded = entry.Discarded,
            ReplayedAtUtc = entry.ReplayedAtUtc
        };

    private static string RedactSensitiveText(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
            return string.Empty;

        var redacted = value.Replace("raw:", "raw:[redacted]", StringComparison.OrdinalIgnoreCase);
        foreach (var marker in new[] { "token", "secret", "password", "apikey", "authorization" })
        {
            if (redacted.IndexOf(marker, StringComparison.OrdinalIgnoreCase) >= 0)
                return "[redacted]";
        }

        return redacted;
    }

    private static bool ShouldRedactKey(string key)
        => key.Contains("token", StringComparison.OrdinalIgnoreCase)
           || key.Contains("secret", StringComparison.OrdinalIgnoreCase)
           || key.Contains("password", StringComparison.OrdinalIgnoreCase)
           || key.Contains("authorization", StringComparison.OrdinalIgnoreCase)
           || key.Contains("apikey", StringComparison.OrdinalIgnoreCase);

    private static bool HasConfiguredSecretValue(string? value, string? valueRef)
        => !string.IsNullOrWhiteSpace(SecretResolver.Resolve(valueRef) ?? value);

    internal static async Task StreamChannelAuthEventsAsync(
        HttpContext ctx,
        ChannelAuthEventStore authEventStore,
        string channelId,
        string? accountId)
    {
        ctx.Response.ContentType = "text/event-stream";
        ctx.Response.Headers.CacheControl = "no-cache";
        ctx.Response.Headers.Connection = "keep-alive";

        using var subscription = authEventStore.Subscribe();
        var ct = ctx.RequestAborted;

        try
        {
            await WriteSseCommentAsync(ctx, "stream-open", ct);

            var currentItems = accountId is not null
                ? authEventStore.GetLatest(channelId, accountId) is { } currentEvt ? [currentEvt] : []
                : authEventStore.GetAll(channelId);
            foreach (var current in currentItems)
            {
                await WriteChannelAuthEventAsync(ctx, current, ct);
            }

            await foreach (var evt in subscription.Reader.ReadAllAsync(ct))
            {
                if (!string.Equals(evt.ChannelId, channelId, StringComparison.Ordinal))
                    continue;
                if (accountId is not null && !string.Equals(evt.AccountId, accountId, StringComparison.Ordinal))
                    continue;

                await WriteChannelAuthEventAsync(ctx, evt, ct);
            }
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            // RequestAborted is the normal shutdown path for disconnected SSE clients.
        }
    }

    private static async Task WriteChannelAuthEventAsync(HttpContext ctx, BridgeChannelAuthEvent evt, CancellationToken cancellationToken)
    {
        var json = JsonSerializer.Serialize(MapChannelAuthStatusItem(evt), CoreJsonContext.Default.ChannelAuthStatusItem);
        await ctx.Response.WriteAsync($"data: {json}\n\n", cancellationToken);
        await ctx.Response.Body.FlushAsync(cancellationToken);
    }

    private static async Task WriteSseCommentAsync(HttpContext ctx, string comment, CancellationToken cancellationToken)
    {
        await ctx.Response.WriteAsync($": {comment}\n\n", cancellationToken);
        await ctx.Response.Body.FlushAsync(cancellationToken);
    }

    private readonly record struct JsonBodyReadResult<T>(
        T? Value,
        IResult? Failure)
        where T : class;
}

using System.Diagnostics;
using System.Runtime.CompilerServices;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Threading.Channels;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Logging;
using OpenClaw.Agent.Execution;
using OpenClaw.Agent.Routing;
using OpenClaw.Core.Abstractions;
using OpenClaw.Core.Memory;
using OpenClaw.Core.Models;
using OpenClaw.Core.Observability;
using OpenClaw.Core.Security;
using OpenClaw.Core.Skills;

namespace OpenClaw.Agent;

/// <summary>
/// Delegate for interactive tool approval. Returns true to allow, false to deny.
/// </summary>
public delegate ValueTask<bool> ToolApprovalCallback(string toolName, string arguments, CancellationToken ct);

/// <summary>
/// The agent loop: receives a user message, builds context from session history + memory,
/// calls the LLM, executes tool calls, and returns the final response.
/// Uses Microsoft.Extensions.AI for provider-agnostic LLM access (thin, AOT-friendly).
/// Includes retry with exponential backoff, per-call timeout, circuit breaker,
/// streaming, parallel tool execution, context compaction, hooks, and tool approval.
/// </summary>
public sealed class AgentRuntime : IAgentRuntime
{
    private readonly OpenClawToolExecutor _toolExecutor;
    private readonly AgentCheckpointManager _checkpoints;
    private readonly AgentToolCallLoop _toolLoop;
    private readonly AgentTurnAccounting _accounting;
    private readonly AgentModelExecutor _modelExecutor;
    private readonly AgentPromptContextAssembler _contextAssembler;
    private readonly ILogger? _logger;
    private string _systemPrompt = string.Empty;
    private readonly int _maxTokens;
    private readonly int _maxIterations;
    private readonly float _temperature;
    private readonly int _maxHistoryTurns;
    private readonly bool _parallelToolExecution;
    private readonly bool _enableCompaction;
    private readonly int _compactionThreshold;
    private readonly int _compactionKeepRecent;
    private readonly bool _requireToolApproval;
    private readonly HashSet<string> _approvalRequiredTools;
    private readonly IReadOnlyList<IToolHook> _hooks;
    private readonly CircuitBreaker _circuitBreaker;
    private readonly RuntimeMetrics? _metrics;
    private readonly ProviderUsageTracker? _providerUsage;
    private readonly ILlmExecutionService? _llmExecutionService;
    private readonly IGoalService? _goalService;
    private readonly Agent.Goal.AgentRuntimeGoalIntegration? _goalIntegration;
    private readonly long _sessionTokenBudget;
    private readonly bool _estimateTokenBudgetAdmission;
    private readonly LlmProviderConfig _config;
    private readonly Action<Session, string, string, long, long>? _recordContractTurnUsage;
    private readonly SkillsConfig? _skillsConfig;
    private readonly bool _metaSkillsEnabled;
    private readonly string? _skillWorkspacePath;
    private readonly IReadOnlyList<string> _pluginSkillDirs;
    private readonly IRedactionPipeline _redaction;
    private readonly ISentinelSubstitutionService _sentinelSubstitution;
    private readonly string? _memoryRecallPrefix;
    private readonly FractalMemoryConfig? _fractalMemory;
    private readonly bool _backgroundExecutionEnabled;
    private readonly ITurnRoutingPolicy _turnRoutingPolicy;
    private readonly object _skillGate = new();
    private string[] _loadedSkillNames = [];
    private IReadOnlyList<SkillDefinition> _loadedSkills = [];
    private int _skillPromptLength;

    public AgentRuntime(
        IChatClient chatClient,
        IReadOnlyList<ITool> tools,
        IMemoryStore memory,
        LlmProviderConfig config,
        int maxHistoryTurns,
        IReadOnlyList<SkillDefinition>? skills = null,
        SkillsConfig? skillsConfig = null,
        string? skillWorkspacePath = null,
        IReadOnlyList<string>? pluginSkillDirs = null,
        ILogger? logger = null,
        int toolTimeoutSeconds = 30,
        RuntimeMetrics? metrics = null,
        ProviderUsageTracker? providerUsage = null,
        ITurnTokenUsageObserver? turnTokenUsageObserver = null,
        ILlmExecutionService? llmExecutionService = null,
        bool parallelToolExecution = true,
        bool enableCompaction = false,
        int compactionThreshold = 40,
        int compactionKeepRecent = 10,
        bool requireToolApproval = false,
        string[]? approvalRequiredTools = null,
        int maxIterations = 10,
        IReadOnlyList<IToolHook>? hooks = null,
        long sessionTokenBudget = 0,
        MemoryRecallConfig? recall = null,
        IUserProfileStore? profileStore = null,
        ProfilesConfig? profilesConfig = null,
        IToolSandbox? toolSandbox = null,
        GatewayConfig? gatewayConfig = null,
        ToolUsageTracker? toolUsageTracker = null,
        ToolExecutionRouter? executionRouter = null,
        IToolPresetResolver? toolPresetResolver = null,
        Func<Session, bool>? isContractTokenBudgetExceeded = null,
        Func<Session, bool>? isContractRuntimeBudgetExceeded = null,
        Action<Session, string, string, long, long>? recordContractTurnUsage = null,
        Action<Session, string>? appendContractSnapshot = null,
        ToolAuditLog? toolAuditLog = null,
        IRedactionPipeline? redaction = null,
        ISentinelSubstitutionService? sentinelSubstitution = null,
        IToolGovernanceService? toolGovernance = null,
        IPlanExecuteVerifyOrchestrator? planExecuteVerify = null,
        ContextBudgetPlanner? contextBudgetPlanner = null,
        ITurnRoutingPolicy? turnRoutingPolicy = null,
        IGoalService? goalService = null,
        IReadOnlyList<IToolResultInterceptor>? interceptors = null)
    {
        _logger = logger;
        _config = config;
        _maxTokens = config.MaxTokens;
        _maxIterations = Math.Max(1, maxIterations);
        _temperature = config.Temperature;
        _maxHistoryTurns = Math.Max(1, maxHistoryTurns);
        _parallelToolExecution = parallelToolExecution;
        _enableCompaction = enableCompaction;
        _compactionThreshold = Math.Max(4, compactionThreshold);
        _compactionKeepRecent = Math.Max(2, compactionKeepRecent);
        _requireToolApproval = requireToolApproval;
        _approvalRequiredTools = NormalizeApprovalRequiredTools(approvalRequiredTools);
        _hooks = hooks ?? [];
        _metrics = metrics;
        _providerUsage = providerUsage;
        _llmExecutionService = llmExecutionService;
        _goalService = goalService;
        _goalIntegration = goalService is not null
            ? new Agent.Goal.AgentRuntimeGoalIntegration(goalService, logger)
            : null;
        _skillsConfig = skillsConfig;
        _metaSkillsEnabled = skillsConfig?.MetaSkill.Enabled ?? true;
        _skillWorkspacePath = skillWorkspacePath;
        _pluginSkillDirs = pluginSkillDirs ?? [];
        _redaction = redaction ?? new NoopRedactionPipeline();
        _sentinelSubstitution = sentinelSubstitution ?? new NoopSentinelSubstitutionService();
        _circuitBreaker = new CircuitBreaker(
            config.CircuitBreakerThreshold,
            TimeSpan.FromSeconds(config.CircuitBreakerCooldownSeconds),
            logger);

        _toolExecutor = new OpenClawToolExecutor(
            tools,
            toolTimeoutSeconds,
            requireToolApproval,
            [.. _approvalRequiredTools],
            _hooks,
            metrics,
            logger,
            config: gatewayConfig,
            toolSandbox: toolSandbox,
            toolUsageTracker: toolUsageTracker,
            executionRouter: executionRouter,
            toolPresetResolver: toolPresetResolver,
            redaction: _redaction,
            sentinelSubstitution: _sentinelSubstitution,
            toolGovernance: toolGovernance,
            planExecuteVerify: planExecuteVerify,
            auditLog: toolAuditLog,
            interceptors: interceptors,
            metaInvokeExecutor: (session, skillName, input, token) => ExecuteMetaSkillAsync(session, skillName, input, token));
        _sessionTokenBudget = sessionTokenBudget;
        _estimateTokenBudgetAdmission = gatewayConfig?.EnableEstimatedTokenAdmissionControl ?? false;
        _fractalMemory = gatewayConfig?.Memory.Fractal;
        _backgroundExecutionEnabled = gatewayConfig?.BackgroundExecution.Enabled ?? false;
        _turnRoutingPolicy = turnRoutingPolicy ?? NoopTurnRoutingPolicy.Instance;
        _recordContractTurnUsage = recordContractTurnUsage;
        var projectId = gatewayConfig?.Memory.ProjectId
            ?? Environment.GetEnvironmentVariable("OPENCLAW_PROJECT");
        _memoryRecallPrefix = string.IsNullOrWhiteSpace(projectId) ? null : $"project:{projectId.Trim()}:";
        _checkpoints = new AgentCheckpointManager(memory, logger);
        _toolLoop = new AgentToolCallLoop(_toolExecutor, _parallelToolExecution);
        _accounting = new AgentTurnAccounting(metrics, providerUsage, config, _sessionTokenBudget,
            _estimateTokenBudgetAdmission, turnTokenUsageObserver, () => CircuitBreakerState,
            isContractTokenBudgetExceeded, isContractRuntimeBudgetExceeded, recordContractTurnUsage,
            appendContractSnapshot, logger);
        _modelExecutor = new AgentModelExecutor(chatClient, config, _circuitBreaker, llmExecutionService, _accounting, logger);
        _contextAssembler = new AgentPromptContextAssembler(memory, requireToolApproval, recall, profileStore,
            profilesConfig, contextBudgetPlanner, _fractalMemory, metrics, logger, _memoryRecallPrefix);
        ApplySkills(skills ?? []);
    }

    public IReadOnlyList<string> LoadedSkillNames
    {
        get
        {
            lock (_skillGate)
            {
                return _loadedSkillNames;
            }
        }
    }

    public IReadOnlyList<SkillDefinition> LoadedSkills
    {
        get
        {
            lock (_skillGate)
            {
                return _loadedSkills;
            }
        }
    }

    public Task<IReadOnlyList<string>> ReloadSkillsAsync(CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();

        if (_skillsConfig is null)
            return Task.FromResult<IReadOnlyList<string>>(LoadedSkillNames);

        var logger = _logger ?? NullLogger.Instance;
        var skills = SkillLoader.LoadAll(_skillsConfig, _skillWorkspacePath, logger, _pluginSkillDirs);
        ApplySkills(skills);

        if (skills.Count > 0)
            logger.LogInformation("{Summary}", SkillPromptBuilder.BuildSummary(skills));
        else
            logger.LogInformation("No skills loaded.");

        return Task.FromResult<IReadOnlyList<string>>(LoadedSkillNames);
    }

    public Task ApplyMcpToolChangesAsync(
        IReadOnlyList<ITool> toAdd,
        IReadOnlyList<string> toRemove,
        CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();
        _toolExecutor.ReplaceMcpTools(toAdd, toRemove);
        return Task.CompletedTask;
    }

    /// <summary>
    /// Exposes the circuit breaker state for health/metrics endpoints.
    /// </summary>
    public CircuitState CircuitBreakerState => _llmExecutionService?.DefaultCircuitState ?? _circuitBreaker.State;

    private static string ResolveCorrelationId(string? correlationId)
        => !string.IsNullOrWhiteSpace(correlationId)
            ? correlationId.Trim()
            : Activity.Current?.TraceId.ToString() ?? Guid.NewGuid().ToString("N")[..16];

    /// <summary>
    /// Run the agent loop for a single user turn. Supports multi-step tool use,
    /// parallel tool execution, hooks, and optional tool approval.
    /// </summary>
    public async Task<string> RunAsync(
        Session session, string userMessage, CancellationToken ct,
        ToolApprovalCallback? approvalCallback = null,
        JsonElement? responseSchema = null,
        string? correlationId = null)
    {
        var result = await RunTurnAsync(session, userMessage, ct, approvalCallback, responseSchema, correlationId);
        return result.Text;
    }

    /// <inheritdoc />
    public async Task<AgentTurnResult> RunTurnAsync(
        Session session, string userMessage, CancellationToken ct,
        ToolApprovalCallback? approvalCallback = null,
        JsonElement? responseSchema = null,
        string? correlationId = null)
    {
        using var activity = Telemetry.ActivitySource.StartActivity("Agent.RunAsync");
        activity?.SetTag("session.id", session.Id);
        activity?.SetTag("channel.id", session.ChannelId);

        _goalService?.BeginTurn(session.Id);
        var resolvedCorrelationId = ResolveCorrelationId(correlationId);
        var turnCtx = new TurnContext
        {
            CorrelationId = resolvedCorrelationId,
            SessionId = session.Id,
            ChannelId = session.ChannelId
        };
        userMessage = _redaction.Redact(userMessage);

        _metrics?.IncrementRequests();
        _logger?.LogInformation("[{CorrelationId}] Turn start session={SessionId} channel={ChannelId}",
            turnCtx.CorrelationId, session.Id, session.ChannelId);

        if (TryRejectContractBudget(session, out var contractBudgetMessage))
        {
            AppendContractSnapshot(session, "budget_exceeded");
            LogTurnComplete(turnCtx);
            return AgentTurnResult.Completed(contractBudgetMessage);
        }

        var resumeCheckpoint = AgentCheckpointManager.TryGetResumableCheckpoint(session);
        if (resumeCheckpoint is null)
        {
            // Record user turn
            session.History.Add(new ChatTurn { Role = "user", Content = userMessage });

            // Compaction or simple trim
            if (_enableCompaction)
                await CompactHistoryAsync(session, ct, resolvedCorrelationId);
            else
                TrimHistory(session);
        }
        else
        {
            resumeCheckpoint.LastResumeAttemptAtUtc = DateTimeOffset.UtcNow;
            _logger?.LogInformation(
                "[{CorrelationId}] Resuming session={SessionId} from checkpoint {CheckpointId}",
                turnCtx.CorrelationId,
                session.Id,
                resumeCheckpoint.CheckpointId);
        }

            using var turnRoutingScope = await ApplyTurnRoutingAsync(session, userMessage, resumeCheckpoint is not null, responseSchema, ct);

        // Build conversation for LLM
        var messages = BuildMessages(session, exactLatestToolBatch: resumeCheckpoint is not null, userMessage: userMessage);
        if (resumeCheckpoint is not null)
        {
            messages.Insert(1, new ChatMessage(ChatRole.System, AgentCheckpointManager.BuildCheckpointResumeInstruction(resumeCheckpoint)));
            if (!AgentCheckpointManager.IsBareResumeRequest(userMessage))
                messages.Add(new ChatMessage(ChatRole.User, AgentCheckpointManager.BuildCheckpointResumeUserNote(userMessage)));
        }
        else
        {
            // Order matters: memory recall first, then profile recall (inserted near conversation start).
            var memoryRecallInjected = await TryInjectRecallAsync(messages, userMessage, ct);
            await TryInjectStructuredMemoryContextAsync(messages, session, userMessage, memoryRecallInjected, ct);
            await TryInjectProfileRecallAsync(messages, session, ct);
        }

        // Inject Goal activation prompt if a goal is active
        if (_goalIntegration is not null)
        {
            var goalPrompt = _goalIntegration.BuildGoalSystemPrompt(session.Id);
            if (goalPrompt is not null)
            {
                // Insert after system prompt but before user message
                messages.Insert(1, new ChatMessage(ChatRole.System, goalPrompt));
                _logger?.LogInformation("[{CorrelationId}] Goal activation prompt injected", turnCtx.CorrelationId);
            }
        }

        // Build tool definitions for the LLM (use pre-cached declarations)
        var chatOptions = new ChatOptions
        {
            ModelId = session.ModelOverride ?? _config.Model,
            MaxOutputTokens = _maxTokens,
            Temperature = _temperature,
            Tools = _toolExecutor.GetToolDeclarations(session),
            ResponseFormat = responseSchema.HasValue
                ? ChatResponseFormat.ForJsonSchema(responseSchema.Value, "response")
                : null
        };

        if (!string.IsNullOrWhiteSpace(session.ReasoningEffort))
        {
            chatOptions.AdditionalProperties ??= new AdditionalPropertiesDictionary();
            chatOptions.AdditionalProperties["reasoning_effort"] = session.ReasoningEffort;
        }

        for (var i = 0; i < _maxIterations; i++)
        {
            // Mid-turn budget check: stop if token budget is exceeded
            if (_sessionTokenBudget > 0 && session.GetTotalTokens() >= _sessionTokenBudget)
            {
                _logger?.LogInformation("[{CorrelationId}] Session token budget exceeded mid-turn ({Used}/{Budget})",
                    turnCtx.CorrelationId, session.GetTotalTokens(), _sessionTokenBudget);
                LogTurnComplete(turnCtx);
                return new AgentTurnResult
                {
                    Text = "You've reached the token limit for this session. Please start a new conversation.",
                    ShouldContinue = false,
                    StopReason = AgentTurnStopReason.BudgetLimited
                };
            }

            if (TryRejectContractBudget(session, out contractBudgetMessage))
            {
                AppendContractSnapshot(session, "budget_exceeded");
                LogTurnComplete(turnCtx);
                return AgentTurnResult.Completed(contractBudgetMessage);
            }

            LlmExecutionResult? executionResult = null;
            var llmSw = Stopwatch.StartNew();
            try
            {
                executionResult = await CallLlmWithResilienceAsync(session, messages, chatOptions, turnCtx, ct);
            }
            catch (CircuitOpenException coe)
            {
                _logger?.LogWarning("[{CorrelationId}] Circuit breaker open — retry after {RetryAfter}s",
                    turnCtx.CorrelationId, coe.RetryAfter.TotalSeconds);
                LogTurnComplete(turnCtx);
                return new AgentTurnResult
                {
                    Text = coe.Message,
                    ShouldContinue = false,
                    StopReason = AgentTurnStopReason.Failed
                };
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                throw;
            }
            catch (EstimatedBudgetAdmissionException ex)
            {
                LogTurnComplete(turnCtx);
                return new AgentTurnResult
                {
                    Text = ex.Message,
                    ShouldContinue = false,
                    StopReason = AgentTurnStopReason.BudgetLimited
                };
            }
            catch (ModelSelectionException ex)
            {
                _logger?.LogWarning("[{CorrelationId}] Model selection failed: {Message}", turnCtx.CorrelationId, ex.Message);
                LogTurnComplete(turnCtx);
                return new AgentTurnResult
                {
                    Text = ex.Message,
                    ShouldContinue = false,
                    StopReason = AgentTurnStopReason.Failed
                };
            }

            catch (Exception ex) when (IsExpectedLlmFailure(ex))
            {
                _metrics?.IncrementLlmErrors();
                _logger?.LogError(ex, "[{CorrelationId}] LLM call failed after all retries and fallbacks", turnCtx.CorrelationId);
                LogTurnComplete(turnCtx);
                return AgentTurnResult.Completed("Sorry, I'm having trouble reaching my AI provider right now. Please try again shortly.");
            }
            llmSw.Stop();

            if (executionResult is null)
            {
                 LogTurnComplete(turnCtx);
                 return AgentTurnResult.Completed("Sorry, I'm having trouble reaching my AI provider right now. Please try again shortly.");
            }

            var response = executionResult.Response;

            // Extract token usage from response
            var inputTokens = response.Usage?.InputTokenCount ?? 0;
            var outputTokens = response.Usage?.OutputTokenCount ?? 0;
            var cacheUsage = PromptCacheUsageExtractor.FromUsage(response.Usage);
            turnCtx.RecordLlmCall(llmSw.Elapsed, inputTokens, outputTokens);
            _metrics?.IncrementLlmCalls();
            _metrics?.AddInputTokens(inputTokens);
            _metrics?.AddOutputTokens(outputTokens);
            _metrics?.AddPromptCacheReads(cacheUsage.CacheReadTokens);
            _metrics?.AddPromptCacheWrites(cacheUsage.CacheWriteTokens);
            _providerUsage?.AddTokens(executionResult.ProviderId, executionResult.ModelId, inputTokens, outputTokens);
            _providerUsage?.AddCacheTokens(executionResult.ProviderId, executionResult.ModelId, cacheUsage.CacheReadTokens, cacheUsage.CacheWriteTokens);

            // Track token usage on the session
            session.AddTokenUsage(inputTokens, outputTokens);
            session.AddCacheUsage(cacheUsage.CacheReadTokens, cacheUsage.CacheWriteTokens);
            _recordContractTurnUsage?.Invoke(session, executionResult.ProviderId, executionResult.ModelId, inputTokens, outputTokens);
            RecordTurnUsage(
                session,
                executionResult.ProviderId,
                executionResult.ModelId,
                inputTokens,
                outputTokens,
                cacheUsage.CacheReadTokens,
                cacheUsage.CacheWriteTokens,
                response.Usage is null
                    ? LlmExecutionEstimateBuilder.BuildInputTokenEstimate(messages, inputTokens, _skillPromptLength)
                    : new InputTokenComponentEstimate(),
                isEstimated: response.Usage is null,
                correlationId: turnCtx.CorrelationId);

            if (TryRejectContractBudget(session, out contractBudgetMessage))
            {
                AppendContractSnapshot(session, "budget_exceeded");
                LogTurnComplete(turnCtx);
                return AgentTurnResult.Completed(contractBudgetMessage);
            }

            // Check for tool calls
            var toolCalls = response.Messages
                .SelectMany(m => m.Contents.OfType<FunctionCallContent>())
                .ToList();

            if (toolCalls.Count == 0)
            {
                // Final text response
                var text = _redaction.Redact(response.Text ?? "");

                // ── Goal continuation check ──
                if (_goalIntegration is not null)
                {
                    _goalIntegration.UpdateGoalTokenUsage(session);
                    var continuationPrompt = _goalIntegration.EvaluateGoalContinuation(
                        session, i, _maxIterations, text);
                    if (continuationPrompt is not null)
                    {
                        messages.Add(new ChatMessage(ChatRole.System, continuationPrompt));
                        session.History.Add(new ChatTurn
                        {
                            Role = "system",
                            Content = $"[goal_check:{i}] Continue working toward objective..."
                        });
                        _logger?.LogInformation(
                            "[{CorrelationId}] Goal auto-continue iteration {Iter}/{Max}",
                            turnCtx.CorrelationId, i + 1, _maxIterations);
                        continue; // ← Don't return — continue the loop
                    }
                }
                // ── End Goal continuation check ──

                session.History.Add(new ChatTurn { Role = "assistant", Content = text });
                AgentCheckpointManager.MarkCheckpointCompleted(session, SessionCheckpointStates.Completed, "final_response");
                AppendContractSnapshot(session, "active");
                LogTurnComplete(turnCtx);
                return AgentTurnResult.Completed(text);
            }

            // Execute tool calls (parallel or sequential based on config)
            var (invocations, toolResults) = await ExecuteToolCallsAsync(
                toolCalls, session, turnCtx, isStreaming: false, approvalCallback, ct);

            // Feed all tool calls as a single assistant message, then all results as a single tool message
            messages.Add(new ChatMessage(ChatRole.Assistant, toolCalls.Cast<AIContent>().ToList()));
            messages.Add(new ChatMessage(ChatRole.Tool, toolResults.Cast<AIContent>().ToList()));

            session.History.Add(new ChatTurn
            {
                Role = "assistant",
                Content = "[tool_use]",
                ToolCalls = invocations
            });

            // Compaction is NOT run inside the iteration loop to avoid cascading LLM calls.
            // It runs once at the start of the turn (before the loop).
            TrimHistory(session);
            await _checkpoints.PersistToolBatchCheckpointAsync(session, turnCtx, i, invocations, ct);
        }

        AgentCheckpointManager.MarkCheckpointCompleted(session, SessionCheckpointStates.Failed, "max_iterations");
        AppendContractSnapshot(session, "active");
        LogTurnComplete(turnCtx);

        var hasActiveGoal = _goalIntegration?.BuildGoalSystemPrompt(session.Id) is not null;
        var canContinue = _backgroundExecutionEnabled;
        return new AgentTurnResult
        {
            Text = canContinue
                ? "I've reached the maximum number of tool iterations. Continuing in the background."
                : "I've reached the maximum number of tool iterations. Task requires more work.",
            ShouldContinue = canContinue,
            StopReason = AgentTurnStopReason.BatchLimitReached,
            ContinuePrompt = canContinue
                ? (hasActiveGoal ? "Continue working toward the active goal." : "Continue working on the task.")
                : null
        };
    }

    /// <summary>
    /// Run the agent loop with streaming. Yields incremental events (text deltas, tool status)
    /// for real-time delivery to WebSocket clients.
    /// </summary>
    public async IAsyncEnumerable<AgentStreamEvent> RunStreamingAsync(
        Session session, string userMessage,
        [EnumeratorCancellation] CancellationToken ct,
        ToolApprovalCallback? approvalCallback = null,
        string? correlationId = null)
    {
        using var activity = Telemetry.ActivitySource.StartActivity("Agent.RunStreamingAsync");
        activity?.SetTag("session.id", session.Id);
        activity?.SetTag("channel.id", session.ChannelId);

        _goalService?.BeginTurn(session.Id);
        var resolvedCorrelationId = ResolveCorrelationId(correlationId);
        var turnCtx = new TurnContext
        {
            CorrelationId = resolvedCorrelationId,
            SessionId = session.Id,
            ChannelId = session.ChannelId
        };
        userMessage = _redaction.Redact(userMessage);

        _metrics?.IncrementRequests();
        _logger?.LogInformation("[{CorrelationId}] Streaming turn start session={SessionId} channel={ChannelId}",
            turnCtx.CorrelationId, session.Id, session.ChannelId);

        if (TryRejectContractBudget(session, out var contractBudgetMessage))
        {
            yield return AgentStreamEvent.ErrorOccurred(contractBudgetMessage, "contract_budget_exceeded");
            yield return AgentStreamEvent.Complete();
            AppendContractSnapshot(session, "budget_exceeded");
            LogTurnComplete(turnCtx);
            yield break;
        }

        if (_requireToolApproval && approvalCallback is null)
        {
            _logger?.LogWarning(
                "[{CorrelationId}] Streaming session has RequireToolApproval=true but no approval callback is registered — protected tools will be auto-denied. " +
                "Connect through /chat for interactive approvals, or set OpenClaw:Tooling:RequireToolApproval=false for trusted local sessions.",
                turnCtx.CorrelationId);
        }

        var resumeCheckpoint = AgentCheckpointManager.TryGetResumableCheckpoint(session);
        if (resumeCheckpoint is null)
        {
            session.History.Add(new ChatTurn { Role = "user", Content = userMessage });

            if (_enableCompaction)
                await CompactHistoryAsync(session, ct, resolvedCorrelationId);
            else
                TrimHistory(session);
        }
        else
        {
            resumeCheckpoint.LastResumeAttemptAtUtc = DateTimeOffset.UtcNow;
            _logger?.LogInformation(
                "[{CorrelationId}] Resuming streaming session={SessionId} from checkpoint {CheckpointId}",
                turnCtx.CorrelationId,
                session.Id,
                resumeCheckpoint.CheckpointId);
        }

            using var turnRoutingScope = await ApplyTurnRoutingAsync(session, userMessage, resumeCheckpoint is not null, responseSchema: null, ct);

        var messages = BuildMessages(session, exactLatestToolBatch: resumeCheckpoint is not null, userMessage: userMessage);
        if (resumeCheckpoint is not null)
        {
            messages.Insert(1, new ChatMessage(ChatRole.System, AgentCheckpointManager.BuildCheckpointResumeInstruction(resumeCheckpoint)));
            if (!AgentCheckpointManager.IsBareResumeRequest(userMessage))
                messages.Add(new ChatMessage(ChatRole.User, AgentCheckpointManager.BuildCheckpointResumeUserNote(userMessage)));
        }
        else
        {
            // Order matters: memory recall first, then profile recall (inserted near conversation start).
            var memoryRecallInjected = await TryInjectRecallAsync(messages, userMessage, ct);
            await TryInjectStructuredMemoryContextAsync(messages, session, userMessage, memoryRecallInjected, ct);
            await TryInjectProfileRecallAsync(messages, session, ct);
        }

        // Inject Goal activation prompt in streaming path
        if (_goalIntegration is not null)
        {
            var goalPrompt = _goalIntegration.BuildGoalSystemPrompt(session.Id);
            if (goalPrompt is not null)
                messages.Insert(1, new ChatMessage(ChatRole.System, goalPrompt));
        }

        var chatOptions = new ChatOptions
        {
            ModelId = session.ModelOverride ?? _config.Model,
            MaxOutputTokens = _maxTokens,
            Temperature = _temperature,
            Tools = _toolExecutor.GetToolDeclarations(session)
        };

        if (!string.IsNullOrWhiteSpace(session.ReasoningEffort))
        {
            chatOptions.AdditionalProperties ??= new AdditionalPropertiesDictionary();
            chatOptions.AdditionalProperties["reasoning_effort"] = session.ReasoningEffort;
        }

        for (var i = 0; i < _maxIterations; i++)
        {
            // Mid-turn budget check: stop if token budget is exceeded
            if (_sessionTokenBudget > 0 && session.GetTotalTokens() >= _sessionTokenBudget)
            {
                _logger?.LogInformation("[{CorrelationId}] Streaming session token budget exceeded mid-turn ({Used}/{Budget})",
                    turnCtx.CorrelationId, session.GetTotalTokens(), _sessionTokenBudget);
                yield return AgentStreamEvent.ErrorOccurred(
                    "You've reached the token limit for this session. Please start a new conversation.",
                    "session_token_limit");
                yield return AgentStreamEvent.Complete();
                LogTurnComplete(turnCtx);
                yield break;
            }

            if (TryRejectContractBudget(session, out contractBudgetMessage))
            {
                yield return AgentStreamEvent.ErrorOccurred(contractBudgetMessage, "contract_budget_exceeded");
                yield return AgentStreamEvent.Complete();
                AppendContractSnapshot(session, "budget_exceeded");
                LogTurnComplete(turnCtx);
                yield break;
            }

            // Stream the LLM response, collecting chunks and tool calls.
            // We buffer events because C# doesn't allow yield in try/catch.
            var streamResult = await StreamLlmCollectAsync(session, messages, chatOptions, turnCtx, ct);

            // Redact the complete buffered text so secrets split across provider chunks cannot leak.
            var fullText = streamResult.FullText;
            var redactedText = _redaction.Redact(fullText);
            if (string.Equals(redactedText, fullText, StringComparison.Ordinal))
            {
                foreach (var delta in streamResult.TextDeltas)
                    yield return AgentStreamEvent.TextDelta(delta);
            }
            else if (!string.IsNullOrEmpty(redactedText))
            {
                yield return AgentStreamEvent.TextDelta(redactedText);
            }

            // If streaming failed, yield error and stop
            if (streamResult.Error is not null)
            {
                yield return AgentStreamEvent.ErrorOccurred(streamResult.Error, "provider_failure");
                yield return AgentStreamEvent.Complete();
                LogTurnComplete(turnCtx);
                yield break;
            }

            session.AddTokenUsage(streamResult.InputTokens, streamResult.OutputTokens);
            session.AddCacheUsage(streamResult.CacheReadTokens, streamResult.CacheWriteTokens);
            if (!string.IsNullOrWhiteSpace(streamResult.ProviderId) && !string.IsNullOrWhiteSpace(streamResult.ModelId))
                _recordContractTurnUsage?.Invoke(session, streamResult.ProviderId, streamResult.ModelId, streamResult.InputTokens, streamResult.OutputTokens);
            if (!string.IsNullOrWhiteSpace(streamResult.ProviderId) && !string.IsNullOrWhiteSpace(streamResult.ModelId))
            {
                    var isUsageEstimated = streamResult.IsUsageEstimated;
                RecordTurnUsage(
                    session,
                    streamResult.ProviderId,
                    streamResult.ModelId,
                    streamResult.InputTokens,
                    streamResult.OutputTokens,
                    streamResult.CacheReadTokens,
                    streamResult.CacheWriteTokens,
                        isUsageEstimated
                            ? LlmExecutionEstimateBuilder.BuildInputTokenEstimate(messages, streamResult.InputTokens, _skillPromptLength)
                            : new InputTokenComponentEstimate(),
                        isEstimated: isUsageEstimated,
                        correlationId: turnCtx.CorrelationId);
            }

            if (TryRejectContractBudget(session, out contractBudgetMessage))
            {
                yield return AgentStreamEvent.ErrorOccurred(contractBudgetMessage, "contract_budget_exceeded");
                yield return AgentStreamEvent.Complete();
                AppendContractSnapshot(session, "budget_exceeded");
                LogTurnComplete(turnCtx);
                yield break;
            }

            var toolCalls = streamResult.ToolCalls;

            if (toolCalls.Count == 0)
            {
                // Final text response
                var finalText = _redaction.Redact(streamResult.FullText);

                // ── Goal continuation check (streaming path) ──
                if (_goalIntegration is not null)
                {
                    _goalIntegration.UpdateGoalTokenUsage(session);
                    var continuationPrompt = _goalIntegration.EvaluateGoalContinuation(
                        session, i, _maxIterations, finalText);
                    if (continuationPrompt is not null)
                    {
                        messages.Add(new ChatMessage(ChatRole.System, continuationPrompt));
                        session.History.Add(new ChatTurn
                        {
                            Role = "system",
                            Content = $"[goal_check:{i}] Continue working toward objective..."
                        });
                        continue; // ← Don't yield Complete — continue the loop
                    }
                }
                // ── End Goal continuation check ──

                session.History.Add(new ChatTurn { Role = "assistant", Content = finalText });
                AgentCheckpointManager.MarkCheckpointCompleted(session, SessionCheckpointStates.Completed, "final_response");
                yield return AgentStreamEvent.Complete();
                AppendContractSnapshot(session, "active");
                LogTurnComplete(turnCtx);
                yield break;
            }

            // Execute tool calls.
            AgentToolBatchExecution? completedBatch = null;
            await foreach (var update in _toolLoop.ExecuteStreamingToolCallsAsync(toolCalls, session, turnCtx, approvalCallback, ct))
            {
                if (update.StreamEvent is not null) yield return update.StreamEvent.Value;
                if (update.Batch is not null) completedBatch = update.Batch;
            }
            var invocations = completedBatch?.Invocations ?? [];
            var toolResults = completedBatch?.Results ?? [];

            messages.Add(new ChatMessage(ChatRole.Assistant, toolCalls.Cast<AIContent>().ToList()));
            messages.Add(new ChatMessage(ChatRole.Tool, toolResults.Cast<AIContent>().ToList()));

            session.History.Add(new ChatTurn
            {
                Role = "assistant",
                Content = "[tool_use]",
                ToolCalls = invocations
            });

            // Compaction is NOT run inside the iteration loop to avoid cascading LLM calls.
            TrimHistory(session);
            await _checkpoints.PersistToolBatchCheckpointAsync(session, turnCtx, i, invocations, ct);
        }

        yield return AgentStreamEvent.ErrorOccurred(
            "I've reached the maximum number of tool iterations. Please try a simpler request.",
            "max_iterations");
        yield return AgentStreamEvent.Complete();
        AgentCheckpointManager.MarkCheckpointCompleted(session, SessionCheckpointStates.Failed, "max_iterations");
        AppendContractSnapshot(session, "active");
        LogTurnComplete(turnCtx);
    }

    private void RecordTurnUsage(Session session, string providerId, string modelId,
        long inputTokens, long outputTokens, long cacheReadTokens, long cacheWriteTokens,
        InputTokenComponentEstimate estimatedInputTokensByComponent, bool isEstimated, string? correlationId)
        => _accounting.RecordTurnUsage(session, providerId, modelId, inputTokens, outputTokens,
            cacheReadTokens, cacheWriteTokens, estimatedInputTokensByComponent, isEstimated, correlationId);

    private ValueTask<bool> TryInjectRecallAsync(List<ChatMessage> messages, string userMessage, CancellationToken ct)
        => _contextAssembler.TryInjectRecallAsync(messages, userMessage, ct);

    private ValueTask TryInjectStructuredMemoryContextAsync(List<ChatMessage> messages, Session session, string userMessage, bool memoryRecallInjected, CancellationToken ct)
        => _contextAssembler.TryInjectStructuredMemoryContextAsync(messages, session, userMessage, memoryRecallInjected, ct);

    private ValueTask TryInjectProfileRecallAsync(List<ChatMessage> messages, Session session, CancellationToken ct)
        => _contextAssembler.TryInjectProfileRecallAsync(messages, session, ct);

    private Task<AgentStreamCollectResult> StreamLlmCollectAsync(
        Session session, List<ChatMessage> messages, ChatOptions options, TurnContext turnCtx, CancellationToken ct)
        => _modelExecutor.StreamLlmCollectAsync(session, messages, options, turnCtx, _skillPromptLength, ct);

    private async Task<(List<ToolInvocation> Invocations, List<FunctionResultContent> Results)> ExecuteToolCallsAsync(
        List<FunctionCallContent> toolCalls, Session session, TurnContext turnCtx,
        bool isStreaming, ToolApprovalCallback? approvalCallback, CancellationToken ct)
    {
        var batch = await _toolLoop.ExecuteToolCallsAsync(toolCalls, session, turnCtx, isStreaming, approvalCallback, ct);
        return (batch.Invocations, batch.Results);
    }

    private Task<LlmExecutionResult> CallLlmWithResilienceAsync(
        Session session, List<ChatMessage> messages, ChatOptions options, TurnContext turnCtx, CancellationToken ct)
        => _modelExecutor.CallLlmWithResilienceAsync(session, messages, options, turnCtx, _skillPromptLength, ct);

    private static bool IsExpectedLlmFailure(Exception ex)
        => ex is HttpRequestException
            or TimeoutException
            or OperationCanceledException
            or System.IO.IOException
            or System.Net.Sockets.SocketException
            || ex is InvalidOperationException invalidOperation && IsExpectedLlmInvalidOperation(invalidOperation);

    private static bool IsExpectedLlmInvalidOperation(InvalidOperationException ex)
    {
        if (ex.InnerException is not null && IsExpectedLlmFailure(ex.InnerException))
            return true;

        var message = ex.Message;
        return message.Contains("LLM", StringComparison.OrdinalIgnoreCase)
            || message.Contains("provider", StringComparison.OrdinalIgnoreCase)
            || message.Contains("model", StringComparison.OrdinalIgnoreCase)
            || message.Contains("credential", StringComparison.OrdinalIgnoreCase)
            || message.Contains("API key", StringComparison.OrdinalIgnoreCase)
            || message.Contains("endpoint", StringComparison.OrdinalIgnoreCase)
            || message.Contains("token budget", StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// Compacts session history by summarizing older turns via the LLM.
    /// Keeps the most recent turns verbatim and replaces older ones with a summary.
    /// </summary>
    public async Task CompactHistoryAsync(Session session, CancellationToken ct, string? correlationId = null)
    {
        if (session.History.Count <= _compactionThreshold)
        {
            // Below threshold — just apply simple trim as fallback
            TrimHistory(session);
            return;
        }

        var keepCount = Math.Min(_compactionKeepRecent, session.History.Count - 2);
        var toSummarizeCount = session.History.Count - keepCount;

        if (toSummarizeCount < 4)
        {
            TrimHistory(session);
            return;
        }

        // Check if we already have a compaction summary as the first turn
        if (session.History.Count > 0 &&
            session.History[0].Role == "system" &&
            session.History[0].Content.StartsWith("[Previous conversation summary:", StringComparison.Ordinal))
        {
            // Previous summary will be included in what gets re-summarized
        }

        var turnsToSummarize = session.History.GetRange(0, toSummarizeCount);
        var conversationText = new StringBuilder();
        foreach (var turn in turnsToSummarize)
        {
            if (turn.Content == "[tool_use]" && turn.ToolCalls is { Count: > 0 })
            {
                foreach (var tc in turn.ToolCalls)
                    conversationText.AppendLine($"assistant: [called {tc.ToolName}] → {Truncate(tc.Result ?? "", 200)}");
            }
            else
            {
                conversationText.AppendLine($"{turn.Role}: {Truncate(turn.Content, 500)}");
            }
        }

        try
        {
            var summaryMessages = new List<ChatMessage>
            {
                new(ChatRole.System,
                    "Summarize the following conversation turns into a concise context summary (2-3 sentences). " +
                    "Focus on key decisions, facts established, and pending tasks. Output ONLY the summary."),
                new(ChatRole.User, conversationText.ToString())
            };

            var summaryOptions = new ChatOptions { MaxOutputTokens = 256, Temperature = 0.3f };
            var compactionTurnCtx = new TurnContext
            {
                CorrelationId = ResolveCorrelationId(correlationId),
                SessionId = session.Id,
                ChannelId = session.ChannelId
            };

            var summarySw = Stopwatch.StartNew();
            var response = await CallLlmWithResilienceAsync(session, summaryMessages, summaryOptions, compactionTurnCtx, ct);
            summarySw.Stop();

            var summaryInputTokens = response.Response.Usage?.InputTokenCount ?? 0;
            var summaryOutputTokens = response.Response.Usage?.OutputTokenCount ?? 0;
            session.AddTokenUsage(summaryInputTokens, summaryOutputTokens);
            _recordContractTurnUsage?.Invoke(session, response.ProviderId, response.ModelId, summaryInputTokens, summaryOutputTokens);
            compactionTurnCtx.RecordLlmCall(summarySw.Elapsed, summaryInputTokens, summaryOutputTokens);
            _metrics?.IncrementLlmCalls();
            _metrics?.AddInputTokens(summaryInputTokens);
            _metrics?.AddOutputTokens(summaryOutputTokens);

            var summary = response.Response.Text ?? "";

            if (!string.IsNullOrWhiteSpace(summary))
            {
                _metrics?.IncrementMemoryCompactions();
                session.History.RemoveRange(0, toSummarizeCount);
                session.History.Insert(0, new ChatTurn
                {
                    Role = "system",
                    Content = $"[Previous conversation summary: {summary}]"
                });
                _logger?.LogDebug("Compacted {Count} history turns into summary", toSummarizeCount);
            }
            else
            {
                // Summarization returned empty — fall back to simple trim
                TrimHistory(session);
            }
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            _logger?.LogWarning(ex, "History compaction failed — falling back to simple trim");
            TrimHistory(session);
        }
    }

    private List<ChatMessage> BuildMessages(Session session, bool exactLatestToolBatch = false, string? userMessage = null)
        => _contextAssembler.BuildMessages(session, _maxHistoryTurns, exactLatestToolBatch, GetSystemPrompt(session, userMessage));

    private string GetSystemPrompt(Session session, string? userMessage = null)
    {
        string systemPrompt;
        string? blockedRoutes = null;
        lock (_skillGate)
        {
            systemPrompt = _systemPrompt;

            // Per-turn projection resolution: when a user message is available and any
            // loaded skill has projection contracts, resolve them to patch skill instructions
            // and build a per-turn skill index. Matches kingcrab MafAgentRuntime behavior.
            if (!string.IsNullOrWhiteSpace(userMessage))
            {
                var hasProjectionSkills = false;
                foreach (var s in _loadedSkills)
                {
                    if (s.ProjectionContracts.Count > 0)
                    {
                        hasProjectionSkills = true;
                        break;
                    }
                }

                if (hasProjectionSkills)
                {
                    var effectiveSkills = ResolveSkillsForTurn(_loadedSkills, userMessage, out blockedRoutes, out var projectionPatches);
                    var skillSection = SkillPromptBuilder.BuildIndex(effectiveSkills, _skillsConfig?.InstructionPrompt);
                    var basePrompt = AgentSystemPromptBuilder.BuildBaseSystemPrompt(_requireToolApproval);
                    systemPrompt = string.IsNullOrEmpty(skillSection) ? basePrompt : basePrompt + "\n" + skillSection;

                    if (!string.IsNullOrWhiteSpace(projectionPatches))
                        systemPrompt += "\n\n[Skill Projection Patches]\n" + projectionPatches.Trim();
                }
            }
        }

        systemPrompt = AgentSystemPromptBuilder.ApplyResponseMode(systemPrompt, session.ResponseMode);

        if (!string.IsNullOrWhiteSpace(blockedRoutes))
            systemPrompt += "\n\n[Blocked Skill Routes]\n" + blockedRoutes.Trim();

        if (!string.IsNullOrWhiteSpace(session.SystemPromptOverride))
            return systemPrompt + "\n\n[Route Instructions]\n" + session.SystemPromptOverride.Trim();

        return systemPrompt;
    }

    internal static SkillDefinition[] ResolveSkillsForTurn(
        IReadOnlyList<SkillDefinition> skills,
        string userMessage,
        out string blockedRoutes)
        => ResolveSkillsForTurn(skills, userMessage, out blockedRoutes, out _);

    internal static SkillDefinition[] ResolveSkillsForTurn(
        IReadOnlyList<SkillDefinition> skills,
        string userMessage,
        out string blockedRoutes,
        out string projectionPatches)
    {
        var resolvedSkills = new List<SkillDefinition>(skills.Count);
        var blocked = new System.Text.StringBuilder();
        var patches = new System.Text.StringBuilder();

        foreach (var skill in skills)
        {
            if (skill.ProjectionContracts.Count == 0)
            {
                resolvedSkills.Add(skill);
                continue;
            }

            var resolution = OpenClaw.Core.Skills.SkillProjectionResolver.ResolveForRequest(
                skill, userMessage,
                Microsoft.Extensions.Logging.Abstractions.NullLogger.Instance);

            if (resolution.IsBlocked)
            {
                blocked.Append("- ");
                blocked.Append(skill.Name);
                blocked.Append(": ");
                blocked.AppendLine(resolution.BlockReason ?? "Projection contract resolution blocked this skill for the current request.");
                resolvedSkills.Add(CloneSkill(skill, skill.Instructions, disableModelInvocation: true));
                continue;
            }

            var patch = OpenClaw.Core.Skills.SkillProjectionResolver.BuildPromptPatch(resolution);
            if (string.IsNullOrWhiteSpace(patch))
            {
                resolvedSkills.Add(skill);
                continue;
            }

            var patchedInstructions = string.Concat(skill.Instructions.TrimEnd(), "\n\n", patch);
            resolvedSkills.Add(CloneSkill(skill, patchedInstructions, skill.DisableModelInvocation));

            patches.AppendLine($"## {skill.Name}");
            patches.AppendLine(patch);
            patches.AppendLine();
        }

        blockedRoutes = blocked.ToString();
        projectionPatches = patches.ToString();
        return [.. resolvedSkills];
    }

    internal static SkillDefinition CloneSkill(SkillDefinition source, string instructions, bool disableModelInvocation)
        => new()
        {
            Name = source.Name,
            Description = source.Description,
            Instructions = instructions,
            Location = source.Location,
            Source = source.Source,
            Metadata = source.Metadata,
            Kind = source.Kind,
            Triggers = source.Triggers,
            MetaPriority = source.MetaPriority,
            FinalTextMode = source.FinalTextMode,
            Composition = source.Composition,
            UserInvocable = source.UserInvocable,
            DisableModelInvocation = disableModelInvocation,
            CommandDispatch = source.CommandDispatch,
            CommandTool = source.CommandTool,
            CommandArgMode = source.CommandArgMode,
            Resources = source.Resources,
            ProjectionContracts = source.ProjectionContracts,
            ProjectionDiscovery = source.ProjectionDiscovery,
            ArtifactContract = source.ArtifactContract
        };

    private async ValueTask<IDisposable> ApplyTurnRoutingAsync(
        Session session,
        string userMessage,
        bool exactLatestToolBatch,
        JsonElement? responseSchema,
        CancellationToken ct)
    {
        var baseOptions = new ChatOptions
        {
            ModelId = session.ModelOverride ?? _config.Model,
            MaxOutputTokens = _maxTokens,
            Temperature = _temperature,
            Tools = _toolExecutor.GetToolDeclarations(session),
            ResponseFormat = responseSchema.HasValue
                ? ChatResponseFormat.ForJsonSchema(responseSchema.Value, "response")
                : null
        };

        if (!string.IsNullOrWhiteSpace(session.ReasoningEffort))
        {
            baseOptions.AdditionalProperties ??= new AdditionalPropertiesDictionary();
            baseOptions.AdditionalProperties["reasoning_effort"] = session.ReasoningEffort;
        }

        var decision = await _turnRoutingPolicy.ResolveAsync(new TurnRoutingRequest
        {
            Session = session,
            Messages = BuildMessages(session, exactLatestToolBatch),
            UserMessage = userMessage,
            BaseOptions = baseOptions
        }, ct);

        var metaRoutingSuffix = BuildMetaRoutingSuffix(userMessage);

        var snapshot = new TurnRoutingSnapshot(
            session.ModelProfileId,
            session.PreferredModelTags,
            session.FallbackModelProfileIds,
            session.SystemPromptOverride,
            session.RouteAllowedTools,
            session.RouteToolsDisabled,
            session.RouteModelTier,
            session.RouteReason,
            session.ReasoningEffort,
            session.ResponseMode);

        if (!string.IsNullOrWhiteSpace(decision.ModelProfileId))
            session.ModelProfileId = decision.ModelProfileId;

        if (!string.IsNullOrWhiteSpace(decision.DirectModelFallbackProfileId))
        {
            var fallback = decision.DirectModelFallbackProfileId!;
            session.FallbackModelProfileIds =
            [
                fallback,
                .. session.FallbackModelProfileIds.Where(item => !string.Equals(item, fallback, StringComparison.OrdinalIgnoreCase))
            ];
        }

        if (decision.PreferredTags.Length > 0)
            session.PreferredModelTags = decision.PreferredTags;
        if (!string.IsNullOrWhiteSpace(decision.ReasoningLevel))
            session.ReasoningEffort = decision.ReasoningLevel;
        if (!string.IsNullOrWhiteSpace(decision.ResponsePolicy))
            session.ResponseMode = decision.ResponsePolicy;
        if (decision.DisableTools)
        {
            session.RouteToolsDisabled = true;
            session.RouteAllowedTools = [];
        }
        else if (decision.AllowedTools.Length > 0)
        {
            session.RouteToolsDisabled = false;
            session.RouteAllowedTools = decision.AllowedTools;
        }
        session.RouteModelTier = decision.Tier;
        session.RouteReason = decision.Reason;
        session.SystemPromptOverride = CombineSystemPromptOverride(
            snapshot.SystemPromptOverride,
            CombineSystemPromptSuffixes(decision.SystemPromptSuffix, metaRoutingSuffix));

        return new TurnRoutingRestoreScope(session, snapshot);
    }

    private static string? CombineSystemPromptOverride(string? original, string? suffix)
    {
        if (string.IsNullOrWhiteSpace(suffix))
            return original;

        if (string.IsNullOrWhiteSpace(original))
            return suffix.Trim();

        return original.Trim() + "\n" + suffix.Trim();
    }

    private string? BuildMetaRoutingSuffix(string userMessage)
    {
        if (!_metaSkillsEnabled)
            return null;

        if (string.IsNullOrWhiteSpace(userMessage))
            return null;

        var skills = LoadedSkills;
        if (!MetaSkillResolver.TryResolve(skills, userMessage, out var matched) || matched is null)
            return null;

        return "[Meta Routing Hint]\n"
            + "A matching meta skill is available. Prefer calling tool `meta_invoke` before other tools.\n"
            + $"Matched skill: {matched.Name}\n"
            + "Use arguments JSON: {\"skill\":\"<matched-skill-name>\",\"input\":\"<user-request>\"}.\n"
            + "If invocation fails, continue with normal tool planning.\n"
            + "[/Meta Routing Hint]";
    }

    private async Task<string> ExecuteMetaSkillAsync(Session session, string skillName, string? input, CancellationToken ct)
    {
        if (!_metaSkillsEnabled)
            return "Error: Meta skill invocation is disabled by runtime policy.";

        var metaSkill = LoadedSkills.FirstOrDefault(skill =>
            skill.Kind == SkillKind.Meta &&
            !skill.DisableModelInvocation &&
            string.Equals(skill.Name, skillName, StringComparison.OrdinalIgnoreCase));

        if (metaSkill is null)
            return $"Error: Meta skill '{skillName}' was not found.";

        var steps = metaSkill.Composition?.Steps;
        if (steps is null || steps.Count == 0)
            return $"Error: Meta skill '{metaSkill.Name}' has no executable composition steps.";

        if (!TryValidateMetaPlan(steps, LoadedSkills, out var validationError))
            return ReturnMetaExecutionOutput(session, metaSkill, finalText: null, stepResults: [], validationError, preserveCheckpoint: false);

        var stepById = steps.ToDictionary(static step => step.Id, StringComparer.OrdinalIgnoreCase);
        var failureBranchTargets = steps
            .Where(static step => !string.IsNullOrWhiteSpace(step.OnFailure))
            .Select(static step => step.OnFailure!)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        var dependentsByStep = BuildDependentsIndex(steps);
        var pending = new HashSet<string>(stepById.Keys.Where(stepId => !failureBranchTargets.Contains(stepId)), StringComparer.OrdinalIgnoreCase);
        var blocked = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var outputs = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        var failureAliases = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        var stepResults = new List<MetaStepExecutionResult>(steps.Count);
        var templateRenderer = new MetaTemplateRenderer();
        var conditionEvaluator = new MetaConditionEvaluator(templateRenderer);
        var toolArgumentResolver = new MetaToolArgumentResolver(templateRenderer);
        var clarifyValidator = new MetaClarifyValidator();
        var routePlanner = new MetaRoutePlanner(conditionEvaluator);
        var resumedFromCheckpoint = TryRestoreMetaExecutionCheckpoint(
            session,
            metaSkill.Name,
            stepById.Keys,
            pending,
            blocked,
            outputs,
            failureAliases,
            stepResults,
            out var waitingPrompt);
        if (resumedFromCheckpoint)
        {
            var timeoutHandledByFailureBranch = false;
            var resumedStepId = session.MetaExecutionCheckpoint?.PendingStepId;
            var resumedStep = string.IsNullOrWhiteSpace(resumedStepId)
                ? null
                : steps.FirstOrDefault(step => string.Equals(step.Id, resumedStepId, StringComparison.OrdinalIgnoreCase));

            var resumedSkipClarify = resumedStep?.Clarify is not null
                && !string.IsNullOrWhiteSpace(resumedStep.Clarify.SkipIf)
                && conditionEvaluator.Evaluate(resumedStep.Clarify.SkipIf, new MetaExecutionContext(input, outputs));

            if (string.IsNullOrWhiteSpace(input) && resumedStep is not null && IsClarifyInputTimedOut(session, metaSkill.Name, resumedStep))
            {
                stepResults.Add(new MetaStepExecutionResult(resumedStep.Id, resumedStep.Kind, ToolResultStatuses.Failed, "user_input_timeout", 0, Continued: false));

                if (TryActivateFailureBranch(resumedStep, stepById, pending, blocked, failureAliases))
                {
                    ClearMetaExecutionCheckpoint(session, metaSkill.Name);
                    timeoutHandledByFailureBranch = true;
                }
                else
                {
                    return ReturnMetaExecutionOutput(session, metaSkill, finalText: null, stepResults, $"Meta step '{resumedStep.Id}' clarify input timed out.", preserveCheckpoint: false);
                }
            }

            if (string.IsNullOrWhiteSpace(input) && resumedSkipClarify)
            {
                timeoutHandledByFailureBranch = true;
                ClearMetaExecutionCheckpoint(session, metaSkill.Name);
            }

            if (string.IsNullOrWhiteSpace(input) && !timeoutHandledByFailureBranch && !resumedSkipClarify)
            {
                return ReturnMetaExecutionOutput(
                    session,
                    metaSkill,
                    finalText: null,
                    stepResults,
                    waitingPrompt ?? "Meta execution is waiting for user input to continue.",
                    preserveCheckpoint: true);
            }
        }
        var turnCtx = new TurnContext
        {
            CorrelationId = ResolveCorrelationId(null),
            SessionId = session.Id,
            ChannelId = session.ChannelId
        };

        var sessionInputs = new Dictionary<string, object>(StringComparer.OrdinalIgnoreCase)
        {
            ["session_id"] = session.Id,
            ["session_history"] = JsonSerializer.Serialize(session.History, CoreJsonContext.Default.ListChatTurn),
            ["session_meta_runs"] = JsonSerializer.Serialize(session.MetaRunHistory, CoreJsonContext.Default.ListSessionMetaRunRecord)
        };

        if (!resumedFromCheckpoint)
            routePlanner.ApplyInitialRoutingBlocks(steps, blocked, pending);

        while (pending.Count > 0)
        {
            var progress = false;

            if (await TryExecuteParallelToolWaveAsync(
                    session,
                    metaSkill,
                    steps,
                    stepById,
                    dependentsByStep,
                    pending,
                    blocked,
                    outputs,
                    failureAliases,
                    stepResults,
                    input,
                    turnCtx,
                    templateRenderer,
                    conditionEvaluator,
                    toolArgumentResolver,
                    routePlanner,
                    ct))
            {
                continue;
            }

            if (await MetaFanOutExecutor.TryExecuteFanOutStepAsync(
                    session,
                    metaSkill,
                    steps,
                    stepById,
                    dependentsByStep,
                    pending,
                    blocked,
                    outputs,
                    failureAliases,
                    stepResults,
                    input,
                    turnCtx,
                    templateRenderer,
                    conditionEvaluator,
                    toolArgumentResolver,
                    routePlanner,
                    ExecuteFanOutChildAsync,
                    (msg, ex) => _logger?.LogWarning(ex, "{FanOutMessage}", msg),
                    ct))
            {
                continue;
            }

            foreach (var step in steps)
            {
                if (!pending.Contains(step.Id))
                    continue;

                if (blocked.Contains(step.Id))
                {
                    pending.Remove(step.Id);
                    progress = true;
                    continue;
                }

                var blockedByDependency = false;
                var waitingForDependency = false;
                foreach (var dependency in step.DependsOn)
                {
                    if (blocked.Contains(dependency))
                    {
                        blockedByDependency = true;
                        break;
                    }

                    if (!outputs.ContainsKey(dependency))
                    {
                        waitingForDependency = true;
                        break;
                    }
                }

                if (blockedByDependency)
                {
                    BlockStepAndDependents(step.Id, blocked, pending, dependentsByStep);
                    progress = true;
                    continue;
                }

                if (waitingForDependency)
                    continue;

                var stepArgs = DeserializeStepArgs(step.WithJson);
                var metaContext = new MetaExecutionContext(input, outputs, inputs: sessionInputs);
                var continueOnError = GetOptionalBoolean(stepArgs, "continue_on_error") ?? false;

                if (!string.IsNullOrWhiteSpace(step.When) && !conditionEvaluator.Evaluate(step.When, metaContext))
                {
                    BlockStepAndDependents(step.Id, blocked, pending, dependentsByStep);
                    stepResults.Add(new MetaStepExecutionResult(step.Id, step.Kind, ToolResultStatuses.Blocked, "condition_false", 0, Continued: false));
                    progress = true;
                    continue;
                }

                string stepInput;
                try
                {
                    stepInput = templateRenderer.Render(
                        GetOptionalString(stepArgs, "input") ?? input ?? string.Empty,
                        metaContext);
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    stepResults.Add(new MetaStepExecutionResult(step.Id, step.Kind, ToolResultStatuses.Failed, "template_render_failed", 0, Continued: continueOnError));

                    if (TryActivateFailureBranch(step, stepById, pending, blocked, failureAliases))
                    {
                        progress = true;
                        continue;
                    }

                    if (!continueOnError)
                    {
                        return ReturnMetaExecutionOutput(session, metaSkill, finalText: null, stepResults, $"Meta step '{step.Id}' template render failed: {ex.Message}", preserveCheckpoint: false);
                    }

                    CompleteMetaStepOutput(step, string.Empty, pending, outputs, failureAliases);
                    routePlanner.ApplyCompletionRouting(step, new MetaExecutionContext(input, outputs), stepById, blocked, pending, dependentsByStep);
                    progress = true;
                    continue;
                }

                switch (NormalizeMetaStepKind(step.Kind))
                {
                    case "tool_call":
                    {
                        var toolName = step.Tool;
                        if (string.IsNullOrWhiteSpace(toolName))
                        {
                            return ReturnMetaExecutionOutput(session, metaSkill, finalText: null, stepResults, $"Meta step '{step.Id}' is 'tool_call' but does not declare a tool.", preserveCheckpoint: false);
                        }

                        if (step.ToolAllowlist.Count > 0 && !step.ToolAllowlist.Contains(toolName, StringComparer.OrdinalIgnoreCase))
                        {
                            stepResults.Add(new MetaStepExecutionResult(step.Id, step.Kind, ToolResultStatuses.Blocked, "tool_not_allowlisted", 0, Continued: false));
                            return ReturnMetaExecutionOutput(session, metaSkill, finalText: null, stepResults, $"Meta step '{step.Id}' tool '{toolName}' is not allowlisted.", preserveCheckpoint: false);
                        }

                            if (!IsToolAllowedByMetaCapabilities(metaSkill, toolName))
                            {
                                pending.Remove(step.Id);
                                progress = true;
                                stepResults.Add(new MetaStepExecutionResult(step.Id, step.Kind, ToolResultStatuses.Blocked, "metadata_capability_denied", 0, Continued: false));
                                return ReturnMetaExecutionOutput(session, metaSkill, finalText: null, stepResults, $"Meta step '{step.Id}' tool '{toolName}' is not permitted by metadata capabilities.", preserveCheckpoint: false);
                            }

                        string toolArgsJson;
                        try
                        {
                            toolArgsJson = toolArgumentResolver.Resolve(
                                metaSkill.Composition?.ToolArgsJson,
                                step.WithJson,
                                step.ToolArgsJson,
                                metaContext);
                        }
                        catch (InvalidOperationException)
                        {
                            return ReturnMetaExecutionOutput(session, metaSkill, finalText: null, stepResults, $"Meta step '{step.Id}' declared invalid tool arguments.", preserveCheckpoint: false);
                        }

                        var stepSw = Stopwatch.StartNew();
                        var toolResult = await ExecuteMetaToolStepWithPolicyAsync(
                            metaSkill,
                            step,
                            toolName,
                            toolArgsJson,
                            session,
                            turnCtx,
                            ct);
                        stepSw.Stop();

                        var completed = string.Equals(toolResult.ResultStatus, ToolResultStatuses.Completed, StringComparison.Ordinal);
                        var resultStatus = toolResult.ResultStatus;
                        var failureCode = toolResult.FailureCode;
                        if (completed && !TryValidateMetaStepOutput(step, toolResult.ResultText, out failureCode))
                        {
                            completed = false;
                            resultStatus = ToolResultStatuses.Failed;
                        }

                        stepResults.Add(new MetaStepExecutionResult(
                            step.Id,
                            step.Kind,
                            resultStatus,
                            failureCode,
                            stepSw.Elapsed.TotalMilliseconds,
                            Continued: !completed && continueOnError));

                        if (completed)
                        {
                            CompleteMetaStepOutput(step, toolResult.ResultText, pending, outputs, failureAliases);
                            routePlanner.ApplyCompletionRouting(step, new MetaExecutionContext(input, outputs), stepById, blocked, pending, dependentsByStep);
                            progress = true;
                            break;
                        }

                        if (TryActivateFailureBranch(step, stepById, pending, blocked, failureAliases))
                        {
                            progress = true;
                            break;
                        }

                        if (!continueOnError)
                        {
                            return ReturnMetaExecutionOutput(session, metaSkill, finalText: null, stepResults, $"Meta step '{step.Id}' failed with status '{toolResult.ResultStatus}'.", preserveCheckpoint: false);
                        }

                        CompleteMetaStepOutput(step, toolResult.ResultText, pending, outputs, failureAliases);
                        routePlanner.ApplyCompletionRouting(step, new MetaExecutionContext(input, outputs), stepById, blocked, pending, dependentsByStep);
                        progress = true;

                        break;
                    }

                    case "skill_exec":
                    {
                        var delegatedSkill = !string.IsNullOrWhiteSpace(step.Skill)
                            ? LoadedSkills.FirstOrDefault(skill =>
                                string.Equals(skill.Name, step.Skill, StringComparison.OrdinalIgnoreCase))
                            : null;

                        if (delegatedSkill is null)
                        {
                            stepResults.Add(new MetaStepExecutionResult(step.Id, step.Kind, ToolResultStatuses.Failed, "skill_not_found", 0, Continued: continueOnError));

                            if (TryActivateFailureBranch(step, stepById, pending, blocked, failureAliases))
                            {
                                progress = true;
                                break;
                            }

                            if (!continueOnError)
                            {
                                return ReturnMetaExecutionOutput(session, metaSkill, finalText: null, stepResults, $"Meta step '{step.Id}' references missing skill '{step.Skill}'.", preserveCheckpoint: false);
                            }

                            CompleteMetaStepOutput(step, string.Empty, pending, outputs, failureAliases);
                            routePlanner.ApplyCompletionRouting(step, new MetaExecutionContext(input, outputs), stepById, blocked, pending, dependentsByStep);
                            progress = true;
                            break;
                        }

                        var renderedArgs = new List<string>(step.SkillExecArgs.Count);
                        try
                        {
                            var argumentContext = new MetaExecutionContext(input, outputs);
                            foreach (var argument in step.SkillExecArgs)
                                renderedArgs.Add(templateRenderer.Render(argument, argumentContext));
                        }
                        catch (Exception ex)
                        {
                            stepResults.Add(new MetaStepExecutionResult(step.Id, step.Kind, ToolResultStatuses.Failed, "template_render_failed", 0, Continued: continueOnError));

                            if (TryActivateFailureBranch(step, stepById, pending, blocked, failureAliases))
                            {
                                progress = true;
                                break;
                            }

                            if (!continueOnError)
                            {
                                return ReturnMetaExecutionOutput(session, metaSkill, finalText: null, stepResults, $"Meta step '{step.Id}' template render failed: {ex.Message}", preserveCheckpoint: false);
                            }

                            CompleteMetaStepOutput(step, ex.Message, pending, outputs, failureAliases);
                            routePlanner.ApplyCompletionRouting(step, new MetaExecutionContext(input, outputs), stepById, blocked, pending, dependentsByStep);
                            progress = true;
                            break;
                        }

                        var renderedCwd = step.SkillExecCwd;
                        if (!string.IsNullOrWhiteSpace(renderedCwd))
                        {
                            try
                            {
                                renderedCwd = templateRenderer.Render(renderedCwd, new MetaExecutionContext(input, outputs));
                            }
                            catch (Exception ex)
                            {
                                stepResults.Add(new MetaStepExecutionResult(step.Id, step.Kind, ToolResultStatuses.Failed, "template_render_failed", 0, Continued: continueOnError));

                                if (TryActivateFailureBranch(step, stepById, pending, blocked, failureAliases))
                                {
                                    progress = true;
                                    break;
                                }

                                if (!continueOnError)
                                {
                                    return ReturnMetaExecutionOutput(session, metaSkill, finalText: null, stepResults, $"Meta step '{step.Id}' template render failed: {ex.Message}", preserveCheckpoint: false);
                                }

                                CompleteMetaStepOutput(step, ex.Message, pending, outputs, failureAliases);
                                routePlanner.ApplyCompletionRouting(step, new MetaExecutionContext(input, outputs), stepById, blocked, pending, dependentsByStep);
                                progress = true;
                                break;
                            }
                        }

                        var renderedStdin = step.SkillExecStdin;
                        if (!string.IsNullOrWhiteSpace(renderedStdin))
                        {
                            try
                            {
                                renderedStdin = templateRenderer.Render(renderedStdin, new MetaExecutionContext(input, outputs));
                            }
                            catch (Exception ex)
                            {
                                stepResults.Add(new MetaStepExecutionResult(step.Id, step.Kind, ToolResultStatuses.Failed, "template_render_failed", 0, Continued: continueOnError));

                                if (TryActivateFailureBranch(step, stepById, pending, blocked, failureAliases))
                                {
                                    progress = true;
                                    break;
                                }

                                if (!continueOnError)
                                {
                                    return ReturnMetaExecutionOutput(session, metaSkill, finalText: null, stepResults, $"Meta step '{step.Id}' template render failed: {ex.Message}", preserveCheckpoint: false);
                                }

                                CompleteMetaStepOutput(step, ex.Message, pending, outputs, failureAliases);
                                routePlanner.ApplyCompletionRouting(step, new MetaExecutionContext(input, outputs), stepById, blocked, pending, dependentsByStep);
                                progress = true;
                                break;
                            }
                        }

                        var stepSw = Stopwatch.StartNew();
                        var skillExecResult = await ExecuteMetaSkillExecStepWithPolicyAsync(
                            delegatedSkill,
                            step,
                            renderedArgs,
                            renderedCwd,
                            renderedStdin,
                            ct);
                        stepSw.Stop();

                        var completed = string.Equals(skillExecResult.ResultStatus, ToolResultStatuses.Completed, StringComparison.Ordinal);
                        var resultStatus = skillExecResult.ResultStatus;
                        var failureCode = skillExecResult.FailureCode;
                        if (completed && !TryValidateMetaStepOutput(step, skillExecResult.ResultText, out failureCode))
                        {
                            completed = false;
                            resultStatus = ToolResultStatuses.Failed;
                        }

                        stepResults.Add(new MetaStepExecutionResult(
                            step.Id,
                            step.Kind,
                            resultStatus,
                            failureCode,
                            stepSw.Elapsed.TotalMilliseconds,
                            Continued: !completed && continueOnError,
                            ExecutionEvidence: BuildSkillExecExecutionEvidence(step.SkillExecEntrypoint, renderedArgs, renderedStdin, step.SkillExecParseMode)));

                        if (completed)
                        {
                            CompleteMetaStepOutput(step, skillExecResult.ResultText, pending, outputs, failureAliases);
                            routePlanner.ApplyCompletionRouting(step, new MetaExecutionContext(input, outputs), stepById, blocked, pending, dependentsByStep);
                            progress = true;
                            break;
                        }

                        if (TryActivateFailureBranch(step, stepById, pending, blocked, failureAliases))
                        {
                            progress = true;
                            break;
                        }

                        if (!continueOnError)
                        {
                            return ReturnMetaExecutionOutput(session, metaSkill, finalText: null, stepResults, skillExecResult.FailureMessage ?? $"Meta step '{step.Id}' failed with status '{skillExecResult.ResultStatus}'.", preserveCheckpoint: false);
                        }

                        CompleteMetaStepOutput(step, skillExecResult.ResultText, pending, outputs, failureAliases);
                        routePlanner.ApplyCompletionRouting(step, new MetaExecutionContext(input, outputs), stepById, blocked, pending, dependentsByStep);
                        progress = true;
                        break;
                    }

                    case "agent":
                    {
                        var delegatedSkill = !string.IsNullOrWhiteSpace(step.Skill)
                            ? LoadedSkills.FirstOrDefault(skill =>
                                !skill.DisableModelInvocation &&
                                string.Equals(skill.Name, step.Skill, StringComparison.OrdinalIgnoreCase))
                            : null;

                        var delegatedInstructions = delegatedSkill is null
                            ? string.Empty
                            : SkillPromptBuilder.BuildSkillBody(delegatedSkill);

                        var messages = new List<ChatMessage>
                        {
                            new(ChatRole.System,
                                string.IsNullOrWhiteSpace(delegatedInstructions)
                                    ? "You are executing a meta-skill delegated step. Return only the final useful result for this step."
                                    : "You are executing a meta-skill delegated step. Follow the delegated skill instructions. Return only the final useful result for this step.\n\n" + delegatedInstructions),
                            new(ChatRole.User, string.IsNullOrWhiteSpace(stepInput) ? input ?? string.Empty : stepInput)
                        };

                        var options = new ChatOptions
                        {
                            ModelId = session.ModelOverride ?? _config.Model,
                            MaxOutputTokens = _maxTokens,
                            Temperature = _temperature
                        };

                        var stepSw = Stopwatch.StartNew();
                        var llmResult = await ExecuteMetaLlmStepWithPolicyAsync(
                            step,
                            token => CallLlmWithResilienceAsync(session, messages, options, turnCtx, token),
                            ct);
                        stepSw.Stop();

                        if (!llmResult.Completed)
                        {
                            var failureMessage = llmResult.FailureMessage ?? $"Meta step '{step.Id}' failed before producing a response.";
                            stepResults.Add(new MetaStepExecutionResult(step.Id, step.Kind, ToolResultStatuses.Failed, llmResult.FailureCode, stepSw.Elapsed.TotalMilliseconds, Continued: continueOnError));

                            if (TryActivateFailureBranch(step, stepById, pending, blocked, failureAliases))
                            {
                                progress = true;
                                break;
                            }

                            if (!continueOnError)
                            {
                                return ReturnMetaExecutionOutput(session, metaSkill, finalText: null, stepResults, failureMessage, preserveCheckpoint: false);
                            }

                            CompleteMetaStepOutput(step, failureMessage, pending, outputs, failureAliases);
                            routePlanner.ApplyCompletionRouting(step, new MetaExecutionContext(input, outputs), stepById, blocked, pending, dependentsByStep);
                            progress = true;
                            break;
                        }

                        var stepOutput = llmResult.ExecutionResult!.Response.Text ?? string.Empty;
                        if (!TryValidateMetaStepOutput(step, stepOutput, out var failureCode))
                        {
                            stepResults.Add(new MetaStepExecutionResult(step.Id, step.Kind, ToolResultStatuses.Failed, failureCode, stepSw.Elapsed.TotalMilliseconds, Continued: continueOnError));

                            if (TryActivateFailureBranch(step, stepById, pending, blocked, failureAliases))
                            {
                                progress = true;
                                break;
                            }

                            if (!continueOnError)
                            {
                                return ReturnMetaExecutionOutput(session, metaSkill, finalText: null, stepResults, $"Meta step '{step.Id}' failed output contract validation.", preserveCheckpoint: false);
                            }

                            CompleteMetaStepOutput(step, stepOutput, pending, outputs, failureAliases);
                            routePlanner.ApplyCompletionRouting(step, new MetaExecutionContext(input, outputs), stepById, blocked, pending, dependentsByStep);
                            progress = true;
                            break;
                        }

                        CompleteMetaStepOutput(step, stepOutput, pending, outputs, failureAliases);
                        routePlanner.ApplyCompletionRouting(step, new MetaExecutionContext(input, outputs), stepById, blocked, pending, dependentsByStep);
                        progress = true;
                        stepResults.Add(new MetaStepExecutionResult(step.Id, step.Kind, ToolResultStatuses.Completed, null, stepSw.Elapsed.TotalMilliseconds, Continued: false));
                        break;
                    }

                    case "llm_chat":
                    {
                        var llmSystemPrompt = GetOptionalString(stepArgs, "system_prompt")
                            ?? "You are executing a meta-skill llm_chat step. Return only the final useful result for this step.";
                        var options = new ChatOptions
                        {
                            ModelId = session.ModelOverride ?? _config.Model,
                            MaxOutputTokens = GetOptionalInt32(stepArgs, "max_tokens") ?? _maxTokens,
                            Temperature = GetOptionalSingle(stepArgs, "temperature") ?? _temperature
                        };
                        var messages = new List<ChatMessage>
                        {
                            new(ChatRole.System, llmSystemPrompt),
                            new(ChatRole.User, stepInput)
                        };

                        var stepSw = Stopwatch.StartNew();
                        var llmResult = await ExecuteMetaLlmStepWithPolicyAsync(
                            step,
                            token => CallLlmWithResilienceAsync(session, messages, options, turnCtx, token),
                            ct);
                        stepSw.Stop();

                        if (!llmResult.Completed)
                        {
                            var failureMessage = llmResult.FailureMessage ?? $"Meta step '{step.Id}' failed before producing a response.";
                            stepResults.Add(new MetaStepExecutionResult(step.Id, step.Kind, ToolResultStatuses.Failed, llmResult.FailureCode, stepSw.Elapsed.TotalMilliseconds, Continued: continueOnError));

                            if (TryActivateFailureBranch(step, stepById, pending, blocked, failureAliases))
                            {
                                progress = true;
                                break;
                            }

                            if (!continueOnError)
                            {
                                return ReturnMetaExecutionOutput(session, metaSkill, finalText: null, stepResults, failureMessage, preserveCheckpoint: false);
                            }

                            CompleteMetaStepOutput(step, failureMessage, pending, outputs, failureAliases);
                            routePlanner.ApplyCompletionRouting(step, new MetaExecutionContext(input, outputs), stepById, blocked, pending, dependentsByStep);
                            progress = true;
                            break;
                        }

                        var stepOutput = llmResult.ExecutionResult!.Response.Text ?? string.Empty;
                        if (!TryValidateMetaStepOutput(step, stepOutput, out var failureCode))
                        {
                            stepResults.Add(new MetaStepExecutionResult(step.Id, step.Kind, ToolResultStatuses.Failed, failureCode, stepSw.Elapsed.TotalMilliseconds, Continued: continueOnError));

                            if (TryActivateFailureBranch(step, stepById, pending, blocked, failureAliases))
                            {
                                progress = true;
                                break;
                            }

                            if (!continueOnError)
                            {
                                return ReturnMetaExecutionOutput(session, metaSkill, finalText: null, stepResults, $"Meta step '{step.Id}' failed output contract validation.", preserveCheckpoint: false);
                            }

                            CompleteMetaStepOutput(step, stepOutput, pending, outputs, failureAliases);
                            routePlanner.ApplyCompletionRouting(step, new MetaExecutionContext(input, outputs), stepById, blocked, pending, dependentsByStep);
                            progress = true;
                            break;
                        }

                        CompleteMetaStepOutput(step, stepOutput, pending, outputs, failureAliases);
                        routePlanner.ApplyCompletionRouting(step, new MetaExecutionContext(input, outputs), stepById, blocked, pending, dependentsByStep);
                        progress = true;
                        stepResults.Add(new MetaStepExecutionResult(step.Id, step.Kind, ToolResultStatuses.Completed, null, stepSw.Elapsed.TotalMilliseconds, Continued: false));
                        break;
                    }

                    case "llm_classify":
                    {
                        if (!TryGetStringArray(stepArgs, "options", out var optionsValues) || optionsValues.Count == 0)
                        {
                            return ReturnMetaExecutionOutput(session, metaSkill, finalText: null, stepResults, $"Meta step '{step.Id}' is 'llm_classify' but does not declare non-empty options.", preserveCheckpoint: false);
                        }

                        var classifyPrompt = BuildClassificationPrompt(stepInput, optionsValues);
                        var messages = new List<ChatMessage>
                        {
                            new(ChatRole.System, "You are a strict classifier. Return exactly one label from the provided options."),
                            new(ChatRole.User, classifyPrompt)
                        };
                        var options = new ChatOptions
                        {
                            ModelId = session.ModelOverride ?? _config.Model,
                            MaxOutputTokens = GetOptionalInt32(stepArgs, "max_tokens") ?? 32,
                            Temperature = GetOptionalSingle(stepArgs, "temperature") ?? 0
                        };

                        var stepSw = Stopwatch.StartNew();
                        var llmResult = await ExecuteMetaLlmStepWithPolicyAsync(
                            step,
                            token => CallLlmWithResilienceAsync(session, messages, options, turnCtx, token),
                            ct);
                        stepSw.Stop();

                        if (!llmResult.Completed)
                        {
                            var failureMessage = llmResult.FailureMessage ?? $"Meta step '{step.Id}' failed before producing a response.";
                            stepResults.Add(new MetaStepExecutionResult(step.Id, step.Kind, ToolResultStatuses.Failed, llmResult.FailureCode, stepSw.Elapsed.TotalMilliseconds, Continued: continueOnError));

                            if (TryActivateFailureBranch(step, stepById, pending, blocked, failureAliases))
                            {
                                progress = true;
                                break;
                            }

                            if (!continueOnError)
                            {
                                return ReturnMetaExecutionOutput(session, metaSkill, finalText: null, stepResults, failureMessage, preserveCheckpoint: false);
                            }

                            CompleteMetaStepOutput(step, failureMessage, pending, outputs, failureAliases);
                            routePlanner.ApplyCompletionRouting(step, new MetaExecutionContext(input, outputs), stepById, blocked, pending, dependentsByStep);
                            progress = true;
                            break;
                        }

                        var rawLabel = llmResult.ExecutionResult!.Response.Text?.Trim() ?? string.Empty;
                        if (!TryResolveClassificationLabel(rawLabel, optionsValues, out var selectedLabel))
                        {
                            stepResults.Add(new MetaStepExecutionResult(step.Id, step.Kind, ToolResultStatuses.Failed, "invalid_classification", stepSw.Elapsed.TotalMilliseconds, Continued: continueOnError));

                            if (TryActivateFailureBranch(step, stepById, pending, blocked, failureAliases))
                            {
                                progress = true;
                                break;
                            }

                            if (!continueOnError)
                            {
                                return ReturnMetaExecutionOutput(session, metaSkill, finalText: null, stepResults, $"Meta step '{step.Id}' returned classification '{rawLabel}' outside declared options.", preserveCheckpoint: false);
                            }

                            CompleteMetaStepOutput(step, rawLabel, pending, outputs, failureAliases);
                            routePlanner.ApplyCompletionRouting(step, new MetaExecutionContext(input, outputs), stepById, blocked, pending, dependentsByStep);
                            progress = true;
                            break;
                        }

                        if (!TryValidateMetaStepOutput(step, selectedLabel!, out var outputFailureCode))
                        {
                            stepResults.Add(new MetaStepExecutionResult(step.Id, step.Kind, ToolResultStatuses.Failed, outputFailureCode, stepSw.Elapsed.TotalMilliseconds, Continued: continueOnError));

                            if (TryActivateFailureBranch(step, stepById, pending, blocked, failureAliases))
                            {
                                progress = true;
                                break;
                            }

                            if (!continueOnError)
                            {
                                return ReturnMetaExecutionOutput(session, metaSkill, finalText: null, stepResults, $"Meta step '{step.Id}' failed output contract validation.", preserveCheckpoint: false);
                            }

                            CompleteMetaStepOutput(step, selectedLabel!, pending, outputs, failureAliases);
                            routePlanner.ApplyCompletionRouting(step, new MetaExecutionContext(input, outputs), stepById, blocked, pending, dependentsByStep);
                            progress = true;
                            break;
                        }

                        CompleteMetaStepOutput(step, selectedLabel!, pending, outputs, failureAliases);
                        progress = true;
                        stepResults.Add(new MetaStepExecutionResult(step.Id, step.Kind, ToolResultStatuses.Completed, null, stepSw.Elapsed.TotalMilliseconds, Continued: false));

                        routePlanner.ApplyCompletionRouting(step, new MetaExecutionContext(input, outputs), stepById, blocked, pending, dependentsByStep);

                        if (TryGetRouteMap(stepArgs, out var routeMap))
                        {
                            ApplyClassificationRouting(
                                selectedLabel!,
                                routeMap,
                                blocked,
                                pending,
                                dependentsByStep,
                                stepById);
                        }

                        break;
                    }

                    case "user_input":
                    {
                        var userValue = GetOptionalString(stepArgs, "value")
                            ?? GetOptionalString(stepArgs, "default")
                            ?? GetOptionalString(stepArgs, "default_input")
                            ?? stepInput;

                        var skipClarify = step.Clarify is not null
                            && !string.IsNullOrWhiteSpace(step.Clarify.SkipIf)
                            && conditionEvaluator.Evaluate(step.Clarify.SkipIf, new MetaExecutionContext(input, outputs));

                        if (string.IsNullOrWhiteSpace(userValue))
                        {
                            if (skipClarify)
                            {
                                CompleteMetaStepOutput(step, string.Empty, pending, outputs, failureAliases);
                                routePlanner.ApplyCompletionRouting(step, new MetaExecutionContext(input, outputs), stepById, blocked, pending, dependentsByStep);
                                progress = true;
                                stepResults.Add(new MetaStepExecutionResult(step.Id, step.Kind, ToolResultStatuses.Completed, null, 0, Continued: false));
                                break;
                            }

                            var prompt = GetOptionalString(stepArgs, "prompt")
                                ?? $"Please provide input for step '{step.Id}'.";
                            stepResults.Add(new MetaStepExecutionResult(step.Id, step.Kind, ToolResultStatuses.Failed, "user_input_required", 0, Continued: continueOnError));
                            SaveMetaExecutionCheckpoint(
                                session,
                                metaSkill.Name,
                                step.Id,
                                prompt,
                                pending,
                                blocked,
                                outputs,
                                failureAliases,
                                stepResults);

                            if (TryActivateFailureBranch(step, stepById, pending, blocked, failureAliases))
                            {
                                progress = true;
                                break;
                            }

                            if (!continueOnError)
                            {
                                return ReturnMetaExecutionOutput(session, metaSkill, finalText: null, stepResults, $"Meta step '{step.Id}' requires user input but no value/default is available in the current execution context. Prompt: {prompt}", preserveCheckpoint: true);
                            }

                            CompleteMetaStepOutput(step, string.Empty, pending, outputs, failureAliases);
                            progress = true;
                            break;
                        }

                        var normalizedUserValue = userValue;
                        if (IsClarifyInputTimedOut(session, metaSkill.Name, step))
                        {
                            stepResults.Add(new MetaStepExecutionResult(step.Id, step.Kind, ToolResultStatuses.Failed, "user_input_timeout", 0, Continued: continueOnError));

                            if (TryActivateFailureBranch(step, stepById, pending, blocked, failureAliases))
                            {
                                progress = true;
                                break;
                            }

                            if (!continueOnError)
                                return ReturnMetaExecutionOutput(session, metaSkill, finalText: null, stepResults, $"Meta step '{step.Id}' clarify input timed out.", preserveCheckpoint: false);

                            CompleteMetaStepOutput(step, string.Empty, pending, outputs, failureAliases);
                            routePlanner.ApplyCompletionRouting(step, new MetaExecutionContext(input, outputs), stepById, blocked, pending, dependentsByStep);
                            progress = true;
                            break;
                        }

                        if (step.Clarify is not null)
                        {
                            var clarifyResult = clarifyValidator.ValidateAndNormalize(userValue, step.Clarify);
                            if (!clarifyResult.IsValid)
                            {
                                stepResults.Add(new MetaStepExecutionResult(step.Id, step.Kind, ToolResultStatuses.Failed, clarifyResult.FailureCode, 0, Continued: continueOnError));

                                if (TryActivateFailureBranch(step, stepById, pending, blocked, failureAliases))
                                {
                                    progress = true;
                                    break;
                                }

                                if (!continueOnError)
                                {
                                    var clarifyFailure = clarifyResult.FailureCode switch
                                    {
                                        "user_input_cancelled" => $"Meta step '{step.Id}' clarify input was cancelled.",
                                        "user_input_timeout" => $"Meta step '{step.Id}' clarify input timed out.",
                                        _ => $"Meta step '{step.Id}' failed clarify validation."
                                    };
                                    return ReturnMetaExecutionOutput(session, metaSkill, finalText: null, stepResults, clarifyFailure, preserveCheckpoint: false);
                                }

                                CompleteMetaStepOutput(step, userValue, pending, outputs, failureAliases);
                                routePlanner.ApplyCompletionRouting(step, new MetaExecutionContext(input, outputs), stepById, blocked, pending, dependentsByStep);
                                progress = true;
                                break;
                            }

                            normalizedUserValue = clarifyResult.NormalizedOutput ?? userValue;
                        }

                        if (!TryValidateMetaStepOutput(step, normalizedUserValue, out var failureCode))
                        {
                            stepResults.Add(new MetaStepExecutionResult(step.Id, step.Kind, ToolResultStatuses.Failed, failureCode, 0, Continued: continueOnError));

                            if (TryActivateFailureBranch(step, stepById, pending, blocked, failureAliases))
                            {
                                progress = true;
                                break;
                            }

                            if (!continueOnError)
                            {
                                return ReturnMetaExecutionOutput(session, metaSkill, finalText: null, stepResults, $"Meta step '{step.Id}' failed output contract validation.", preserveCheckpoint: false);
                            }

                            CompleteMetaStepOutput(step, userValue, pending, outputs, failureAliases);
                            routePlanner.ApplyCompletionRouting(step, new MetaExecutionContext(input, outputs), stepById, blocked, pending, dependentsByStep);
                            progress = true;
                            break;
                        }

                        CompleteMetaStepOutput(step, normalizedUserValue, pending, outputs, failureAliases);
                        routePlanner.ApplyCompletionRouting(step, new MetaExecutionContext(input, outputs), stepById, blocked, pending, dependentsByStep);
                        progress = true;
                        stepResults.Add(new MetaStepExecutionResult(step.Id, step.Kind, ToolResultStatuses.Completed, null, 0, Continued: false));
                        break;
                    }

                    case "fan_out":
                    {
                        // Managed primarily by TryExecuteFanOutStepAsync (called above the loop).
                        // If a step reaches here its dependencies are still unsatisfied —
                        // skip and retry next iteration.
                        break;
                    }

                    default:
                        return ReturnMetaExecutionOutput(session, metaSkill, finalText: null, stepResults, $"Meta step '{step.Id}' has unsupported kind '{step.Kind}'.", preserveCheckpoint: false);
                }
            }

            if (progress)
                continue;

            var remaining = steps.Where(step => pending.Contains(step.Id) && !blocked.Contains(step.Id)).Select(step => step.Id).ToArray();
            if (remaining.Length == 0)
                break;

            return ReturnMetaExecutionOutput(session, metaSkill, finalText: null, stepResults, $"Meta execution graph stalled. Remaining unresolved steps: {string.Join(", ", remaining)}.", preserveCheckpoint: false);
        }

        var executedStepIds = steps.Where(step => outputs.ContainsKey(step.Id)).Select(static step => step.Id).ToList();
        var finalText = ResolveMetaFinalText(metaSkill, steps, outputs, executedStepIds);

        return ReturnMetaExecutionOutput(session, metaSkill, finalText, stepResults, error: null, preserveCheckpoint: false);
    }

    private async Task<bool> TryExecuteParallelToolWaveAsync(
        Session session,
        SkillDefinition metaSkill,
        IReadOnlyList<MetaSkillStepDefinition> steps,
        IReadOnlyDictionary<string, MetaSkillStepDefinition> stepById,
        Dictionary<string, List<string>> dependentsByStep,
        HashSet<string> pending,
        HashSet<string> blocked,
        Dictionary<string, string> outputs,
        Dictionary<string, string> failureAliases,
        List<MetaStepExecutionResult> stepResults,
        string? input,
        TurnContext turnCtx,
        MetaTemplateRenderer templateRenderer,
        MetaConditionEvaluator conditionEvaluator,
        MetaToolArgumentResolver toolArgumentResolver,
        MetaRoutePlanner routePlanner,
        CancellationToken ct)
    {
        if (pending.Count < 2)
            return false;

        var candidates = new List<MetaParallelToolStepCandidate>();
        foreach (var step in steps)
        {
            if (!pending.Contains(step.Id) || blocked.Contains(step.Id))
                continue;

            var blockedByDependency = false;
            var waitingForDependency = false;
            foreach (var dependency in step.DependsOn)
            {
                if (blocked.Contains(dependency))
                {
                    blockedByDependency = true;
                    break;
                }

                if (!outputs.ContainsKey(dependency))
                {
                    waitingForDependency = true;
                    break;
                }
            }

            if (blockedByDependency || waitingForDependency)
                continue;

            if (!string.Equals(NormalizeMetaStepKind(step.Kind), "tool_call", StringComparison.Ordinal))
                continue;

            if (!string.IsNullOrWhiteSpace(step.OnFailure) || step.Routes.Count > 0)
                continue;

            var stepArgs = DeserializeStepArgs(step.WithJson);
            var continueOnError = GetOptionalBoolean(stepArgs, "continue_on_error") ?? false;
            if (!continueOnError)
                continue;

            var metaContext = new MetaExecutionContext(input, outputs);
            if (!string.IsNullOrWhiteSpace(step.When) && !conditionEvaluator.Evaluate(step.When, metaContext))
                continue;

            var toolName = step.Tool;
            if (string.IsNullOrWhiteSpace(toolName))
                continue;

            if (step.ToolAllowlist.Count > 0 && !step.ToolAllowlist.Contains(toolName, StringComparer.OrdinalIgnoreCase))
                continue;

            if (!IsToolAllowedByMetaCapabilities(metaSkill, toolName))
                continue;

            try
            {
                _ = templateRenderer.Render(
                    GetOptionalString(stepArgs, "input") ?? input ?? string.Empty,
                    metaContext);
            }
            catch (Exception)
            {
                continue;
            }

            string toolArgsJson;
            try
            {
                toolArgsJson = toolArgumentResolver.Resolve(
                    metaSkill.Composition?.ToolArgsJson,
                    step.WithJson,
                    step.ToolArgsJson,
                    metaContext);
            }
            catch (InvalidOperationException)
            {
                continue;
            }

            candidates.Add(new MetaParallelToolStepCandidate(step, toolName, toolArgsJson));
        }

        if (candidates.Count < 2)
            return false;

        var executions = await Task.WhenAll(candidates.Select(async candidate =>
        {
            var stepSw = Stopwatch.StartNew();
            var toolResult = await ExecuteMetaToolStepWithPolicyAsync(
                metaSkill,
                candidate.Step,
                candidate.ToolName,
                candidate.ToolArgsJson,
                session,
                turnCtx,
                ct);
            stepSw.Stop();
            return new MetaParallelToolStepExecution(candidate.Step, toolResult, stepSw.Elapsed.TotalMilliseconds);
        }));

        foreach (var execution in executions)
        {
            var completed = string.Equals(execution.ToolResult.ResultStatus, ToolResultStatuses.Completed, StringComparison.Ordinal);
            var resultStatus = execution.ToolResult.ResultStatus;
            var failureCode = execution.ToolResult.FailureCode;
            if (completed && !TryValidateMetaStepOutput(execution.Step, execution.ToolResult.ResultText, out failureCode))
            {
                completed = false;
                resultStatus = ToolResultStatuses.Failed;
            }

            stepResults.Add(new MetaStepExecutionResult(
                execution.Step.Id,
                execution.Step.Kind,
                resultStatus,
                failureCode,
                execution.DurationMs,
                Continued: !completed));

            CompleteMetaStepOutput(execution.Step, execution.ToolResult.ResultText, pending, outputs, failureAliases);
            routePlanner.ApplyCompletionRouting(execution.Step, new MetaExecutionContext(input, outputs), stepById, blocked, pending, dependentsByStep);
        }

        return true;
    }

    private async Task<(string Output, string? FailureCode)> ExecuteFanOutChildAsync(
        SkillDefinition metaSkill,
        MetaSkillStepDefinition template,
        string childId,
        string childInput,
        MetaExecutionContext childContext,
        Session session,
        TurnContext turnCtx,
        CancellationToken ct)
    {
        switch (NormalizeMetaStepKind(template.Kind))
        {
            case "tool_call":
            {
                var toolName = template.Tool;
                if (string.IsNullOrWhiteSpace(toolName))
                    return ($"Error: fan-out child step '{childId}' is 'tool_call' but does not declare a tool.", "missing_tool");

                if (template.ToolAllowlist.Count > 0 && !template.ToolAllowlist.Contains(toolName, StringComparer.OrdinalIgnoreCase))
                    return ($"Error: tool '{toolName}' is not allowlisted for fan-out child step '{childId}'.", "tool_not_allowlisted");

                if (!IsToolAllowedByMetaCapabilities(metaSkill, toolName))
                    return ($"Error: tool '{toolName}' is not permitted by metadata capabilities for fan-out child step '{childId}'.", "metadata_capability_denied");

                string toolArgsJson;
                try
                {
                    toolArgsJson = new MetaToolArgumentResolver(new MetaTemplateRenderer())
                        .Resolve(null, template.WithJson, template.ToolArgsJson, childContext);
                }
                catch (InvalidOperationException)
                {
                    return ($"Error: invalid tool arguments for child step '{childId}'.", "invalid_tool_args");
                }

                var result = await ExecuteMetaToolStepWithPolicyAsync(
                    metaSkill,
                    new MetaSkillStepDefinition { Id = childId, Kind = template.Kind, Retry = template.Retry, TimeoutSeconds = template.TimeoutSeconds },
                    toolName,
                    toolArgsJson,
                    session,
                    turnCtx,
                    ct);

                var completed = string.Equals(result.ResultStatus, ToolResultStatuses.Completed, StringComparison.Ordinal);
                var failureCode = result.FailureCode;
                if (completed && !TryValidateMetaStepOutput(template, result.ResultText, out failureCode))
                    completed = false;
                return completed ? (result.ResultText, null) : (result.ResultText, failureCode);
            }

            case "llm_chat":
            {
                var stepArgs = DeserializeStepArgs(template.WithJson);
                var systemPrompt = GetOptionalString(stepArgs, "system_prompt")
                    ?? "Return the result for this step.";
                var chatOptions = new ChatOptions
                {
                    ModelId = session.ModelOverride ?? _config.Model,
                    MaxOutputTokens = GetOptionalInt32(stepArgs, "max_tokens") ?? _maxTokens,
                    Temperature = GetOptionalSingle(stepArgs, "temperature") ?? _temperature
                };
                var messages = new List<ChatMessage>
                {
                    new(ChatRole.System, systemPrompt),
                    new(ChatRole.User, childInput)
                };

                var llmResult = await ExecuteMetaLlmStepWithPolicyAsync(
                    new MetaSkillStepDefinition { Id = childId, Kind = template.Kind, Retry = template.Retry, TimeoutSeconds = template.TimeoutSeconds },
                    token => CallLlmWithResilienceAsync(session, messages, chatOptions, turnCtx, token),
                    ct);

                if (!llmResult.Completed)
                    return (llmResult.FailureMessage ?? "", llmResult.FailureCode);

                var output = llmResult.ExecutionResult?.Response.Text ?? "";
                var failureCode = (string?)null;
                if (!TryValidateMetaStepOutput(template, output, out failureCode))
                    return (output, failureCode);

                return (output, null);
            }

            default:
                return ($"Error: unsupported fan_out child kind '{template.Kind}'.", "unsupported_child_kind");
        }
    }

    private static string ReturnMetaExecutionOutput(
        Session session,
        SkillDefinition metaSkill,
        string? finalText,
        IReadOnlyList<MetaStepExecutionResult> stepResults,
        string? error,
        bool preserveCheckpoint)
    {
        AppendMetaRunHistory(session, metaSkill.Name, finalText, stepResults, error, preserveCheckpoint);

        if (!preserveCheckpoint)
            ClearMetaExecutionCheckpoint(session, metaSkill.Name);

        return BuildMetaExecutionOutput(metaSkill, finalText, stepResults, error);
    }

    private static void AppendMetaRunHistory(
        Session session,
        string skillName,
        string? finalText,
        IReadOnlyList<MetaStepExecutionResult> stepResults,
        string? error,
        bool preserveCheckpoint)
    {
        session.MetaRunHistory.Add(new SessionMetaRunRecord
        {
            RunId = $"meta_{Guid.NewGuid():N}",
            SkillName = skillName,
            Status = preserveCheckpoint ? "paused" : error is null ? "completed" : "failed",
            FinalText = finalText,
            Error = error,
            ErrorCode = string.IsNullOrWhiteSpace(error) ? null : DeriveMetaErrorCode(error, stepResults),
            StartedAtUtc = DateTimeOffset.UtcNow,
            CompletedAtUtc = DateTimeOffset.UtcNow,
            StepResults = stepResults.Select(static result => new SessionMetaStepResult
            {
                Id = result.Id,
                Kind = result.Kind,
                Status = result.Status,
                FailureCode = result.FailureCode,
                DurationMs = result.DurationMs,
                Continued = result.Continued,
                ExecutionEvidence = result.ExecutionEvidence
            }).ToList()
        });
    }

    private static string BuildMetaExecutionOutput(
        SkillDefinition metaSkill,
        string? finalText,
        IReadOnlyList<MetaStepExecutionResult> stepResults,
        string? error)
    {
        if (!string.Equals(metaSkill.FinalTextMode, "structured", StringComparison.OrdinalIgnoreCase))
            return error is null ? finalText ?? string.Empty : $"Error: {error}";

        return BuildStructuredMetaExecutionJson(metaSkill.Name, finalText, stepResults, error);
    }

    private static string ResolveMetaFinalText(
        SkillDefinition metaSkill,
        IReadOnlyList<MetaSkillStepDefinition> steps,
        IReadOnlyDictionary<string, string> outputs,
        IReadOnlyList<string> executedStepIds)
    {
        var mode = metaSkill.FinalTextMode?.Trim();
        if (string.IsNullOrWhiteSpace(mode) || mode.Equals("auto", StringComparison.OrdinalIgnoreCase))
        {
            if (executedStepIds.Count == 0)
                return string.Empty;

            return outputs[executedStepIds[^1]];
        }

        if (mode.Equals("raw", StringComparison.OrdinalIgnoreCase))
        {
            if (executedStepIds.Count == 0)
                return string.Empty;

            return outputs[executedStepIds[^1]];
        }

        if (mode.StartsWith("step:", StringComparison.OrdinalIgnoreCase))
        {
            var finalStepId = mode[5..].Trim();
            if (!string.IsNullOrWhiteSpace(finalStepId) && outputs.TryGetValue(finalStepId, out var finalStepOutput))
                return finalStepOutput;
        }

        if (executedStepIds.Count == 0)
            return string.Empty;

        return outputs[executedStepIds[^1]];
    }

    private static bool TryRestoreMetaExecutionCheckpoint(
        Session session,
        string skillName,
        IEnumerable<string> stepIds,
        HashSet<string> pending,
        HashSet<string> blocked,
        Dictionary<string, string> outputs,
        Dictionary<string, string> failureAliases,
        List<MetaStepExecutionResult> stepResults,
        out string? waitingPrompt)
    {
        waitingPrompt = null;
        var checkpoint = session.MetaExecutionCheckpoint;
        if (checkpoint is null || !string.Equals(checkpoint.SkillName, skillName, StringComparison.OrdinalIgnoreCase))
            return false;

        var validStepIds = new HashSet<string>(stepIds, StringComparer.OrdinalIgnoreCase);
        foreach (var pendingStep in checkpoint.PendingStepIds)
        {
            if (!validStepIds.Contains(pendingStep))
            {
                session.MetaExecutionCheckpoint = null;
                return false;
            }
        }

        foreach (var blockedStep in checkpoint.BlockedStepIds)
        {
            if (!validStepIds.Contains(blockedStep))
            {
                session.MetaExecutionCheckpoint = null;
                return false;
            }
        }

        foreach (var stepId in checkpoint.Outputs.Keys)
        {
            if (!validStepIds.Contains(stepId))
            {
                session.MetaExecutionCheckpoint = null;
                return false;
            }
        }

        foreach (var stepId in checkpoint.FailureAliases.Keys)
        {
            if (!validStepIds.Contains(stepId))
            {
                session.MetaExecutionCheckpoint = null;
                return false;
            }
        }

        foreach (var aliasTarget in checkpoint.FailureAliases.Values)
        {
            if (!validStepIds.Contains(aliasTarget))
            {
                session.MetaExecutionCheckpoint = null;
                return false;
            }
        }

        foreach (var result in checkpoint.StepResults)
        {
            if (!validStepIds.Contains(result.Id))
            {
                session.MetaExecutionCheckpoint = null;
                return false;
            }
        }

        pending.Clear();
        foreach (var pendingStep in checkpoint.PendingStepIds)
            pending.Add(pendingStep);

        blocked.Clear();
        foreach (var blockedStep in checkpoint.BlockedStepIds)
            blocked.Add(blockedStep);

        outputs.Clear();
        foreach (var (key, value) in checkpoint.Outputs)
            outputs[key] = value;

        failureAliases.Clear();
        foreach (var (key, value) in checkpoint.FailureAliases)
            failureAliases[key] = value;

        stepResults.Clear();
        foreach (var result in checkpoint.StepResults)
        {
            stepResults.Add(new MetaStepExecutionResult(
                result.Id,
                result.Kind,
                result.Status,
                result.FailureCode,
                result.DurationMs,
                result.Continued));
        }

        checkpoint.LastUpdatedAtUtc = DateTimeOffset.UtcNow;
        waitingPrompt = checkpoint.Prompt;
        return true;
    }

    private static void SaveMetaExecutionCheckpoint(
        Session session,
        string skillName,
        string pendingStepId,
        string prompt,
        HashSet<string> pending,
        HashSet<string> blocked,
        Dictionary<string, string> outputs,
        Dictionary<string, string> failureAliases,
        IReadOnlyList<MetaStepExecutionResult> stepResults)
    {
        session.MetaExecutionCheckpoint = new SessionMetaExecutionCheckpoint
        {
            SkillName = skillName,
            PendingStepId = pendingStepId,
            Prompt = prompt,
            LastUpdatedAtUtc = DateTimeOffset.UtcNow,
            PendingStepIds = pending.ToList(),
            BlockedStepIds = blocked.ToList(),
            Outputs = new Dictionary<string, string>(outputs, StringComparer.OrdinalIgnoreCase),
            FailureAliases = new Dictionary<string, string>(failureAliases, StringComparer.OrdinalIgnoreCase),
            StepResults = stepResults.Select(static result => new SessionMetaStepResult
            {
                Id = result.Id,
                Kind = result.Kind,
                Status = result.Status,
                FailureCode = result.FailureCode,
                DurationMs = result.DurationMs,
                Continued = result.Continued,
                ExecutionEvidence = result.ExecutionEvidence
            }).ToList()
        };
    }

    private static SessionMetaStepExecutionEvidence? BuildSkillExecExecutionEvidence(
        string? entrypoint,
        IReadOnlyList<string> renderedArgs,
        string? renderedStdin,
        string? parseMode)
    {
        var hasEntrypoint = !string.IsNullOrWhiteSpace(entrypoint);
        if (!hasEntrypoint && renderedArgs.Count == 0)
            return null;

        var commandParts = new List<string>(5);
        if (hasEntrypoint)
            commandParts.Add(entrypoint!);
        commandParts.AddRange(renderedArgs.Take(4));
        var commandPreview = string.Join(" ", commandParts);
        var hasStdin = !string.IsNullOrEmpty(renderedStdin);
        return new SessionMetaStepExecutionEvidence
        {
            CommandPreview = commandPreview,
            InputMode = hasStdin ? "stdin" : "args",
            StdinBytes = hasStdin ? System.Text.Encoding.UTF8.GetByteCount(renderedStdin!) : 0,
            ParseMode = string.IsNullOrWhiteSpace(parseMode) ? "text" : parseMode
        };
    }

    private static void ClearMetaExecutionCheckpoint(Session session, string skillName)
    {
        if (session.MetaExecutionCheckpoint is null)
            return;

        if (!string.Equals(session.MetaExecutionCheckpoint.SkillName, skillName, StringComparison.OrdinalIgnoreCase))
            return;

        session.MetaExecutionCheckpoint = null;
    }

    private static string BuildStructuredMetaExecutionJson(
        string skill,
        string? finalText,
        IReadOnlyList<MetaStepExecutionResult> stepResults,
        string? error)
    {
        using var stream = new MemoryStream();
        using (var writer = new Utf8JsonWriter(stream))
        {
            writer.WriteStartObject();
            writer.WriteString("skill", skill);
            writer.WriteString("final_text", finalText ?? string.Empty);
            if (!string.IsNullOrWhiteSpace(error))
            {
                writer.WriteString("error", error);
                var errorCode = DeriveMetaErrorCode(error, stepResults);
                if (!string.IsNullOrWhiteSpace(errorCode))
                    writer.WriteString("error_code", errorCode);
            }

            writer.WriteStartArray("steps");
            foreach (var step in stepResults)
            {
                writer.WriteStartObject();
                writer.WriteString("id", step.Id);
                writer.WriteString("kind", step.Kind);
                writer.WriteString("status", step.Status);
                writer.WriteNumber("duration_ms", step.DurationMs);
                writer.WriteBoolean("continued", step.Continued);
                if (!string.IsNullOrWhiteSpace(step.FailureCode))
                    writer.WriteString("failure_code", step.FailureCode);
                writer.WriteEndObject();
            }
            writer.WriteEndArray();
            writer.WriteEndObject();
        }

        return Encoding.UTF8.GetString(stream.ToArray());
    }

    private static string DeriveMetaErrorCode(string error, IReadOnlyList<MetaStepExecutionResult> stepResults)
    {
        for (var i = stepResults.Count - 1; i >= 0; i--)
        {
            var step = stepResults[i];
            if (!string.IsNullOrWhiteSpace(step.FailureCode))
                return step.FailureCode!;
        }

        if (error.Contains("depends on", StringComparison.OrdinalIgnoreCase))
            return "dependency_not_completed";
        if (error.Contains("does not declare a tool", StringComparison.OrdinalIgnoreCase))
            return "invalid_tool_step";
        if (error.Contains("unsupported kind", StringComparison.OrdinalIgnoreCase))
            return "unsupported_step_kind";
        if (error.Contains("failed with status", StringComparison.OrdinalIgnoreCase))
            return "step_failed";
        if (error.Contains("missing dependency", StringComparison.OrdinalIgnoreCase))
            return "invalid_dag";
        if (error.Contains("dependency cycle", StringComparison.OrdinalIgnoreCase))
            return "invalid_dag";
        if (error.Contains("execution graph stalled", StringComparison.OrdinalIgnoreCase))
            return "invalid_dag";
        if (error.Contains("requires user input", StringComparison.OrdinalIgnoreCase))
            return "user_input_required";
        if (error.Contains("classify", StringComparison.OrdinalIgnoreCase))
            return "invalid_classification";
        if (error.Contains("metadata capabilities", StringComparison.OrdinalIgnoreCase))
            return "metadata_capability_denied";

        return "meta_step_error";
    }

    private static bool IsToolAllowedByMetaCapabilities(SkillDefinition metaSkill, string toolName)
    {
        var capabilities = metaSkill.Metadata.Capabilities;
        if (capabilities.Length == 0)
            return true;

        var normalizedTool = toolName.Trim();
        foreach (var rawCapability in capabilities)
        {
            if (string.IsNullOrWhiteSpace(rawCapability))
                continue;

            var capability = rawCapability.Trim();
            if (capability.Equals("*", StringComparison.OrdinalIgnoreCase) ||
                capability.Equals("all-tools", StringComparison.OrdinalIgnoreCase) ||
                capability.Equals("tools:*", StringComparison.OrdinalIgnoreCase) ||
                capability.Equals("tool:*", StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }

            if (capability.Equals(normalizedTool, StringComparison.OrdinalIgnoreCase) ||
                capability.Equals($"tool:{normalizedTool}", StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }
        }

        return false;
    }

    private async Task<ToolExecutionResult> ExecuteMetaToolStepWithPolicyAsync(
        SkillDefinition metaSkill,
        MetaSkillStepDefinition step,
        string toolName,
        string toolArgsJson,
        Session session,
        TurnContext turnCtx,
        CancellationToken ct)
    {
        var maxAttempts = Math.Max(1, step.Retry.MaxAttempts);
        ToolExecutionResult? lastResult = null;

        for (var attempt = 1; attempt <= maxAttempts; attempt++)
        {
            using var timeoutCts = CreateMetaStepTimeout(step, ct);
            var effectiveCt = timeoutCts?.Token ?? ct;
            try
            {
                lastResult = await _toolExecutor.ExecuteAsync(
                    toolName,
                    toolArgsJson,
                    $"meta:{metaSkill?.Name ?? "fan_out"}:{step.Id}:attempt:{attempt}",
                    session,
                    turnCtx,
                    isStreaming: false,
                    approvalCallback: null,
                    ct: effectiveCt,
                    onDelta: null,
                    toolCallCount: attempt);
            }
            catch (OperationCanceledException) when (!ct.IsCancellationRequested)
            {
                lastResult = CreateMetaStepFailedToolResult(
                    toolName,
                    toolArgsJson,
                    "step_timeout",
                    $"Meta step '{step.Id}' timed out after {step.TimeoutSeconds} second(s).");
            }

            if (string.Equals(lastResult.ResultStatus, ToolResultStatuses.Completed, StringComparison.Ordinal) || attempt == maxAttempts)
                return lastResult;

            if (step.Retry.BackoffMs > 0)
                await Task.Delay(step.Retry.BackoffMs, ct);
        }

        return lastResult ?? CreateMetaStepFailedToolResult(toolName, toolArgsJson, "step_failed", $"Meta step '{step.Id}' failed before producing a result.");
    }

    private async Task<ToolExecutionResult> ExecuteMetaSkillExecStepWithPolicyAsync(
        SkillDefinition delegatedSkill,
        MetaSkillStepDefinition step,
        IReadOnlyList<string> arguments,
        string? workingDirectory,
        string? stdin,
        CancellationToken ct)
    {
        var maxAttempts = Math.Max(1, step.Retry.MaxAttempts);
        ToolExecutionResult? lastResult = null;

        for (var attempt = 1; attempt <= maxAttempts; attempt++)
        {
            using var timeoutCts = CreateMetaStepTimeout(step, ct);
            var effectiveCt = timeoutCts?.Token ?? ct;
            try
            {
                lastResult = await _toolExecutor.ExecuteSkillEntrypointAsync(
                    delegatedSkill,
                    step.SkillExecEntrypoint!,
                    arguments,
                    workingDirectory,
                    step.SkillExecParseMode ?? "text",
                    stdin,
                    effectiveCt);
            }
            catch (OperationCanceledException) when (!ct.IsCancellationRequested)
            {
                lastResult = CreateMetaStepFailedToolResult(
                    "skill_exec",
                    "{}",
                    "step_timeout",
                    $"Meta step '{step.Id}' timed out after {step.TimeoutSeconds} second(s).");
            }

            if (lastResult is not null &&
                (string.Equals(lastResult.ResultStatus, ToolResultStatuses.Completed, StringComparison.Ordinal) || attempt == maxAttempts))
            {
                return lastResult;
            }

            if (step.Retry.BackoffMs > 0)
                await Task.Delay(step.Retry.BackoffMs, ct);
        }

        return lastResult ?? CreateMetaStepFailedToolResult("skill_exec", "{}", "step_failed", $"Meta step '{step.Id}' failed before producing a result.");
    }

    private static async Task<MetaLlmStepExecutionResult> ExecuteMetaLlmStepWithPolicyAsync(
        MetaSkillStepDefinition step,
        Func<CancellationToken, Task<LlmExecutionResult>> executeAsync,
        CancellationToken ct)
    {
        var maxAttempts = Math.Max(1, step.Retry.MaxAttempts);
        string? lastFailureCode = null;
        string? lastFailureMessage = null;

        for (var attempt = 1; attempt <= maxAttempts; attempt++)
        {
            using var timeoutCts = CreateMetaStepTimeout(step, ct);
            var effectiveCt = timeoutCts?.Token ?? ct;
            try
            {
                return MetaLlmStepExecutionResult.Succeeded(await executeAsync(effectiveCt));
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                throw;
            }
            catch (OperationCanceledException)
            {
                lastFailureCode = "step_timeout";
                lastFailureMessage = $"Meta step '{step.Id}' timed out after {step.TimeoutSeconds} second(s).";
            }
            catch (Exception ex)
            {
                lastFailureCode = "llm_failed";
                lastFailureMessage = $"Meta step '{step.Id}' failed: {ex.Message}";
            }

            if (attempt == maxAttempts)
                return MetaLlmStepExecutionResult.Failed(lastFailureCode ?? "llm_failed", lastFailureMessage ?? $"Meta step '{step.Id}' failed before producing a response.");

            if (attempt < maxAttempts && step.Retry.BackoffMs > 0)
                await Task.Delay(step.Retry.BackoffMs, ct);
        }

        return MetaLlmStepExecutionResult.Failed("llm_failed", $"Meta step '{step.Id}' failed before producing a response.");
    }

    private static CancellationTokenSource? CreateMetaStepTimeout(MetaSkillStepDefinition step, CancellationToken ct)
    {
        if (step.TimeoutSeconds is not > 0)
            return null;

        var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeoutCts.CancelAfter(TimeSpan.FromSeconds(step.TimeoutSeconds.Value));
        return timeoutCts;
    }

    private static ToolExecutionResult CreateMetaStepFailedToolResult(
        string toolName,
        string arguments,
        string failureCode,
        string failureMessage)
    {
        return new ToolExecutionResult
        {
            Invocation = new ToolInvocation
            {
                ToolName = toolName,
                Arguments = arguments,
                Result = failureMessage,
                ResultStatus = ToolResultStatuses.Failed,
                FailureCode = failureCode,
                FailureMessage = failureMessage
            },
            ResultText = failureMessage,
            ResultStatus = ToolResultStatuses.Failed,
            FailureCode = failureCode,
            FailureMessage = failureMessage
        };
    }

    private static void CompleteMetaStepOutput(
        MetaSkillStepDefinition step,
        string output,
        HashSet<string> pending,
        Dictionary<string, string> outputs,
        Dictionary<string, string> failureAliases)
    {
        outputs[step.Id] = output;
        if (failureAliases.TryGetValue(step.Id, out var primaryStepId))
            outputs[primaryStepId] = output;

        pending.Remove(step.Id);
    }

    private static bool TryActivateFailureBranch(
        MetaSkillStepDefinition step,
        IReadOnlyDictionary<string, MetaSkillStepDefinition> stepById,
        HashSet<string> pending,
        HashSet<string> blocked,
        Dictionary<string, string> failureAliases)
    {
        if (string.IsNullOrWhiteSpace(step.OnFailure))
            return false;

        var fallbackStepId = step.OnFailure.Trim();
        if (!stepById.ContainsKey(fallbackStepId))
            return false;

        pending.Remove(step.Id);
        blocked.Remove(fallbackStepId);
        pending.Add(fallbackStepId);
        failureAliases[fallbackStepId] = step.Id;
        return true;
    }

    private static bool TryValidateMetaStepOutput(
        MetaSkillStepDefinition step,
        string output,
        out string? failureCode)
    {
        failureCode = null;
        if (step.OutputChoices.Count > 0)
        {
            var candidate = output.Trim();
            if (!step.OutputChoices.Contains(candidate, StringComparer.OrdinalIgnoreCase))
            {
                failureCode = "invalid_output_choice";
                return false;
            }
        }

        var contract = step.OutputContract;
        if (contract is null || string.IsNullOrWhiteSpace(contract.Format) || contract.Format.Equals("text", StringComparison.OrdinalIgnoreCase))
            return true;

        if (!contract.Format.Equals("json", StringComparison.OrdinalIgnoreCase))
        {
            failureCode = "output_contract_failed";
            return false;
        }

        var sanitized = SanitizeJsonOutput(output);
        try
        {
            using var doc = JsonDocument.Parse(sanitized);
            if (doc.RootElement.ValueKind != JsonValueKind.Object)
            {
                failureCode = "output_contract_failed";
                return false;
            }

            foreach (var requiredProperty in contract.RequiredProperties)
            {
                if (string.IsNullOrWhiteSpace(requiredProperty))
                    continue;

                if (!doc.RootElement.TryGetProperty(requiredProperty, out _))
                {
                    failureCode = "output_contract_failed";
                    return false;
                }
            }

            return true;
        }
        catch (JsonException)
        {
            failureCode = "output_contract_failed";
            return false;
        }
    }

    private static string SanitizeJsonOutput(string output)
    {
        var trimmed = output.Trim();
        if (trimmed.Length == 0)
            return trimmed;

        // 1. Strip ```json / ``` fences
        if (trimmed.StartsWith("```", StringComparison.Ordinal))
        {
            var fenceEnd = trimmed.IndexOf('\n');
            if (fenceEnd >= 0)
            {
                var contentStart = fenceEnd + 1;
                var closingFence = trimmed.LastIndexOf("```", StringComparison.Ordinal);
                if (closingFence > contentStart)
                    trimmed = trimmed[contentStart..closingFence].Trim();
            }
        }

        // 2. Extract first { ... } if the output is not already pure JSON
        if (trimmed.Length > 0 && trimmed[0] != '{')
        {
            var openBrace = trimmed.IndexOf('{');
            if (openBrace >= 0)
            {
                var closeBrace = trimmed.LastIndexOf('}');
                if (closeBrace > openBrace)
                    trimmed = trimmed[openBrace..(closeBrace + 1)].Trim();
            }
        }

        return trimmed;
    }

    private readonly record struct MetaLlmStepExecutionResult(LlmExecutionResult? ExecutionResult, string? FailureCode, string? FailureMessage)
    {
        public bool Completed => ExecutionResult is not null;

        public static MetaLlmStepExecutionResult Succeeded(LlmExecutionResult executionResult)
            => new(executionResult, null, null);

        public static MetaLlmStepExecutionResult Failed(string failureCode, string failureMessage)
            => new(null, failureCode, failureMessage);
    }

    private readonly record struct MetaParallelToolStepCandidate(MetaSkillStepDefinition Step, string ToolName, string ToolArgsJson);

    private readonly record struct MetaParallelToolStepExecution(MetaSkillStepDefinition Step, ToolExecutionResult ToolResult, double DurationMs);

    private static bool IsClarifyInputTimedOut(Session session, string skillName, MetaSkillStepDefinition step)
    {
        if (step.Clarify?.TimeoutSeconds is not > 0)
            return false;

        var checkpoint = session.MetaExecutionCheckpoint;
        if (checkpoint is null ||
            !string.Equals(checkpoint.SkillName, skillName, StringComparison.OrdinalIgnoreCase) ||
            !string.Equals(checkpoint.PendingStepId, step.Id, StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        var deadline = checkpoint.CreatedAtUtc + TimeSpan.FromSeconds(step.Clarify.TimeoutSeconds.Value);
        return DateTimeOffset.UtcNow >= deadline;
    }

    private static Dictionary<string, object?> DeserializeStepArgs(string? withJson)
    {
        if (string.IsNullOrWhiteSpace(withJson))
            return new Dictionary<string, object?>(StringComparer.OrdinalIgnoreCase);

        try
        {
            var parsed = JsonSerializer.Deserialize(withJson, CoreJsonContext.Default.DictionaryStringObject);
            if (parsed is not null)
                return new Dictionary<string, object?>(parsed, StringComparer.OrdinalIgnoreCase);
        }
        catch (JsonException)
        {
        }

        return new Dictionary<string, object?>(StringComparer.OrdinalIgnoreCase);
    }

    private static string? GetOptionalString(Dictionary<string, object?> args, string key)
    {
        if (!args.TryGetValue(key, out var value) || value is null)
            return null;

        if (value is string text)
            return text;

        if (value is JsonElement json && json.ValueKind == JsonValueKind.String)
            return json.GetString();

        return null;
    }

    private static bool? GetOptionalBoolean(Dictionary<string, object?> args, string key)
    {
        if (!args.TryGetValue(key, out var value) || value is null)
            return null;

        if (value is bool b)
            return b;

        if (value is string s && bool.TryParse(s, out var parsedString))
            return parsedString;

        if (value is JsonElement json)
        {
            if (json.ValueKind == JsonValueKind.True)
                return true;
            if (json.ValueKind == JsonValueKind.False)
                return false;
            if (json.ValueKind == JsonValueKind.String && bool.TryParse(json.GetString(), out var parsedJsonString))
                return parsedJsonString;
        }

        return null;
    }

    private static int? GetOptionalInt32(Dictionary<string, object?> args, string key)
    {
        if (!args.TryGetValue(key, out var value) || value is null)
            return null;

        if (value is int i)
            return i;
        if (value is long l && l is >= int.MinValue and <= int.MaxValue)
            return (int)l;
        if (value is JsonElement json)
        {
            if (json.ValueKind == JsonValueKind.Number && json.TryGetInt32(out var parsed))
                return parsed;
            if (json.ValueKind == JsonValueKind.String && int.TryParse(json.GetString(), out parsed))
                return parsed;
        }

        return null;
    }

    private static float? GetOptionalSingle(Dictionary<string, object?> args, string key)
    {
        if (!args.TryGetValue(key, out var value) || value is null)
            return null;

        if (value is float f)
            return f;
        if (value is double d)
            return (float)d;
        if (value is decimal m)
            return (float)m;
        if (value is JsonElement json)
        {
            if (json.ValueKind == JsonValueKind.Number && json.TryGetDouble(out var parsed))
                return (float)parsed;
            if (json.ValueKind == JsonValueKind.String && float.TryParse(json.GetString(), out var parsedFloat))
                return parsedFloat;
        }

        return null;
    }

    private static bool TryGetStringArray(Dictionary<string, object?> args, string key, out List<string> values)
    {
        values = [];
        if (!args.TryGetValue(key, out var value) || value is null)
            return false;

        if (value is JsonElement json && json.ValueKind == JsonValueKind.Array)
        {
            foreach (var item in json.EnumerateArray())
            {
                if (item.ValueKind != JsonValueKind.String)
                    return false;

                var text = item.GetString();
                if (string.IsNullOrWhiteSpace(text))
                    return false;

                values.Add(text.Trim());
            }

            return true;
        }

        return false;
    }

    private static bool TryGetRouteMap(Dictionary<string, object?> args, out Dictionary<string, List<string>> routeMap)
    {
        routeMap = new Dictionary<string, List<string>>(StringComparer.OrdinalIgnoreCase);
        if (!args.TryGetValue("route", out var routeValue) || routeValue is not JsonElement routeJson || routeJson.ValueKind != JsonValueKind.Object)
            return false;

        foreach (var property in routeJson.EnumerateObject())
        {
            if (property.Value.ValueKind == JsonValueKind.String)
            {
                var target = property.Value.GetString();
                if (!string.IsNullOrWhiteSpace(target))
                    routeMap[property.Name] = [target.Trim()];
            }
            else if (property.Value.ValueKind == JsonValueKind.Array)
            {
                var targets = new List<string>();
                foreach (var item in property.Value.EnumerateArray())
                {
                    if (item.ValueKind != JsonValueKind.String)
                        continue;

                    var target = item.GetString();
                    if (!string.IsNullOrWhiteSpace(target))
                        targets.Add(target.Trim());
                }

                if (targets.Count > 0)
                    routeMap[property.Name] = targets;
            }
        }

        return routeMap.Count > 0;
    }

    private static string BuildClassificationPrompt(string input, IReadOnlyList<string> options)
    {
        var optionsList = string.Join(", ", options);
        return $"Classify the following text into exactly one label from [{optionsList}]. Return only the label.\n\nText:\n{input}";
    }

    private static bool TryResolveClassificationLabel(string raw, IReadOnlyList<string> options, out string? selected)
    {
        selected = null;
        if (string.IsNullOrWhiteSpace(raw))
            return false;

        var candidate = raw.Trim().Trim('"', '\'', '`');
        selected = options.FirstOrDefault(option => string.Equals(option, candidate, StringComparison.OrdinalIgnoreCase));
        if (!string.IsNullOrWhiteSpace(selected))
            return true;

        foreach (var option in options)
        {
            if (candidate.Contains(option, StringComparison.OrdinalIgnoreCase))
            {
                selected = option;
                return true;
            }
        }

        return false;
    }

    private static string NormalizeMetaStepKind(string kind)
        => kind.Trim().ToLowerInvariant();

    private static Dictionary<string, List<string>> BuildDependentsIndex(IReadOnlyList<MetaSkillStepDefinition> steps)
    {
        var dependents = new Dictionary<string, List<string>>(StringComparer.OrdinalIgnoreCase);
        foreach (var step in steps)
        {
            dependents.TryAdd(step.Id, []);
        }

        foreach (var step in steps)
        {
            foreach (var dependency in step.DependsOn)
            {
                if (!dependents.TryGetValue(dependency, out var children))
                    continue;

                children.Add(step.Id);
            }
        }

        return dependents;
    }

    private static void BlockStepAndDependents(
        string stepId,
        HashSet<string> blocked,
        HashSet<string> pending,
        IReadOnlyDictionary<string, List<string>> dependentsByStep)
    {
        var stack = new Stack<string>();
        stack.Push(stepId);

        while (stack.Count > 0)
        {
            var current = stack.Pop();
            if (!blocked.Add(current))
                continue;

            pending.Remove(current);

            if (!dependentsByStep.TryGetValue(current, out var dependents))
                continue;

            foreach (var dependent in dependents)
                stack.Push(dependent);
        }
    }

    private static void ApplyClassificationRouting(
        string selectedLabel,
        IReadOnlyDictionary<string, List<string>> routeMap,
        HashSet<string> blocked,
        HashSet<string> pending,
        IReadOnlyDictionary<string, List<string>> dependentsByStep,
        IReadOnlyDictionary<string, MetaSkillStepDefinition> stepById)
    {
        var selectedTargets = routeMap.TryGetValue(selectedLabel, out var matchedTargets)
            ? matchedTargets
            : [];

        foreach (var (label, targets) in routeMap)
        {
            if (string.Equals(label, selectedLabel, StringComparison.OrdinalIgnoreCase))
                continue;

            foreach (var target in targets)
            {
                if (!stepById.ContainsKey(target))
                    continue;

                BlockStepAndDependents(target, blocked, pending, dependentsByStep);
            }
        }

        foreach (var target in selectedTargets)
            blocked.Remove(target);
    }

    private static bool TryValidateMetaPlan(IReadOnlyList<MetaSkillStepDefinition> steps, IReadOnlyList<SkillDefinition> loadedSkills, out string? error)
    {
        error = null;
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var step in steps)
        {
            if (!seen.Add(step.Id))
            {
                error = $"Meta execution graph contains duplicate step id '{step.Id}'.";
                return false;
            }
        }

        foreach (var step in steps)
        {
            if (!string.IsNullOrWhiteSpace(step.Skill))
            {
                var delegatedSkill = loadedSkills.FirstOrDefault(skill =>
                    string.Equals(skill.Name, step.Skill, StringComparison.OrdinalIgnoreCase));

                if (delegatedSkill is not null && delegatedSkill.Kind == SkillKind.Meta)
                {
                    error = $"Meta execution graph cannot compose meta skill '{delegatedSkill.Name}' from step '{step.Id}'.";
                    return false;
                }
            }

            foreach (var dependency in step.DependsOn)
            {
                if (!seen.Contains(dependency))
                {
                    error = $"Meta execution graph references missing dependency '{dependency}' from step '{step.Id}'.";
                    return false;
                }

                if (string.Equals(step.Id, dependency, StringComparison.OrdinalIgnoreCase))
                {
                    error = $"Meta execution graph contains self-dependency on step '{step.Id}'.";
                    return false;
                }
            }
        }

        var stepById = steps.ToDictionary(static step => step.Id, StringComparer.OrdinalIgnoreCase);
        var designatedFallbacks = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        var fallbackTargets = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var step in steps)
        {
            if (string.IsNullOrWhiteSpace(step.OnFailure))
                continue;

            if (string.Equals(step.Id, step.OnFailure, StringComparison.OrdinalIgnoreCase) ||
                !stepById.TryGetValue(step.OnFailure, out var substitute))
            {
                error = $"Meta execution graph references invalid on_failure target '{step.OnFailure}' from step '{step.Id}'.";
                return false;
            }

            if (!string.IsNullOrWhiteSpace(substitute.OnFailure) || substitute.DependsOn.Count > 0)
            {
                error = $"Meta execution graph has invalid on_failure substitute '{substitute.Id}' from step '{step.Id}'.";
                return false;
            }

            if (designatedFallbacks.TryGetValue(step.OnFailure, out var priorStep))
            {
                error = $"Meta execution graph fallback step '{step.OnFailure}' is shared by steps '{priorStep}' and '{step.Id}'.";
                return false;
            }

            designatedFallbacks[step.OnFailure] = step.Id;
            fallbackTargets.Add(step.OnFailure);
        }

        foreach (var step in steps)
        {
            foreach (var dependency in step.DependsOn)
            {
                if (!fallbackTargets.Contains(dependency))
                    continue;

                error = $"Meta execution graph step '{step.Id}' depends directly on fallback-only step '{dependency}'.";
                return false;
            }
        }

        if (HasDependencyCycle(steps))
        {
            error = "Meta execution graph contains a dependency cycle.";
            return false;
        }

        return true;
    }

    private static bool HasDependencyCycle(IReadOnlyList<MetaSkillStepDefinition> steps)
    {
        var state = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        var stepById = steps.ToDictionary(static step => step.Id, StringComparer.OrdinalIgnoreCase);

        bool Dfs(string stepId)
        {
            if (state.TryGetValue(stepId, out var currentState))
                return currentState == 1;

            state[stepId] = 1;
            var step = stepById[stepId];
            foreach (var dependency in step.DependsOn)
            {
                if (Dfs(dependency))
                    return true;
            }

            state[stepId] = 2;
            return false;
        }

        foreach (var step in steps)
        {
            if (state.TryGetValue(step.Id, out var currentState) && currentState == 2)
                continue;

            if (Dfs(step.Id))
                return true;
        }

        return false;
    }

    private static string ResolveMetaTemplate(string template, string? rootInput, IReadOnlyDictionary<string, string> outputs)
    {
        return Regex.Replace(template, "{{\\s*(?<token>[^{}]+?)\\s*}}", match =>
        {
            var token = match.Groups["token"].Value;
            if (token.Equals("input", StringComparison.OrdinalIgnoreCase) ||
                token.Equals("inputs.user_message", StringComparison.OrdinalIgnoreCase))
            {
                return rootInput ?? string.Empty;
            }

            const string outputPrefix = "outputs.";
            if (token.StartsWith(outputPrefix, StringComparison.OrdinalIgnoreCase))
            {
                var key = token[outputPrefix.Length..];
                if (!string.IsNullOrWhiteSpace(key) && outputs.TryGetValue(key, out var value))
                    return value;
            }

            return string.Empty;
        });
    }

    private static string RewriteMetaTemplateJson(string withJson, string? rootInput, IReadOnlyDictionary<string, string> outputs)
    {
        var resolved = ResolveMetaTemplate(withJson, rootInput, outputs);
        return IsValidJson(resolved) ? resolved : withJson;
    }

    private static bool IsValidJson(string value)
    {
        try
        {
            using var _ = JsonDocument.Parse(value);
            return true;
        }
        catch (JsonException)
        {
            return false;
        }
    }

    private static string? CombineSystemPromptSuffixes(string? first, string? second)
    {
        if (string.IsNullOrWhiteSpace(first))
            return second;
        if (string.IsNullOrWhiteSpace(second))
            return first;
        return first.Trim() + "\n" + second.Trim();
    }

    private readonly record struct TurnRoutingSnapshot(
        string? ModelProfileId,
        string[] PreferredModelTags,
        string[] FallbackModelProfileIds,
        string? SystemPromptOverride,
        string[] RouteAllowedTools,
        bool RouteToolsDisabled,
        string? RouteModelTier,
        string? RouteReason,
        string? ReasoningEffort,
        string ResponseMode);

    private sealed class TurnRoutingRestoreScope(Session session, TurnRoutingSnapshot snapshot) : IDisposable
    {
        public void Dispose()
        {
            session.ModelProfileId = snapshot.ModelProfileId;
            session.PreferredModelTags = snapshot.PreferredModelTags;
            session.FallbackModelProfileIds = snapshot.FallbackModelProfileIds;
            session.SystemPromptOverride = snapshot.SystemPromptOverride;
            session.RouteAllowedTools = snapshot.RouteAllowedTools;
            session.RouteToolsDisabled = snapshot.RouteToolsDisabled;
            session.RouteReason = snapshot.RouteReason;
            session.ReasoningEffort = snapshot.ReasoningEffort;
            session.ResponseMode = snapshot.ResponseMode;
        }
    }

    private static IList<AIContent> BuildTurnContents(string content)
    {
        var (markers, remainingText) = MediaMarkerProtocol.Extract(content);
        var contents = new List<AIContent>();
        if (!string.IsNullOrWhiteSpace(remainingText))
            contents.Add(new TextContent(remainingText));

        foreach (var marker in markers)
        {
            var mediaType = marker.Kind switch
            {
                MediaMarkerKind.ImageUrl or MediaMarkerKind.ImagePath or MediaMarkerKind.TelegramImageFileId => "image/*",
                MediaMarkerKind.AudioUrl or MediaMarkerKind.TelegramAudioFileId => "audio/*",
                MediaMarkerKind.VideoUrl or MediaMarkerKind.TelegramVideoFileId => "video/*",
                MediaMarkerKind.DocumentUrl or MediaMarkerKind.FileUrl or MediaMarkerKind.FilePath or MediaMarkerKind.TelegramDocumentFileId => "application/octet-stream",
                _ => "application/octet-stream"
            };

            switch (marker.Kind)
            {
                case MediaMarkerKind.ImagePath:
                case MediaMarkerKind.FilePath:
                    contents.Add(new UriContent(new Uri(Path.GetFullPath(marker.Value)), mediaType));
                    break;
                default:
                    if (Uri.TryCreate(marker.Value, UriKind.Absolute, out var uri))
                        contents.Add(new UriContent(uri, mediaType));
                    else if (Uri.TryCreate(marker.Value, UriKind.Relative, out _))
                        contents.Add(new TextContent(marker.Value));
                    break;
            }
        }

        if (contents.Count == 0)
            contents.Add(new TextContent(content));

        return contents;
    }

    private void ApplySkills(IReadOnlyList<SkillDefinition> skills)
    {
        lock (_skillGate)
        {
            var promptVisibleSkills = _metaSkillsEnabled
                ? skills
                : skills.Where(static skill => skill.Kind != SkillKind.Meta).ToArray();

            // Progressive disclosure: only the metadata index lives in the system prompt.
            // The full SKILL.md body for any single skill is fetched on demand via the
            // `load_skill` tool, which reads from LoadedSkills (this same snapshot).
            var skillSection = SkillPromptBuilder.BuildIndex(promptVisibleSkills, _skillsConfig?.InstructionPrompt);
            var basePrompt = AgentSystemPromptBuilder.BuildBaseSystemPrompt(_requireToolApproval);
            _skillPromptLength = skillSection.Length;
            _systemPrompt = string.IsNullOrEmpty(skillSection) ? basePrompt : basePrompt + "\n" + skillSection;
            _loadedSkills = skills;
            _loadedSkillNames = skills
                .Select(skill => skill.Name)
                .OrderBy(name => name, StringComparer.OrdinalIgnoreCase)
                .ToArray();
        }
    }

    internal void TrimHistory(Session session)
    {
        if (session.History.Count <= _maxHistoryTurns)
            return;

        var toRemove = session.History.Count - _maxHistoryTurns;
        session.History.RemoveRange(0, toRemove);
    }

    private static string Truncate(string text, int maxLength) =>
        text.Length <= maxLength ? text : text[..maxLength] + "…";

    private static HashSet<string> NormalizeApprovalRequiredTools(string[]? configuredTools)
    {
        var normalized = new HashSet<string>(StringComparer.Ordinal);
        var tools = configuredTools is { Length: > 0 } ? configuredTools : ["shell", "write_file"];

        foreach (var toolName in tools)
        {
            if (string.IsNullOrWhiteSpace(toolName))
                continue;

            normalized.Add(NormalizeApprovalToolName(toolName.Trim()));
        }

        return normalized;
    }

    private static string NormalizeApprovalToolName(string toolName) =>
        string.Equals(toolName, "file_write", StringComparison.Ordinal)
            ? "write_file"
            : toolName;

    private void LogTurnComplete(TurnContext turnCtx) => _accounting.LogTurnComplete(turnCtx);

    private bool TryRejectContractBudget(Session session, out string message)
        => _accounting.TryRejectContractBudget(session, out message);

    private void AppendContractSnapshot(Session session, string status)
        => _accounting.AppendContractSnapshot(session, status);
}

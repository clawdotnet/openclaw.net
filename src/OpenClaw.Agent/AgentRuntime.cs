using System.Diagnostics;
using System.Runtime.CompilerServices;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging;
using OpenClaw.Core.Abstractions;
using OpenClaw.Core.Models;
using OpenClaw.Core.Observability;
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
public sealed class AgentRuntime
{
    private readonly IChatClient _chatClient;
    private readonly IReadOnlyList<ITool> _tools;
    private readonly Dictionary<string, ITool> _toolsByName;
    private readonly List<AITool> _cachedToolDeclarations;
    private readonly IMemoryStore _memory;
    private readonly ILogger? _logger;
    private readonly string _systemPrompt;
    private readonly int _maxTokens;
    private readonly int _maxIterations;
    private readonly float _temperature;
    private readonly int _maxHistoryTurns;
    private readonly int _llmTimeoutSeconds;
    private readonly int _retryCount;
    private readonly int _toolTimeoutSeconds;
    private readonly bool _parallelToolExecution;
    private readonly bool _enableCompaction;
    private readonly int _compactionThreshold;
    private readonly int _compactionKeepRecent;
    private readonly bool _requireToolApproval;
    private readonly HashSet<string> _approvalRequiredTools;
    private readonly IReadOnlyList<IToolHook> _hooks;
    private readonly CircuitBreaker _circuitBreaker;
    private readonly RuntimeMetrics? _metrics;
    private readonly long _sessionTokenBudget;
    private readonly LlmProviderConfig _config;

    public AgentRuntime(
        IChatClient chatClient,
        IReadOnlyList<ITool> tools,
        IMemoryStore memory,
        LlmProviderConfig config,
        int maxHistoryTurns,
        IReadOnlyList<SkillDefinition>? skills = null,
        ILogger? logger = null,
        int toolTimeoutSeconds = 30,
        RuntimeMetrics? metrics = null,
        bool parallelToolExecution = true,
        bool enableCompaction = false,
        int compactionThreshold = 40,
        int compactionKeepRecent = 10,
        bool requireToolApproval = false,
        string[]? approvalRequiredTools = null,
        int maxIterations = 10,
        IReadOnlyList<IToolHook>? hooks = null,
        long sessionTokenBudget = 0)
    {
        _chatClient = chatClient;
        _tools = tools;
        _toolsByName = tools.ToDictionary(t => t.Name, StringComparer.Ordinal);
        _memory = memory;
        _logger = logger;
        _config = config;
        _maxTokens = config.MaxTokens;
        _maxIterations = Math.Max(1, maxIterations);
        _temperature = config.Temperature;
        _maxHistoryTurns = Math.Max(1, maxHistoryTurns);
        _llmTimeoutSeconds = config.TimeoutSeconds;
        _retryCount = config.RetryCount;
        _toolTimeoutSeconds = toolTimeoutSeconds;
        _parallelToolExecution = parallelToolExecution;
        _enableCompaction = enableCompaction;
        _compactionThreshold = Math.Max(4, compactionThreshold);
        _compactionKeepRecent = Math.Max(2, compactionKeepRecent);
        _requireToolApproval = requireToolApproval;
        _approvalRequiredTools = NormalizeApprovalRequiredTools(approvalRequiredTools);
        _hooks = hooks ?? [];
        _metrics = metrics;
        _systemPrompt = BuildSystemPrompt(skills ?? [], requireToolApproval);
        _circuitBreaker = new CircuitBreaker(
            config.CircuitBreakerThreshold,
            TimeSpan.FromSeconds(config.CircuitBreakerCooldownSeconds),
            logger);

        // Cache tool declarations — ParameterSchema is static, no need to re-parse per iteration
        _cachedToolDeclarations = _tools.Select(CacheDeclarationTool).Cast<AITool>().ToList();
        _sessionTokenBudget = sessionTokenBudget;
    }

    /// <summary>
    /// Exposes the circuit breaker state for health/metrics endpoints.
    /// </summary>
    public CircuitState CircuitBreakerState => _circuitBreaker.State;

    /// <summary>
    /// Run the agent loop for a single user turn. Supports multi-step tool use,
    /// parallel tool execution, hooks, and optional tool approval.
    /// </summary>
    public async Task<string> RunAsync(
        Session session, string userMessage, CancellationToken ct,
        ToolApprovalCallback? approvalCallback = null,
        JsonElement? responseSchema = null)
    {
        using var activity = Telemetry.ActivitySource.StartActivity("Agent.RunAsync");
        activity?.SetTag("session.id", session.Id);
        activity?.SetTag("channel.id", session.ChannelId);

        var turnCtx = new TurnContext
        {
            SessionId = session.Id,
            ChannelId = session.ChannelId
        };

        _metrics?.IncrementRequests();
        _logger?.LogInformation("[{CorrelationId}] Turn start session={SessionId} channel={ChannelId}",
            turnCtx.CorrelationId, session.Id, session.ChannelId);

        // Record user turn
        session.History.Add(new ChatTurn { Role = "user", Content = userMessage });

        // Compaction or simple trim
        if (_enableCompaction)
            await CompactHistoryAsync(session, ct);
        else
            TrimHistory(session);

        // Build conversation for LLM
        var messages = BuildMessages(session);

        // Build tool definitions for the LLM (use pre-cached declarations)
        var chatOptions = new ChatOptions
        {
            ModelId = session.ModelOverride ?? _config.Model,
            MaxOutputTokens = _maxTokens,
            Temperature = _temperature,
            Tools = _cachedToolDeclarations,
            ResponseFormat = responseSchema.HasValue
                ? ChatResponseFormat.ForJsonSchema(responseSchema.Value, "response")
                : null
        };

        for (var i = 0; i < _maxIterations; i++)
        {
            // Mid-turn budget check: stop if token budget is exceeded
            if (_sessionTokenBudget > 0 && (session.TotalInputTokens + session.TotalOutputTokens) >= _sessionTokenBudget)
            {
                _logger?.LogInformation("[{CorrelationId}] Session token budget exceeded mid-turn ({Used}/{Budget})",
                    turnCtx.CorrelationId, session.TotalInputTokens + session.TotalOutputTokens, _sessionTokenBudget);
                LogTurnComplete(turnCtx);
                return "You've reached the token limit for this session. Please start a new conversation.";
            }

            ChatResponse? response = null;
            var llmSw = Stopwatch.StartNew();
            try
            {
                response = await CallLlmWithResilienceAsync(messages, chatOptions, turnCtx, ct);
            }
            catch (CircuitOpenException coe)
            {
                _logger?.LogWarning("[{CorrelationId}] Circuit breaker open — retry after {RetryAfter}s",
                    turnCtx.CorrelationId, coe.RetryAfter.TotalSeconds);
                LogTurnComplete(turnCtx);
                return coe.Message;
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception ex)
            {
                _metrics?.IncrementLlmErrors();
                
                // Attempt Failover logic if configured
                var currentModel = chatOptions.ModelId ?? _config.Model;
                var fallbacksTried = false;

                if (_config.FallbackModels is not null && _config.FallbackModels.Length > 0)
                {
                    foreach (var fallback in _config.FallbackModels)
                    {
                        if (string.Equals(fallback, currentModel, StringComparison.OrdinalIgnoreCase))
                            continue;
                            
                        _logger?.LogWarning("[{CorrelationId}] Primary model '{Model}' failed. Retrying with fallback '{Fallback}'", turnCtx.CorrelationId, currentModel, fallback);
                        
                        try 
                        {
                            chatOptions.ModelId = fallback;
                            response = await CallLlmWithResilienceAsync(messages, chatOptions, turnCtx, ct);
                            fallbacksTried = true;
                            // Reset modelid if successful
                            chatOptions.ModelId = currentModel; 
                            break; 
                        }
                        catch (Exception innerEx)
                        {
                            _logger?.LogWarning(innerEx, "[{CorrelationId}] Fallback model '{Fallback}' failed.", turnCtx.CorrelationId, fallback);
                            // continue trying next
                        }
                    }
                }
                
                if (!fallbacksTried)
                {
                    _logger?.LogError(ex, "[{CorrelationId}] LLM call failed after all retries and fallbacks", turnCtx.CorrelationId);
                    LogTurnComplete(turnCtx);
                    return "Sorry, I'm having trouble reaching my AI provider right now. Please try again shortly.";
                }
            }
            llmSw.Stop();

            if (response is null)
            {
                 LogTurnComplete(turnCtx);
                 return "Sorry, I'm having trouble reaching my AI provider right now. Please try again shortly.";
            }

            // Extract token usage from response
            var inputTokens = response.Usage?.InputTokenCount ?? 0;
            var outputTokens = response.Usage?.OutputTokenCount ?? 0;
            turnCtx.RecordLlmCall(llmSw.Elapsed, inputTokens, outputTokens);
            _metrics?.IncrementLlmCalls();
            _metrics?.AddInputTokens(inputTokens);
            _metrics?.AddOutputTokens(outputTokens);

            // Track token usage on the session
            session.TotalInputTokens += inputTokens;
            session.TotalOutputTokens += outputTokens;

            // Check for tool calls
            var toolCalls = response.Messages
                .SelectMany(m => m.Contents.OfType<FunctionCallContent>())
                .ToList();

            if (toolCalls.Count == 0)
            {
                // Final text response
                var text = response.Text ?? "";
                session.History.Add(new ChatTurn { Role = "assistant", Content = text });
                LogTurnComplete(turnCtx);
                return text;
            }

            // Execute tool calls (parallel or sequential based on config)
            var (invocations, toolResults) = await ExecuteToolCallsAsync(
                toolCalls, turnCtx, approvalCallback, ct);

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
        }

        LogTurnComplete(turnCtx);
        return "I've reached the maximum number of tool iterations. Please try a simpler request.";
    }

    /// <summary>
    /// Run the agent loop with streaming. Yields incremental events (text deltas, tool status)
    /// for real-time delivery to WebSocket clients.
    /// </summary>
    public async IAsyncEnumerable<AgentStreamEvent> RunStreamingAsync(
        Session session, string userMessage,
        [EnumeratorCancellation] CancellationToken ct,
        ToolApprovalCallback? approvalCallback = null)
    {
        using var activity = Telemetry.ActivitySource.StartActivity("Agent.RunStreamingAsync");
        activity?.SetTag("session.id", session.Id);
        activity?.SetTag("channel.id", session.ChannelId);

        var turnCtx = new TurnContext
        {
            SessionId = session.Id,
            ChannelId = session.ChannelId
        };

        _metrics?.IncrementRequests();
        _logger?.LogInformation("[{CorrelationId}] Streaming turn start session={SessionId} channel={ChannelId}",
            turnCtx.CorrelationId, session.Id, session.ChannelId);

        if (_requireToolApproval && approvalCallback is null)
        {
            _logger?.LogWarning("[{CorrelationId}] Streaming session has RequireToolApproval=true but no approval callback — protected tools will be auto-denied",
                turnCtx.CorrelationId);
        }

        session.History.Add(new ChatTurn { Role = "user", Content = userMessage });

        if (_enableCompaction)
            await CompactHistoryAsync(session, ct);
        else
            TrimHistory(session);

        var messages = BuildMessages(session);
        var chatOptions = new ChatOptions
        {
            MaxOutputTokens = _maxTokens,
            Temperature = _temperature,
            Tools = _cachedToolDeclarations
        };

        for (var i = 0; i < _maxIterations; i++)
        {
            // Stream the LLM response, collecting chunks and tool calls.
            // We buffer events because C# doesn't allow yield in try/catch.
            var streamResult = await StreamLlmCollectAsync(messages, chatOptions, turnCtx, ct);

            // Yield buffered text deltas
            foreach (var delta in streamResult.TextDeltas)
                yield return AgentStreamEvent.TextDelta(delta);

            // If streaming failed, yield error and stop
            if (streamResult.Error is not null)
            {
                yield return AgentStreamEvent.ErrorOccurred(streamResult.Error);
                yield return AgentStreamEvent.Complete();
                LogTurnComplete(turnCtx);
                yield break;
            }

            session.TotalInputTokens += streamResult.InputTokens;
            session.TotalOutputTokens += streamResult.OutputTokens;

            var toolCalls = streamResult.ToolCalls;

            if (toolCalls.Count == 0)
            {
                // Final text response
                session.History.Add(new ChatTurn { Role = "assistant", Content = streamResult.FullText });
                yield return AgentStreamEvent.Complete();
                LogTurnComplete(turnCtx);
                yield break;
            }

            // Execute tool calls
            var (invocations, toolResults) = await ExecuteToolCallsAsync(
                toolCalls, turnCtx, approvalCallback, ct);

            foreach (var inv in invocations)
            {
                yield return AgentStreamEvent.ToolStarted(inv.ToolName);
                yield return AgentStreamEvent.ToolCompleted(inv.ToolName, inv.Result ?? "");
            }

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
        }

        yield return AgentStreamEvent.ErrorOccurred(
            "I've reached the maximum number of tool iterations. Please try a simpler request.");
        yield return AgentStreamEvent.Complete();
        LogTurnComplete(turnCtx);
    }

    /// <summary>
    /// Result of collecting a streaming LLM response.
    /// </summary>
    private sealed class StreamCollectResult
    {
        public List<string> TextDeltas { get; } = [];
        public string FullText => string.Concat(TextDeltas);
        public List<FunctionCallContent> ToolCalls { get; } = [];
        public int InputTokens { get; set; }
        public int OutputTokens { get; set; }
        public string? Error { get; set; }
    }

    /// <summary>
    /// Streams the LLM, buffers text deltas and collects tool calls.
    /// Error handling is done without yield so this can live in a try/catch.
    /// </summary>
    private async Task<StreamCollectResult> StreamLlmCollectAsync(
        List<ChatMessage> messages, ChatOptions options, TurnContext turnCtx, CancellationToken ct)
    {
        var result = new StreamCollectResult();
        var llmSw = Stopwatch.StartNew();

        // Create the timeout CTS here so it is properly disposed after enumeration completes.
        using var timeoutCts = _llmTimeoutSeconds > 0
            ? CancellationTokenSource.CreateLinkedTokenSource(ct)
            : null;
        timeoutCts?.CancelAfter(TimeSpan.FromSeconds(_llmTimeoutSeconds));
        var effectiveCt = timeoutCts?.Token ?? ct;

        IAsyncEnumerable<ChatResponseUpdate> stream;
        try
        {
            stream = StreamLlmAsync(messages, options, effectiveCt);
        }
        catch (CircuitOpenException coe)
        {
            result.Error = coe.Message;
            LogTurnComplete(turnCtx);
            return result;
        }

        try
        {
            await foreach (var update in stream.WithCancellation(effectiveCt))
            {
                if (!string.IsNullOrEmpty(update.Text))
                    result.TextDeltas.Add(update.Text);

                foreach (var content in update.Contents)
                {
                    if (content is FunctionCallContent fc)
                        result.ToolCalls.Add(fc);
                }
            }
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            _metrics?.IncrementLlmErrors();
            _logger?.LogError(ex, "[{CorrelationId}] Streaming LLM call failed", turnCtx.CorrelationId);
            result.Error = "Sorry, I'm having trouble reaching my AI provider right now. Please try again shortly.";
            LogTurnComplete(turnCtx);
            return result;
        }

        llmSw.Stop();
        result.InputTokens = EstimateInputTokens(messages);
        result.OutputTokens = EstimateTokenCount(result.FullText.Length);
        turnCtx.RecordLlmCall(llmSw.Elapsed, result.InputTokens, result.OutputTokens);
        _metrics?.IncrementLlmCalls();
        _metrics?.AddInputTokens(result.InputTokens);
        _metrics?.AddOutputTokens(result.OutputTokens);

        return result;
    }

    /// <summary>
    /// Executes tool calls either in parallel or sequentially, running hooks around each.
    /// </summary>
    private async Task<(List<ToolInvocation> Invocations, List<FunctionResultContent> Results)> ExecuteToolCallsAsync(
        List<FunctionCallContent> toolCalls,
        TurnContext turnCtx,
        ToolApprovalCallback? approvalCallback,
        CancellationToken ct,
        Action<string>? onToolStart = null,
        Action<string>? onToolComplete = null)
    {
        if (_parallelToolExecution && toolCalls.Count > 1)
        {
            return await ExecuteToolCallsParallelAsync(toolCalls, turnCtx, approvalCallback, ct);
        }

        return await ExecuteToolCallsSequentialAsync(toolCalls, turnCtx, approvalCallback, ct);
    }

    private async Task<(List<ToolInvocation>, List<FunctionResultContent>)> ExecuteToolCallsSequentialAsync(
        List<FunctionCallContent> toolCalls,
        TurnContext turnCtx,
        ToolApprovalCallback? approvalCallback,
        CancellationToken ct)
    {
        var invocations = new List<ToolInvocation>(toolCalls.Count);
        var toolResults = new List<FunctionResultContent>(toolCalls.Count);

        foreach (var call in toolCalls)
        {
            var (invocation, result) = await ExecuteSingleToolCallAsync(call, turnCtx, approvalCallback, ct);
            invocations.Add(invocation);
            toolResults.Add(result);
        }

        return (invocations, toolResults);
    }

    private async Task<(List<ToolInvocation>, List<FunctionResultContent>)> ExecuteToolCallsParallelAsync(
        List<FunctionCallContent> toolCalls,
        TurnContext turnCtx,
        ToolApprovalCallback? approvalCallback,
        CancellationToken ct)
    {
        var tasks = toolCalls
            .Select(call => ExecuteSingleToolCallAsync(call, turnCtx, approvalCallback, ct))
            .ToArray();

        var results = await Task.WhenAll(tasks);

        var invocations = new List<ToolInvocation>(results.Length);
        var toolResults = new List<FunctionResultContent>(results.Length);

        foreach (var (invocation, result) in results)
        {
            invocations.Add(invocation);
            toolResults.Add(result);
        }

        return (invocations, toolResults);
    }

    private async Task<(ToolInvocation, FunctionResultContent)> ExecuteSingleToolCallAsync(
        FunctionCallContent call,
        TurnContext turnCtx,
        ToolApprovalCallback? approvalCallback,
        CancellationToken ct)
    {
        using var activity = Telemetry.ActivitySource.StartActivity("Agent.ExecuteTool");
        activity?.SetTag("tool.name", call.Name);
        var tool = _toolsByName.GetValueOrDefault(call.Name);
        if (tool is null)
        {
            var inv = new ToolInvocation
            {
                ToolName = call.Name,
                Arguments = "{}",
                Result = "Error: Unknown tool",
                Duration = TimeSpan.Zero
            };
            return (inv, new FunctionResultContent(call.CallId, "Error: Unknown tool"));
        }

        var argsJson = call.Arguments is not null
            ? JsonSerializer.Serialize(call.Arguments, CoreJsonContext.Default.IDictionaryStringObject)
            : "{}";

        // Tool approval check
        if (_requireToolApproval && _approvalRequiredTools.Contains(NormalizeApprovalToolName(tool.Name)))
        {
            if (approvalCallback is not null)
            {
                var approved = await approvalCallback(tool.Name, argsJson, ct);
                if (!approved)
                {
                    _logger?.LogInformation("[{CorrelationId}] Tool {Tool} denied by user",
                        turnCtx.CorrelationId, tool.Name);
                    var deniedInv = new ToolInvocation
                    {
                        ToolName = call.Name,
                        Arguments = argsJson,
                        Result = "Tool execution denied by user.",
                        Duration = TimeSpan.Zero
                    };
                    return (deniedInv, new FunctionResultContent(call.CallId, "Tool execution denied by user."));
                }
            }
            else
            {
                // No approval callback — deny by default for safety
                _logger?.LogWarning("[{CorrelationId}] Tool {Tool} requires approval but no approval channel available — denied",
                    turnCtx.CorrelationId, tool.Name);
                var noCallbackInv = new ToolInvocation
                {
                    ToolName = call.Name,
                    Arguments = argsJson,
                    Result = "Tool requires approval but no approval channel is available. Please confirm you want to execute this action.",
                    Duration = TimeSpan.Zero
                };
                return (noCallbackInv, new FunctionResultContent(call.CallId, noCallbackInv.Result));
            }
        }

        // Run pre-hooks
        foreach (var hook in _hooks)
        {
            try
            {
                var allowed = await hook.BeforeExecuteAsync(tool.Name, argsJson, ct);
                if (!allowed)
                {
                    var msg = $"Tool execution denied by hook: {hook.Name}";
                    _logger?.LogInformation("[{CorrelationId}] {Message}", turnCtx.CorrelationId, msg);
                    var hookInv = new ToolInvocation
                    {
                        ToolName = call.Name,
                        Arguments = argsJson,
                        Result = msg,
                        Duration = TimeSpan.Zero
                    };
                    return (hookInv, new FunctionResultContent(call.CallId, msg));
                }
            }
            catch (Exception ex)
            {
                _logger?.LogWarning(ex, "[{CorrelationId}] Hook {Hook} BeforeExecute threw", turnCtx.CorrelationId, hook.Name);
            }
        }

        var sw = Stopwatch.StartNew();
        string result;
        var toolFailed = false;
        var toolTimedOut = false;
        try
        {
            result = await ExecuteToolWithTimeoutAsync(tool, argsJson, ct);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw; // External cancellation — propagate
        }
        catch (OperationCanceledException)
        {
            result = "Error: Tool execution timed out.";
            toolFailed = true;
            toolTimedOut = true;
            _metrics?.IncrementToolTimeouts();
            _logger?.LogWarning("[{CorrelationId}] Tool {Tool} timed out after {Timeout}s",
                turnCtx.CorrelationId, tool.Name, _toolTimeoutSeconds);
        }
        catch (Exception ex)
        {
            result = $"Error: Tool execution failed ({ex.GetType().Name}).";
            toolFailed = true;
            _metrics?.IncrementToolFailures();
            _logger?.LogWarning(ex, "[{CorrelationId}] Tool {Tool} failed", turnCtx.CorrelationId, tool.Name);
        }
        sw.Stop();

        _metrics?.IncrementToolCalls();
        turnCtx.RecordToolCall(sw.Elapsed, toolFailed, toolTimedOut);
        _logger?.LogDebug("[{CorrelationId}] Tool {Tool} completed in {Duration}ms ok={Ok}",
            turnCtx.CorrelationId, tool.Name, sw.Elapsed.TotalMilliseconds, !toolFailed);

        // Run post-hooks
        foreach (var hook in _hooks)
        {
            try
            {
                await hook.AfterExecuteAsync(tool.Name, argsJson, result, sw.Elapsed, toolFailed, ct);
            }
            catch (Exception ex)
            {
                _logger?.LogWarning(ex, "[{CorrelationId}] Hook {Hook} AfterExecute threw", turnCtx.CorrelationId, hook.Name);
            }
        }

        var invocation = new ToolInvocation
        {
            ToolName = call.Name,
            Arguments = argsJson,
            Result = result,
            Duration = sw.Elapsed
        };

        return (invocation, new FunctionResultContent(call.CallId, result));
    }

    /// <summary>
    /// Calls the LLM through the circuit breaker with retry (exponential backoff) and per-call timeout.
    /// Retries on <see cref="HttpRequestException"/> with 429/5xx status or <see cref="TaskCanceledException"/>
    /// when the per-call timeout fires (not the outer cancellation token).
    /// </summary>
    private async Task<ChatResponse> CallLlmWithResilienceAsync(
        List<ChatMessage> messages, ChatOptions options, TurnContext turnCtx, CancellationToken ct)
    {
        using var activity = Telemetry.ActivitySource.StartActivity("Agent.CallLlm");
        activity?.SetTag("llm.messages_count", messages.Count);

        var lastException = default(Exception);

        for (var attempt = 0; attempt <= _retryCount; attempt++)
        {
            if (attempt > 0)
            {
                var delayMs = (int)Math.Pow(2, attempt - 1) * 1000; // 1s, 2s, 4s …
                turnCtx.RecordRetry();
                _metrics?.IncrementLlmRetries();
                _logger?.LogInformation("[{CorrelationId}] LLM retry {Attempt}/{Max} after {Delay}ms",
                    turnCtx.CorrelationId, attempt, _retryCount, delayMs);
                await Task.Delay(delayMs, ct);
            }

            try
            {
                return await _circuitBreaker.ExecuteAsync(async innerCt =>
                {
                    if (_llmTimeoutSeconds > 0)
                    {
                        using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(innerCt);
                        timeoutCts.CancelAfter(TimeSpan.FromSeconds(_llmTimeoutSeconds));
                        return await _chatClient.GetResponseAsync(messages, options, timeoutCts.Token);
                    }

                    return await _chatClient.GetResponseAsync(messages, options, innerCt);
                }, ct);
            }
            catch (CircuitOpenException)
            {
                throw; // Don't retry when the circuit is open
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                throw; // External cancellation — propagate immediately
            }
            catch (HttpRequestException httpEx) when (IsTransient(httpEx))
            {
                lastException = httpEx;
                _logger?.LogWarning(httpEx, "Transient LLM error on attempt {Attempt}", attempt + 1);
            }
            catch (OperationCanceledException timeoutEx) when (!ct.IsCancellationRequested)
            {
                // Per-call timeout fired — treat as transient
                lastException = timeoutEx;
                _logger?.LogWarning("LLM call timed out on attempt {Attempt} (timeout {Timeout}s)", attempt + 1, _llmTimeoutSeconds);
            }
            catch (Exception ex) when (attempt < _retryCount && IsTransient(ex))
            {
                lastException = ex;
                _logger?.LogWarning(ex, "Transient LLM error on attempt {Attempt}", attempt + 1);
            }
        }

        throw lastException ?? new InvalidOperationException("LLM call failed with no captured exception.");
    }

    /// <summary>
    /// Streams LLM output through the circuit breaker.
    /// Timeout CTS is owned by the caller (StreamLlmCollectAsync) to ensure proper disposal.
    /// Streaming doesn't retry mid-stream — callers handle errors at a higher level.
    /// </summary>
    private IAsyncEnumerable<ChatResponseUpdate> StreamLlmAsync(
        List<ChatMessage> messages, ChatOptions options, CancellationToken ct)
    {
        // Record the circuit breaker check synchronously
        _circuitBreaker.ThrowIfOpen();

        return _chatClient.GetStreamingResponseAsync(messages, options, ct);
    }

    /// <summary>
    /// Executes a tool with a per-tool timeout derived from <see cref="_toolTimeoutSeconds"/>.
    /// </summary>
    private async Task<string> ExecuteToolWithTimeoutAsync(ITool tool, string argsJson, CancellationToken ct)
    {
        if (_toolTimeoutSeconds <= 0)
            return await tool.ExecuteAsync(argsJson, ct);

        using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeoutCts.CancelAfter(TimeSpan.FromSeconds(_toolTimeoutSeconds));
        return await tool.ExecuteAsync(argsJson, timeoutCts.Token);
    }

    /// <summary>
    /// Determines whether an exception represents a transient failure worth retrying.
    /// </summary>
    private static bool IsTransient(Exception ex)
    {
        if (ex is HttpRequestException httpEx && httpEx.StatusCode.HasValue)
        {
            var code = (int)httpEx.StatusCode.Value;
            return code is 429 or (>= 500 and <= 599);
        }

        // IOException / SocketException are often transient network issues
        return ex is System.IO.IOException or System.Net.Sockets.SocketException;
    }

    /// <summary>
    /// Compacts session history by summarizing older turns via the LLM.
    /// Keeps the most recent turns verbatim and replaces older ones with a summary.
    /// </summary>
    internal async Task CompactHistoryAsync(Session session, CancellationToken ct)
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

        var turnsToSummarize = session.History.Take(toSummarizeCount).ToList();
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
                SessionId = session.Id,
                ChannelId = session.ChannelId
            };

            var summarySw = Stopwatch.StartNew();
            var response = await CallLlmWithResilienceAsync(summaryMessages, summaryOptions, compactionTurnCtx, ct);
            summarySw.Stop();

            var summaryInputTokens = response.Usage?.InputTokenCount ?? 0;
            var summaryOutputTokens = response.Usage?.OutputTokenCount ?? 0;
            session.TotalInputTokens += summaryInputTokens;
            session.TotalOutputTokens += summaryOutputTokens;
            compactionTurnCtx.RecordLlmCall(summarySw.Elapsed, summaryInputTokens, summaryOutputTokens);
            _metrics?.IncrementLlmCalls();
            _metrics?.AddInputTokens(summaryInputTokens);
            _metrics?.AddOutputTokens(summaryOutputTokens);

            var summary = response.Text ?? "";

            if (!string.IsNullOrWhiteSpace(summary))
            {
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
        catch (Exception ex)
        {
            _logger?.LogWarning(ex, "History compaction failed — falling back to simple trim");
            TrimHistory(session);
        }
    }

    private List<ChatMessage> BuildMessages(Session session)
    {
        var messages = new List<ChatMessage>
        {
            new(ChatRole.System, _systemPrompt)
        };

        // Add history (bounded to avoid context overflow)
        var skip = Math.Max(0, session.History.Count - _maxHistoryTurns);
        for (var i = skip; i < session.History.Count; i++)
        {
            var turn = session.History[i];
            if (turn.Role == "system" && turn.Content.StartsWith("[Previous conversation summary:", StringComparison.Ordinal))
            {
                // Include compaction summaries as system context
                messages.Add(new ChatMessage(ChatRole.System, turn.Content));
            }
            else if (turn.Role is "user" or "assistant" && turn.Content != "[tool_use]")
            {
                messages.Add(new ChatMessage(
                    turn.Role == "user" ? ChatRole.User : ChatRole.Assistant,
                    turn.Content));
            }
            else if (turn.Content == "[tool_use]" && turn.ToolCalls is { Count: > 0 })
            {
                // Include a summary of tool calls so the LLM retains context of previous actions
                var toolSummary = string.Join("\n", turn.ToolCalls.Select(tc =>
                    $"- Called {tc.ToolName}: {Truncate(tc.Result ?? "(no result)", 200)}"));
                messages.Add(new ChatMessage(ChatRole.Assistant,
                    $"[Previous tool calls:\n{toolSummary}]"));
            }
        }

        return messages;
    }

    private static string BuildSystemPrompt(IReadOnlyList<SkillDefinition> skills, bool requireApproval)
    {
        const int PromptFileMaxChars = 20_000;

        static string? TryReadPromptFile(string path, int maxChars)
        {
            try
            {
                using var fs = File.OpenRead(path);
                using var reader = new StreamReader(fs, Encoding.UTF8, detectEncodingFromByteOrderMarks: true, bufferSize: 4096, leaveOpen: false);

                var buffer = new char[maxChars + 1];
                var read = reader.ReadBlock(buffer, 0, buffer.Length);
                if (read <= 0)
                    return null;

                var take = Math.Min(read, maxChars);
                var text = new string(buffer, 0, take);

                if (read > maxChars || !reader.EndOfStream)
                    text += "…";

                return text;
            }
            catch
            {
                return null;
            }
        }

        static void AppendOptionalPromptFile(ref string prompt, string label, string path, int maxChars)
        {
            if (!File.Exists(path))
                return;

            var content = TryReadPromptFile(path, maxChars);
            if (string.IsNullOrWhiteSpace(content))
                return;

            prompt += $"\n\n[{label}]\n{content}";
        }

        var basePrompt =
            """
            You are OpenClaw, a self-hosted AI assistant. You run locally on the user's machine.
            You can execute tools to interact with the operating system, files, and external services.
            Be concise, helpful, and security-conscious. Never expose credentials or sensitive data.
            When using tools, explain what you're doing and why.
            """;

        if (requireApproval)
        {
            basePrompt +=
                """

                IMPORTANT: Some tools require user approval before execution. If a tool call is denied,
                explain what you were trying to do and ask the user how they'd like to proceed.
                """;
        }

        var workspacePath = Environment.GetEnvironmentVariable("OPENCLAW_WORKSPACE") ?? Directory.GetCurrentDirectory();
        
        var agentsFile = Path.Combine(workspacePath, "AGENTS.md");
        AppendOptionalPromptFile(ref basePrompt, "Workspace Memory (AGENTS.md)", agentsFile, PromptFileMaxChars);

        var soulFile = Path.Combine(workspacePath, "SOUL.md");
        AppendOptionalPromptFile(ref basePrompt, "Agent Personality (SOUL.md)", soulFile, PromptFileMaxChars);

        var skillSection = SkillPromptBuilder.Build(skills);
        return string.IsNullOrEmpty(skillSection) ? basePrompt : basePrompt + "\n" + skillSection;
    }

    /// <summary>
    /// Parses and caches a tool's parameter schema into an AIFunctionDeclaration.
    /// Called once at construction — avoids re-parsing static schemas on every iteration.
    /// </summary>
    private static AIFunctionDeclaration CacheDeclarationTool(ITool tool)
    {
        using var doc = JsonDocument.Parse(tool.ParameterSchema);
        return AIFunctionFactory.CreateDeclaration(
            tool.Name,
            tool.Description,
            doc.RootElement.Clone(),
            returnJsonSchema: null);
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

    private static int EstimateInputTokens(IReadOnlyList<ChatMessage> messages)
    {
        var charCount = 0;
        foreach (var message in messages)
        {
            foreach (var content in message.Contents)
            {
                var value = content.ToString();
                if (!string.IsNullOrEmpty(value))
                    charCount += value.Length;
            }
        }

        return EstimateTokenCount(charCount);
    }

    private static int EstimateTokenCount(int charCount)
    {
        if (charCount <= 0)
            return 0;

        // Approximation used only when streaming providers omit usage details.
        return Math.Max(1, (charCount + 3) / 4);
    }

    private void LogTurnComplete(TurnContext turnCtx)
    {
        _metrics?.SetCircuitBreakerState((int)_circuitBreaker.State);
        _logger?.LogInformation("[{CorrelationId}] Turn complete: {Summary}", turnCtx.CorrelationId, turnCtx.ToString());
    }
}

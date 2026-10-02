using System.Text.Json;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging;
using OpenClaw.Core.Abstractions;
using OpenClaw.Core.Models;
using OpenClaw.Core.Observability;
using OpenClaw.Core.Pipeline;

namespace OpenClaw.Agent.Tools;

/// <summary>
/// Tool that delegates a subtask to a named sub-agent with a focused persona and toolset.
/// Prevents infinite delegation loops via a depth counter.
/// The sub-agent runs in a persisted child session and returns its final response.
/// </summary>
public sealed class DelegateTool : IToolWithContext
{
    private readonly Func<IReadOnlyList<ITool>, LlmProviderConfig, AgentProfile, IAgentRuntime> _runtimeFactory;
    private readonly IChatClient _chatClient;
    private readonly IReadOnlyList<ITool> _allTools;
    private readonly IMemoryStore _memory;
    private readonly MemoryRecallConfig? _recall;
    private readonly LlmProviderConfig _llmConfig;
    private readonly DelegationConfig _delegationConfig;
    private readonly int _currentDepth;
    private readonly RuntimeMetrics? _metrics;
    private readonly ILogger? _logger;

    public string Name => "delegate_agent";

    public string Description =>
        "Delegate a subtask to a specialized sub-agent. " +
        "Available profiles: " + string.Join(", ", _delegationConfig.Profiles.Keys) + ". " +
        "Use this when a task requires a different expertise or focus area.";

    public string ParameterSchema =>
        """
        {
          "type": "object",
          "properties": {
            "profile": {
              "type": "string",
              "description": "Name of the agent profile to delegate to"
            },
            "task": {
              "type": "string",
              "description": "The task description for the sub-agent to complete"
            }
          },
          "required": ["profile", "task"]
        }
        """;

    public DelegateTool(
        IChatClient chatClient,
        IReadOnlyList<ITool> allTools,
        IMemoryStore memory,
        LlmProviderConfig llmConfig,
        DelegationConfig delegationConfig,
        int currentDepth = 0,
        RuntimeMetrics? metrics = null,
        ILogger? logger = null,
        MemoryRecallConfig? recall = null,
        Func<IReadOnlyList<ITool>, LlmProviderConfig, AgentProfile, IAgentRuntime>? runtimeFactory = null)
    {
        _chatClient = chatClient;
        _allTools = allTools;
        _memory = memory;
        _llmConfig = llmConfig;
        _delegationConfig = delegationConfig;
        _currentDepth = currentDepth;
        _metrics = metrics;
        _logger = logger;
        _recall = recall;
        _runtimeFactory = runtimeFactory ?? ((tools, subConfig, profile) => new AgentRuntime(
            _chatClient,
            tools,
            _memory,
            subConfig,
            profile.MaxHistoryTurns,
            logger: _logger,
            metrics: _metrics,
            maxIterations: profile.MaxIterations,
            recall: _recall));
    }

    public ValueTask<string> ExecuteAsync(string argumentsJson, CancellationToken ct)
        => ExecuteCoreAsync(argumentsJson, context: null, ct);

    public ValueTask<string> ExecuteAsync(string argumentsJson, ToolExecutionContext context, CancellationToken ct)
        => ExecuteCoreAsync(argumentsJson, context, ct);

    private async ValueTask<string> ExecuteCoreAsync(string argumentsJson, ToolExecutionContext? context, CancellationToken ct)
    {
        using var doc = JsonDocument.Parse(argumentsJson);
        var root = doc.RootElement;

        var profileName = root.TryGetProperty("profile", out var p) ? p.GetString() : null;
        var task = root.TryGetProperty("task", out var t) ? t.GetString() : null;

        if (string.IsNullOrWhiteSpace(profileName))
            return "Error: 'profile' parameter is required.";
        if (string.IsNullOrWhiteSpace(task))
            return "Error: 'task' parameter is required.";

        if (!_delegationConfig.Profiles.TryGetValue(profileName, out var profile))
            return $"Error: Unknown agent profile '{profileName}'. Available: {string.Join(", ", _delegationConfig.Profiles.Keys)}";

        if (_currentDepth >= _delegationConfig.MaxDepth)
            return $"Error: Maximum delegation depth ({_delegationConfig.MaxDepth}) reached. Cannot delegate further.";

        _logger?.LogInformation("Delegating to sub-agent '{Profile}' (depth {Depth}): {Task}",
            profileName, _currentDepth + 1, task.Length > 100 ? task[..100] + "…" : task);

        // Build tool subset for the sub-agent
        var toolSubset = profile.AllowedTools.Length > 0
            ? _allTools.Where(tool => profile.AllowedTools.Contains(tool.Name, StringComparer.Ordinal)).ToList()
            : _allTools.Where(tool => tool.Name != "delegate_agent").ToList(); // Exclude self to prevent trivial loops

        // Create a child DelegateTool at depth + 1 (if profiles allow further delegation)
        if (_currentDepth + 1 < _delegationConfig.MaxDepth)
        {
            var childDelegate = new DelegateTool(
                _chatClient, _allTools, _memory, _llmConfig, _delegationConfig,
                _currentDepth + 1, _metrics, _logger, _recall, _runtimeFactory);
            toolSubset = [.. toolSubset, childDelegate];
        }

        // Override system prompt if profile provides one; build an ephemeral LLM config
        var subConfig = new LlmProviderConfig
        {
            Provider = _llmConfig.Provider,
            Model = _llmConfig.Model,
            ApiKey = _llmConfig.ApiKey,
            Endpoint = _llmConfig.Endpoint,
            MaxTokens = _llmConfig.MaxTokens,
            Temperature = _llmConfig.Temperature,
            TimeoutSeconds = _llmConfig.TimeoutSeconds,
            RetryCount = _llmConfig.RetryCount,
            CircuitBreakerThreshold = _llmConfig.CircuitBreakerThreshold,
            CircuitBreakerCooldownSeconds = _llmConfig.CircuitBreakerCooldownSeconds
        };

        var subAgent = _runtimeFactory(toolSubset, subConfig, profile);
        var now = DateTimeOffset.UtcNow;
        var allowedTools = toolSubset
            .Select(static item => item.Name)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .Order(StringComparer.OrdinalIgnoreCase)
            .ToArray();

        // Create a persisted child session for the sub-agent.
        var subSession = new Session
        {
            Id = $"delegate:{profileName}:{Guid.NewGuid():N}",
            ChannelId = "delegation",
            SenderId = profileName,
            Delegation = new SessionDelegationMetadata
            {
                ParentSessionId = context?.Session.Id,
                ParentChannelId = context?.Session.ChannelId,
                ParentSenderId = context?.Session.SenderId,
                Profile = profileName,
                RequestedTask = task,
                AllowedTools = allowedTools,
                Depth = _currentDepth + 1,
                StartedAtUtc = now,
                Status = "running"
            }
        };

        var parentSummary = context is null
            ? null
            : UpsertParentDelegationSummary(context.Session, subSession.Id, profileName, task, now);

        // Prefix the task with the profile's system context
        var fullTask = string.IsNullOrWhiteSpace(profile.SystemPrompt)
            ? task
            : $"[Context: {profile.SystemPrompt}]\n\n{task}";

        try
        {
            var result = await subAgent.RunAsync(
                subSession,
                fullTask,
                ct,
                callerCredentialContext: context?.McpCallerCredentialContext);
            FinalizeDelegation(subSession, parentSummary, status: "completed", result, error: null);
            await _memory.SaveSessionAsync(subSession, ct);
            _logger?.LogInformation("Sub-agent '{Profile}' completed (depth {Depth}), response length={Length}",
                profileName, _currentDepth + 1, result.Length);
            return result;
        }
        catch (Exception ex)
        {
            var error = $"Error: Sub-agent '{profileName}' failed: {ex.Message}";
            FinalizeDelegation(subSession, parentSummary, status: "failed", result: null, error);
            await _memory.SaveSessionAsync(subSession, ct);
            _logger?.LogError(ex, "Sub-agent '{Profile}' failed (depth {Depth})", profileName, _currentDepth + 1);
            return error;
        }
    }

    private static SessionDelegationChildSummary UpsertParentDelegationSummary(
        Session parentSession,
        string childSessionId,
        string profileName,
        string task,
        DateTimeOffset startedAtUtc)
    {
        var existing = parentSession.DelegatedSessions.FirstOrDefault(item => string.Equals(item.SessionId, childSessionId, StringComparison.Ordinal));
        if (existing is not null)
            return existing;

        var created = new SessionDelegationChildSummary
        {
            SessionId = childSessionId,
            Profile = profileName,
            TaskPreview = Truncate(task, 200),
            StartedAtUtc = startedAtUtc,
            Status = "running"
        };
        parentSession.DelegatedSessions.Add(created);
        return created;
    }

    private static void FinalizeDelegation(
        Session subSession,
        SessionDelegationChildSummary? parentSummary,
        string status,
        string? result,
        string? error)
    {
        var toolUsage = BuildToolUsage(subSession);
        var proposedChanges = toolUsage
            .Where(static item => item.IsMutation)
            .Select(static item => new SessionDelegationChangeSummary
            {
                ToolName = item.ToolName,
                Action = item.Action,
                Summary = item.Summary
            })
            .ToArray();
        var preview = Truncate(error ?? result ?? string.Empty, 240);
        var completedAtUtc = DateTimeOffset.UtcNow;

        if (subSession.Delegation is not null)
        {
            subSession.Delegation.Status = status;
            subSession.Delegation.CompletedAtUtc = completedAtUtc;
            subSession.Delegation.FinalResponsePreview = preview;
            subSession.Delegation.ToolUsage = toolUsage;
            subSession.Delegation.ProposedChanges = proposedChanges;
        }

        if (parentSummary is not null)
        {
            parentSummary.Status = status;
            parentSummary.CompletedAtUtc = completedAtUtc;
            parentSummary.FinalResponsePreview = preview;
            parentSummary.ToolUsage = toolUsage;
            parentSummary.ProposedChanges = proposedChanges;
        }
    }

    private static SessionDelegationToolUsage[] BuildToolUsage(Session session)
    {
        return session.History
            .SelectMany(static turn => turn.ToolCalls ?? [])
            .Select(static call =>
            {
                var descriptor = ToolActionPolicyResolver.Resolve(call.ToolName, call.Arguments);
                return new SessionDelegationToolUsage
                {
                    ToolName = call.ToolName,
                    Action = descriptor.Action,
                    Summary = string.IsNullOrWhiteSpace(descriptor.Summary)
                        ? $"Execute tool '{call.ToolName}'."
                        : descriptor.Summary,
                    IsMutation = descriptor.IsMutation || ToolActionPolicyResolver.IsMutationCapable(call.ToolName, call.Arguments),
                    Count = 1
                };
            })
            .GroupBy(static item => new { item.ToolName, item.Action, item.Summary, item.IsMutation })
            .Select(static group => new SessionDelegationToolUsage
            {
                ToolName = group.Key.ToolName,
                Action = group.Key.Action,
                Summary = group.Key.Summary,
                IsMutation = group.Key.IsMutation,
                Count = group.Sum(static item => item.Count)
            })
            .OrderByDescending(static item => item.Count)
            .ThenBy(static item => item.ToolName, StringComparer.OrdinalIgnoreCase)
            .ToArray();
    }

    private static string Truncate(string value, int maxLength)
    {
        if (string.IsNullOrWhiteSpace(value))
            return string.Empty;

        var trimmed = value.Trim();
        return trimmed.Length <= maxLength
            ? trimmed
            : trimmed[..maxLength] + "…";
    }
}

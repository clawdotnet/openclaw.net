using System.Text.Json;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging;
using OpenClaw.Core.Abstractions;
using OpenClaw.Core.Models;
using OpenClaw.Core.Observability;

namespace OpenClaw.Agent.Tools;

/// <summary>
/// Tool that delegates a subtask to a named sub-agent with a focused persona and toolset.
/// Prevents infinite delegation loops via a depth counter.
/// The sub-agent runs in an ephemeral session and returns its final response.
/// </summary>
public sealed class DelegateTool : ITool
{
    private readonly IChatClient _chatClient;
    private readonly IReadOnlyList<ITool> _allTools;
    private readonly IMemoryStore _memory;
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
        ILogger? logger = null)
    {
        _chatClient = chatClient;
        _allTools = allTools;
        _memory = memory;
        _llmConfig = llmConfig;
        _delegationConfig = delegationConfig;
        _currentDepth = currentDepth;
        _metrics = metrics;
        _logger = logger;
    }

    public async ValueTask<string> ExecuteAsync(string argumentsJson, CancellationToken ct)
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
                _currentDepth + 1, _metrics, _logger);
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

        var subAgent = new AgentRuntime(
            _chatClient,
            toolSubset,
            _memory,
            subConfig,
            profile.MaxHistoryTurns,
            logger: _logger,
            metrics: _metrics,
            maxIterations: profile.MaxIterations);

        // Create an ephemeral session for the sub-agent
        var subSession = new Session
        {
            Id = $"delegate:{profileName}:{Guid.NewGuid():N}",
            ChannelId = "delegation",
            SenderId = profileName
        };

        // Prefix the task with the profile's system context
        var fullTask = string.IsNullOrWhiteSpace(profile.SystemPrompt)
            ? task
            : $"[Context: {profile.SystemPrompt}]\n\n{task}";

        try
        {
            var result = await subAgent.RunAsync(subSession, fullTask, ct);
            _logger?.LogInformation("Sub-agent '{Profile}' completed (depth {Depth}), response length={Length}",
                profileName, _currentDepth + 1, result.Length);
            return result;
        }
        catch (Exception ex)
        {
            _logger?.LogError(ex, "Sub-agent '{Profile}' failed (depth {Depth})", profileName, _currentDepth + 1);
            return $"Error: Sub-agent '{profileName}' failed: {ex.Message}";
        }
    }
}

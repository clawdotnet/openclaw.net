namespace OpenClaw.Core.Validation;

/// <summary>
/// Validates <see cref="Models.GatewayConfig"/> at startup and returns any errors.
/// Fail-fast: the gateway should refuse to start with invalid configuration.
/// </summary>
public static class ConfigValidator
{
    public static IReadOnlyList<string> Validate(Models.GatewayConfig config)
    {
        var errors = new List<string>();

        // Port
        if (config.Port is < 1 or > 65535)
            errors.Add($"Port must be between 1 and 65535 (got {config.Port}).");

        // LLM
        if (string.IsNullOrWhiteSpace(config.Llm.Model))
            errors.Add("Llm.Model must be set.");
        if (config.Llm.MaxTokens < 1)
            errors.Add($"Llm.MaxTokens must be >= 1 (got {config.Llm.MaxTokens}).");
        if (config.Llm.Temperature is < 0 or > 2)
            errors.Add($"Llm.Temperature must be between 0 and 2 (got {config.Llm.Temperature}).");
        if (config.Llm.TimeoutSeconds < 0)
            errors.Add($"Llm.TimeoutSeconds must be >= 0 (got {config.Llm.TimeoutSeconds}).");
        if (config.Llm.RetryCount < 0)
            errors.Add($"Llm.RetryCount must be >= 0 (got {config.Llm.RetryCount}).");
        if (config.Llm.CircuitBreakerThreshold < 1)
            errors.Add($"Llm.CircuitBreakerThreshold must be >= 1 (got {config.Llm.CircuitBreakerThreshold}).");
        if (config.Llm.CircuitBreakerCooldownSeconds < 1)
            errors.Add($"Llm.CircuitBreakerCooldownSeconds must be >= 1 (got {config.Llm.CircuitBreakerCooldownSeconds}).");

        // Memory
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
        }

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

        // Middleware
        if (config.SessionTokenBudget < 0)
            errors.Add($"SessionTokenBudget must be >= 0 (got {config.SessionTokenBudget}).");
        if (config.SessionRateLimitPerMinute < 0)
            errors.Add($"SessionRateLimitPerMinute must be >= 0 (got {config.SessionRateLimitPerMinute}).");

        return errors;
    }
}

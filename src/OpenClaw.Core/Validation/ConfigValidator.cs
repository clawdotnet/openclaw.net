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
            }
        }

        return errors;
    }

    private static bool IsValidCronExpression(string expression)
    {
        if (string.IsNullOrWhiteSpace(expression))
            return false;

        var parts = expression.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length != 5)
            return false;

        return IsValidCronField(parts[0], 0, 59) &&
               IsValidCronField(parts[1], 0, 23) &&
               IsValidCronField(parts[2], 1, 31) &&
               IsValidCronField(parts[3], 1, 12) &&
               IsValidCronField(parts[4], 0, 6);
    }

    private static bool IsValidCronField(string field, int min, int max)
    {
        if (field == "*")
            return true;

        if (int.TryParse(field, out var exact))
            return exact >= min && exact <= max;

        if (field.Contains('/'))
        {
            var stepParts = field.Split('/');
            if (stepParts.Length != 2 || stepParts[0] != "*" || !int.TryParse(stepParts[1], out var step))
                return false;
            return step > 0;
        }

        if (field.Contains(','))
        {
            var options = field.Split(',');
            if (options.Length == 0)
                return false;

            foreach (var option in options)
            {
                if (!int.TryParse(option, out var parsed) || parsed < min || parsed > max)
                    return false;
            }

            return true;
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

            return start >= min && end <= max && start <= end;
        }

        return false;
    }
}

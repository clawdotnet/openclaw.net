using System.Text;
using System.Text.Json;
using OpenClaw.Core.Abstractions;
using OpenClaw.Core.Models;
using OpenClaw.Core.Observability;
using OpenClaw.Core.Sessions;

namespace OpenClaw.Gateway.Tools;

/// <summary>
/// Gateway management and status operations.
/// </summary>
internal sealed class GatewayTool : ITool
{
    private readonly RuntimeMetrics _metrics;
    private readonly SessionManager _sessions;
    private readonly GatewayConfig _config;
    private readonly GatewayRuntimeState _runtimeState;

    public GatewayTool(RuntimeMetrics metrics, SessionManager sessions, GatewayConfig config, GatewayRuntimeState runtimeState)
    {
        _metrics = metrics;
        _sessions = sessions;
        _config = config;
        _runtimeState = runtimeState;
    }

    public string Name => "gateway";
    public string Description => "Get gateway status, metrics, and configuration summary.";
    public string ParameterSchema => """{"type":"object","properties":{"action":{"type":"string","enum":["status","config"],"description":"Action: status (runtime metrics) or config (configuration summary)"}}}""";

    public ValueTask<string> ExecuteAsync(string argumentsJson, CancellationToken ct)
    {
        using var args = JsonDocument.Parse(
            string.IsNullOrWhiteSpace(argumentsJson) ? "{}" : argumentsJson);
        var root = args.RootElement;

        var action = root.TryGetProperty("action", out var a) && a.ValueKind == JsonValueKind.String
            ? a.GetString() : "status";

        return ValueTask.FromResult(action switch
        {
            "config" => GetConfig(),
            _ => GetStatus()
        });
    }

    private string GetStatus()
    {
        var snapshot = _metrics.Snapshot();
        var sb = new StringBuilder();
        sb.AppendLine("Gateway Status:");
        sb.AppendLine($"  Active sessions: {_sessions.ActiveCount}");
        sb.AppendLine($"  Total requests: {snapshot.TotalRequests}");
        sb.AppendLine($"  Total LLM calls: {snapshot.TotalLlmCalls}");
        sb.AppendLine($"  Total tokens (in/out): {snapshot.TotalInputTokens}/{snapshot.TotalOutputTokens}");
        sb.AppendLine($"  Tool calls: {snapshot.TotalToolCalls}");
        sb.AppendLine($"  Tool failures: {snapshot.TotalToolFailures}");
        sb.AppendLine($"  LLM errors: {snapshot.TotalLlmErrors}");
        sb.AppendLine($"  LLM retries: {snapshot.TotalLlmRetries}");
        return sb.ToString().TrimEnd();
    }

    private string GetConfig()
    {
        var browserAvailability = BrowserToolSupport.Evaluate(_config, _runtimeState);
        var sb = new StringBuilder();
        sb.AppendLine("Gateway Configuration:");
        sb.AppendLine($"  Bind: {_config.BindAddress}:{_config.Port}");
        sb.AppendLine($"  LLM Provider: {_config.Llm.Provider}");
        sb.AppendLine($"  Model: {_config.Llm.Model}");
        sb.AppendLine($"  Memory: {_config.Memory.Provider}");
        sb.AppendLine($"  Max sessions: {_config.MaxConcurrentSessions}");
        sb.AppendLine($"  Session timeout: {_config.SessionTimeoutMinutes}m");
        sb.AppendLine($"  Autonomy: {_config.Tooling.AutonomyMode}");
        sb.AppendLine($"  Tool approval: {_config.Tooling.RequireToolApproval}");
        sb.AppendLine($"  Browser: configured={browserAvailability.ConfiguredEnabled} registered={browserAvailability.Registered} local_supported={browserAvailability.LocalExecutionSupported} backend_configured={browserAvailability.ExecutionBackendConfigured}");
        sb.AppendLine($"  Shell: {_config.Tooling.AllowShell}");
        sb.AppendLine($"  Cron: {_config.Cron.Enabled} ({_config.Cron.Jobs.Count} jobs)");
        return sb.ToString().TrimEnd();
    }
}

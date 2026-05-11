using System.Net.Http.Json;
using Microsoft.Extensions.Logging;
using OpenClaw.Core.Abstractions;
using OpenClaw.Core.Models;

namespace OpenClaw.Core.Governance;

public sealed class HttpSidecarToolGovernanceService : IToolGovernanceService
{
    private readonly HttpClient _httpClient;
    private readonly ToolGovernanceConfig _config;
    private readonly ILogger<HttpSidecarToolGovernanceService> _logger;

    public HttpSidecarToolGovernanceService(
        HttpClient httpClient,
        ToolGovernanceConfig config,
        ILogger<HttpSidecarToolGovernanceService> logger)
    {
        _httpClient = httpClient;
        _config = config;
        _logger = logger;
    }

    public async ValueTask<GovernanceDecision> AuthorizeAsync(
        ToolGovernanceContext context,
        CancellationToken cancellationToken = default)
    {
        if (!_config.Enabled)
            return GovernanceDecision.Allow("Governance disabled");

        using var timeout = CreateTimeoutToken(cancellationToken);
        var effectiveToken = timeout?.Token ?? cancellationToken;
        var request = new ToolGovernanceSidecarRequest
        {
            AgentId = context.AgentId,
            ConversationId = context.SessionId,
            SessionId = context.SessionId,
            ChannelId = context.ChannelId,
            UserId = context.SenderId,
            TraceId = context.CorrelationId,
            CallId = context.CallId,
            ToolName = context.ToolName,
            ToolCategory = context.Descriptor.Category,
            RiskLevel = context.Descriptor.RiskLevel.ToString(),
            ArgumentsJson = context.ArgumentsJson,
            ActionDescriptor = context.ActionDescriptor,
            Descriptor = context.Descriptor
        };

        try
        {
            using var response = await _httpClient.PostAsJsonAsync(
                NormalizeEndpoint(_config.DecisionEndpoint, "/api/v1/execute"),
                request,
                CoreJsonContext.Default.ToolGovernanceSidecarRequest,
                effectiveToken);

            if (!response.IsSuccessStatusCode)
            {
                return BuildUnavailableDecision(
                    context,
                    $"Governance sidecar returned {(int)response.StatusCode} {response.StatusCode}");
            }

            var sidecar = await response.Content.ReadFromJsonAsync(
                CoreJsonContext.Default.ToolGovernanceSidecarResponse,
                effectiveToken);

            if (sidecar is null)
                return BuildUnavailableDecision(context, "Governance sidecar returned an empty response");

            var action = MapAction(sidecar.Action);
            var allowed = action switch
            {
                GovernanceAction.Deny => false,
                GovernanceAction.RequireApproval => true,
                GovernanceAction.Allow or GovernanceAction.AuditOnly or GovernanceAction.Redact => sidecar.Allowed ?? true,
                _ => false
            };

            return new GovernanceDecision
            {
                Allowed = allowed,
                Action = action,
                Reason = sidecar.Reason,
                TrustScore = sidecar.TrustScore,
                PolicyId = sidecar.PolicyId,
                RuleId = sidecar.RuleId,
                EvaluationMs = sidecar.EvaluationMs,
                RedactedArgumentsJson = sidecar.RedactedArgumentsJson,
                ReplacementArgumentsJson = sidecar.ReplacementArgumentsJson
            };
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            return BuildUnavailableDecision(context, "Governance sidecar timed out");
        }
        catch (HttpRequestException ex)
        {
            return BuildUnavailableDecision(context, $"Governance sidecar unavailable: {ex.Message}");
        }
        catch (Exception ex) when (ex is not OutOfMemoryException and not StackOverflowException)
        {
            _logger.LogWarning(ex, "Governance sidecar authorization failed for tool {Tool}", context.ToolName);
            return BuildUnavailableDecision(context, $"Governance sidecar failed: {ex.Message}");
        }
    }

    public async ValueTask RecordResultAsync(
        ToolGovernanceContext context,
        GovernanceDecision decision,
        ToolGovernanceExecutionResult result,
        CancellationToken cancellationToken = default)
    {
        if (!_config.Enabled || !_config.AuditResults || string.IsNullOrWhiteSpace(_config.ResultEndpoint))
            return;

        using var timeout = CreateTimeoutToken(cancellationToken);
        var effectiveToken = timeout?.Token ?? cancellationToken;
        var request = new ToolGovernanceSidecarResultRequest
        {
            AgentId = context.AgentId,
            ConversationId = context.SessionId,
            SessionId = context.SessionId,
            ChannelId = context.ChannelId,
            UserId = context.SenderId,
            TraceId = context.CorrelationId,
            CallId = context.CallId,
            ToolName = context.ToolName,
            Descriptor = context.Descriptor,
            Decision = decision,
            Result = result
        };

        try
        {
            using var response = await _httpClient.PostAsJsonAsync(
                NormalizeEndpoint(_config.ResultEndpoint, "/api/v1/result"),
                request,
                CoreJsonContext.Default.ToolGovernanceSidecarResultRequest,
                effectiveToken);

            if (!response.IsSuccessStatusCode)
            {
                _logger.LogWarning(
                    "Governance sidecar result audit failed for tool {Tool} with status {StatusCode}",
                    context.ToolName,
                    response.StatusCode);
            }
        }
        catch (Exception ex) when (ex is not OperationCanceledException and not OutOfMemoryException and not StackOverflowException)
        {
            _logger.LogWarning(ex, "Governance sidecar result audit failed for tool {Tool}", context.ToolName);
        }
    }

    private CancellationTokenSource? CreateTimeoutToken(CancellationToken cancellationToken)
    {
        if (_config.TimeoutMs <= 0)
            return null;

        var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(TimeSpan.FromMilliseconds(_config.TimeoutMs));
        return timeout;
    }

    private GovernanceDecision BuildUnavailableDecision(ToolGovernanceContext context, string reason)
    {
        if (ShouldFailClosed(context.Descriptor))
        {
            return new GovernanceDecision
            {
                Allowed = false,
                Action = GovernanceAction.Deny,
                Reason = reason,
                IsUnavailable = true
            };
        }

        _logger.LogWarning(
            "Governance sidecar unavailable for tool {Tool}. Continuing because low-risk fail-open is enabled. Reason={Reason}",
            context.ToolName,
            reason);

        return new GovernanceDecision
        {
            Allowed = true,
            Action = GovernanceAction.AuditOnly,
            Reason = reason,
            IsUnavailable = true
        };
    }

    private bool ShouldFailClosed(ToolGovernanceDescriptor descriptor)
    {
        if (_config.RequireGovernanceForHighRiskTools && IsHighRiskOrSideEffecting(descriptor))
            return true;

        if (IsLowRiskReadOnly(descriptor) && _config.FailOpenReadOnlyLowRisk)
            return false;

        return _config.FailClosed;
    }

    private static bool IsHighRiskOrSideEffecting(ToolGovernanceDescriptor descriptor)
        => descriptor.RiskLevel is ToolGovernanceRiskLevel.High or ToolGovernanceRiskLevel.Critical
           || !descriptor.ReadOnly
           || descriptor.CanExecuteCode
           || descriptor.CanSendDataExternally
           || descriptor.Capabilities.Any(static capability =>
               capability is "process.execute" or "filesystem.write" or "external.http" or "data.export" or "message.send");

    private static bool IsLowRiskReadOnly(ToolGovernanceDescriptor descriptor)
        => descriptor.ReadOnly &&
           descriptor.RiskLevel == ToolGovernanceRiskLevel.Low &&
           !descriptor.CanExecuteCode &&
           !descriptor.CanSendDataExternally;

    private static string NormalizeEndpoint(string? endpoint, string fallback)
        => string.IsNullOrWhiteSpace(endpoint) ? fallback : endpoint.Trim();

    private static GovernanceAction MapAction(string? action)
    {
        var normalized = (action ?? "allow").Trim().Replace("-", "_", StringComparison.Ordinal).ToLowerInvariant();
        return normalized switch
        {
            "allow" => GovernanceAction.Allow,
            "deny" => GovernanceAction.Deny,
            "require_approval" or "requireapproval" => GovernanceAction.RequireApproval,
            "redact" => GovernanceAction.Redact,
            "audit_only" or "auditonly" or "log" or "warn" => GovernanceAction.AuditOnly,
            _ => GovernanceAction.Deny
        };
    }
}

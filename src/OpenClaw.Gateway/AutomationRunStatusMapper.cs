using OpenClaw.Core.Models;

namespace OpenClaw.Gateway;

internal static class AutomationRunStatusMapper
{
    public static string DeriveOutcome(string automationId, string lifecycleState, string verificationStatus, string? signalSeverity = null)
    {
        if (string.Equals(lifecycleState, AutomationLifecycleStates.Queued, StringComparison.OrdinalIgnoreCase))
            return AutomationLifecycleStates.Queued;

        if (string.Equals(lifecycleState, AutomationLifecycleStates.Running, StringComparison.OrdinalIgnoreCase))
            return AutomationLifecycleStates.Running;

        if (string.Equals(lifecycleState, AutomationLifecycleStates.Stuck, StringComparison.OrdinalIgnoreCase))
            return AutomationLifecycleStates.Stuck;

        if (string.Equals(automationId, GatewayAutomationService.HeartbeatAutomationId, StringComparison.OrdinalIgnoreCase))
        {
            if (string.Equals(signalSeverity, AutomationSignalSeverities.Alert, StringComparison.OrdinalIgnoreCase)
                && string.Equals(verificationStatus, AutomationVerificationStatuses.Verified, StringComparison.OrdinalIgnoreCase))
            {
                return "alert";
            }

            if (string.Equals(verificationStatus, AutomationVerificationStatuses.Failed, StringComparison.OrdinalIgnoreCase))
                return "error";

            if (string.Equals(verificationStatus, AutomationVerificationStatuses.Verified, StringComparison.OrdinalIgnoreCase))
                return "ok";
        }

        return verificationStatus switch
        {
            AutomationVerificationStatuses.Verified => "success",
            AutomationVerificationStatuses.NotVerified => AutomationVerificationStatuses.NotVerified,
            AutomationVerificationStatuses.Failed => AutomationVerificationStatuses.Failed,
            AutomationVerificationStatuses.Blocked => AutomationVerificationStatuses.Blocked,
            _ => AutomationLifecycleStates.Never
        };
    }

    public static string DeriveHealthState(string lifecycleState, string verificationStatus, DateTimeOffset? quarantinedAtUtc)
    {
        if (quarantinedAtUtc is not null)
            return AutomationHealthStates.Quarantined;

        if (string.Equals(lifecycleState, AutomationLifecycleStates.Stuck, StringComparison.OrdinalIgnoreCase))
            return AutomationHealthStates.Degraded;

        if (string.Equals(verificationStatus, AutomationVerificationStatuses.Verified, StringComparison.OrdinalIgnoreCase))
            return AutomationHealthStates.Healthy;

        if (verificationStatus is AutomationVerificationStatuses.NotVerified
            or AutomationVerificationStatuses.Failed
            or AutomationVerificationStatuses.Blocked)
        {
            return AutomationHealthStates.Degraded;
        }

        return AutomationHealthStates.Unknown;
    }

    public static AutomationRunState MapHeartbeatState(HeartbeatRunStatusDto status, AutomationRunState? overlay = null)
    {
        var verificationStatus = status.Outcome switch
        {
            "ok" => AutomationVerificationStatuses.Verified,
            "alert" => AutomationVerificationStatuses.Verified,
            "error" => AutomationVerificationStatuses.Failed,
            _ => overlay?.VerificationStatus ?? AutomationVerificationStatuses.NotRun
        };

        var signalSeverity = status.Outcome switch
        {
            "alert" => AutomationSignalSeverities.Alert,
            "error" => AutomationSignalSeverities.Error,
            _ => overlay?.SignalSeverity
        };

        var lifecycleState = overlay?.LifecycleState;
        if (string.IsNullOrWhiteSpace(lifecycleState) || string.Equals(lifecycleState, AutomationLifecycleStates.Never, StringComparison.OrdinalIgnoreCase))
            lifecycleState = string.Equals(status.Outcome, "never", StringComparison.OrdinalIgnoreCase) ? AutomationLifecycleStates.Never : AutomationLifecycleStates.Completed;

        var quarantinedAtUtc = overlay?.QuarantinedAtUtc;

        return new AutomationRunState
        {
            AutomationId = GatewayAutomationService.HeartbeatAutomationId,
            Outcome = DeriveOutcome(GatewayAutomationService.HeartbeatAutomationId, lifecycleState!, verificationStatus, signalSeverity),
            LifecycleState = lifecycleState!,
            VerificationStatus = verificationStatus,
            HealthState = DeriveHealthState(lifecycleState!, verificationStatus, quarantinedAtUtc),
            LastRunAtUtc = overlay?.LastRunAtUtc ?? status.LastRunAtUtc,
            LastCompletedAtUtc = string.Equals(lifecycleState, AutomationLifecycleStates.Completed, StringComparison.OrdinalIgnoreCase)
                ? (overlay?.LastCompletedAtUtc ?? status.LastRunAtUtc)
                : overlay?.LastCompletedAtUtc,
            LastDeliveredAtUtc = overlay?.LastDeliveredAtUtc ?? status.LastDeliveredAtUtc,
            LastVerifiedSuccessAtUtc = string.Equals(verificationStatus, AutomationVerificationStatuses.Verified, StringComparison.OrdinalIgnoreCase)
                ? (overlay?.LastVerifiedSuccessAtUtc ?? status.LastRunAtUtc)
                : overlay?.LastVerifiedSuccessAtUtc,
            QuarantinedAtUtc = quarantinedAtUtc,
            NextRetryAtUtc = null,
            DeliverySuppressed = status.DeliverySuppressed,
            InputTokens = status.InputTokens,
            OutputTokens = status.OutputTokens,
            FailureStreak = overlay?.FailureStreak ?? 0,
            UnverifiedStreak = overlay?.UnverifiedStreak ?? 0,
            NextRetryAttempt = null,
            LastRunId = overlay?.LastRunId,
            SessionId = status.SessionId,
            MessagePreview = status.MessagePreview,
            VerificationSummary = overlay?.VerificationSummary,
            QuarantineReason = overlay?.QuarantineReason,
            SignalSeverity = signalSeverity
        };
    }
}

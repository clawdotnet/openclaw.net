namespace OpenClaw.Gateway.Bootstrap;

internal sealed record StartupRecoveryOutcome(StartupRecoveryResult Result, LocalStartupSession? Session = null)
{
    public static StartupRecoveryOutcome NotHandled { get; } = new(StartupRecoveryResult.NotHandled);

    public static StartupRecoveryOutcome Declined { get; } = new(StartupRecoveryResult.Declined);

    public static StartupRecoveryOutcome Recovered(LocalStartupSession session) => new(StartupRecoveryResult.Recovered, session);
}

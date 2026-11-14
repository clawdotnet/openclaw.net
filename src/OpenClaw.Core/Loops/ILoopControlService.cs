namespace OpenClaw.Core.Loops;

/// <summary>
/// Receives termination signals from the LoopControlTool or LoopTerminationDetector.
/// Implemented by ClawLoopScheduler to bridge tool/detector with loop cancellation.
/// </summary>
public interface ILoopControlService
{
    /// <summary>
    /// Signals that the loop for a session should be terminated.
    /// Called by LoopControlTool when the model declares completion,
    /// or by LoopTerminationDetector when keyword match fires.
    /// </summary>
    Task SignalCompleteAsync(string sessionId, CancellationToken ct);
}

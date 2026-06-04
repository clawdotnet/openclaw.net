namespace OpenClaw.Core.Abstractions;

/// <summary>
/// Describes whether a sandbox-capable tool may fall back to local execution.
/// </summary>
public interface IToolLocalExecutionPolicy
{
    bool LocalExecutionSupported { get; }

    string LocalExecutionUnavailableFailureCode { get; }

    string LocalExecutionUnavailableMessage { get; }
}

namespace OpenClaw.Core.Abstractions;

/// <summary>An adapter's known failed/blocked outcome, preserving the result exposed to the model.</summary>
public sealed class ToolOutcomeException : Exception
{
    public string Result { get; }
    public string ResultStatus { get; }
    public string? FailureCode { get; }
    public string? FailureMessage { get; }
    public ToolOutcomeException(string result, string status, string? failureCode, string? failureMessage) : base(failureMessage)
    {
        if (status is not ("failed" or "blocked")) throw new ArgumentException("Invalid tool failure status.", nameof(status));
        Result = result; ResultStatus = status; FailureCode = failureCode; FailureMessage = failureMessage;
    }
}

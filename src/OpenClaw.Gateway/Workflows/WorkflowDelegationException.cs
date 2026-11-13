namespace OpenClaw.Gateway.Workflows;

internal sealed class WorkflowDelegationException(string errorCode, int statusCode) : Exception(errorCode)
{
    public string ErrorCode { get; } = errorCode;
    public int StatusCode { get; } = statusCode;
}

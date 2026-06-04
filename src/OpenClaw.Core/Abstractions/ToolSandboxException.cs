namespace OpenClaw.Core.Abstractions;

public class ToolSandboxException : Exception
{
    public ToolSandboxException(string message)
        : this(message, failureCode: null)
    {
    }

    public ToolSandboxException(string message, string? failureCode)
        : base(message)
    {
        FailureCode = failureCode;
    }

    public ToolSandboxException(string message, Exception innerException)
        : this(message, innerException, failureCode: null)
    {
    }

    public ToolSandboxException(string message, Exception innerException, string? failureCode)
        : base(message, innerException)
    {
        FailureCode = failureCode;
    }

    public string? FailureCode { get; }
}

public sealed class ToolSandboxUnavailableException : ToolSandboxException
{
    public ToolSandboxUnavailableException(string message)
        : base(message)
    {
    }

    public ToolSandboxUnavailableException(string message, Exception innerException)
        : base(message, innerException)
    {
    }
}

namespace OpenClaw.Core.Abstractions;

public class ToolSandboxException : Exception
{
    public ToolSandboxException(string message)
        : base(message)
    {
    }

    public ToolSandboxException(string message, Exception innerException)
        : base(message, innerException)
    {
    }
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


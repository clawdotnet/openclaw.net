using OpenClaw.Core.Models;

namespace OpenClaw.Core.Abstractions;

public interface ISandboxCapableTool
{
    ToolSandboxMode DefaultSandboxMode { get; }

    SandboxExecutionRequest CreateSandboxRequest(string argumentsJson);

    string FormatSandboxResult(string argumentsJson, SandboxResult result);
}


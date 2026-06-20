namespace OpenClaw.Core.Abstractions;

public readonly record struct ReductionContext
{
    public required string ToolName { get; init; }
    public required string ArgumentsJson { get; init; }
    public required string RawOutput { get; init; }
    public required bool IsError { get; init; }
    public required int ExitCode { get; init; }
    public required bool BypassReduction { get; init; }

    public static ReductionContext From(
        string toolName, string argumentsJson, string rawOutput,
        bool isError = false, int exitCode = 0, bool bypassReduction = false)
        => new()
        {
            ToolName = toolName,
            ArgumentsJson = argumentsJson,
            RawOutput = rawOutput,
            IsError = isError,
            ExitCode = exitCode,
            BypassReduction = bypassReduction,
        };
}

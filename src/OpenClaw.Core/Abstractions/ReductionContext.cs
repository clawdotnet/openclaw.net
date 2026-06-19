namespace OpenClaw.Core.Abstractions;

public readonly record struct ReductionContext
{
    public required string ToolName { get; init; }
    public required string ArgumentsJson { get; init; }
    public required string RawOutput { get; init; }
    public required bool IsError { get; init; }
    public required bool BypassReduction { get; init; }

    public static ReductionContext From(
        string toolName, string argumentsJson, string rawOutput,
        bool isError = false, bool bypassReduction = false)
        => new()
        {
            ToolName = toolName,
            ArgumentsJson = argumentsJson,
            RawOutput = rawOutput,
            IsError = isError,
            BypassReduction = bypassReduction,
        };
}

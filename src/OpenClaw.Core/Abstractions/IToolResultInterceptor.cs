namespace OpenClaw.Core.Abstractions;

public interface IToolResultInterceptor
{
    int Order { get; }
    string Name { get; }

    ValueTask<string> InterceptAsync(
        string toolName,
        string argumentsJson,
        string rawOutput,
        CancellationToken ct);
}

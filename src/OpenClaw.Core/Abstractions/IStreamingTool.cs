namespace OpenClaw.Core.Abstractions;

/// <summary>
/// Optional tool interface for producing incremental output in streaming sessions.
/// Non-breaking: tools may implement this in addition to <see cref="ITool"/>.
/// </summary>
public interface IStreamingTool
{
    IAsyncEnumerable<string> ExecuteStreamingAsync(string argumentsJson, CancellationToken ct);
}

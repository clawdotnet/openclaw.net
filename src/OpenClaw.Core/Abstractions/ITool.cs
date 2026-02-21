namespace OpenClaw.Core.Abstractions;

/// <summary>
/// A tool that the agent can invoke. Kept minimal for AOT trimming.
/// </summary>
public interface ITool
{
    string Name { get; }
    string Description { get; }
    
    /// <summary>JSON schema describing the tool's parameters.</summary>
    string ParameterSchema { get; }
    
    /// <summary>Execute the tool with the given JSON arguments.</summary>
    ValueTask<string> ExecuteAsync(string argumentsJson, CancellationToken ct);
}

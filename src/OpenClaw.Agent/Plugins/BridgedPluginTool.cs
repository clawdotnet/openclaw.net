using OpenClaw.Core.Abstractions;
using OpenClaw.Core.Plugins;

namespace OpenClaw.Agent.Plugins;

/// <summary>
/// An ITool implementation that bridges to a tool registered by an OpenClaw
/// TypeScript/JavaScript plugin running in a Node.js child process.
/// </summary>
public sealed class BridgedPluginTool : ITool
{
    private readonly PluginBridgeProcess _bridge;
    private readonly string _pluginId;

    public string Name { get; }
    public string Description { get; }
    public string ParameterSchema { get; }

    /// <summary>Whether this tool is optional (opt-in only).</summary>
    public bool Optional { get; }

    public BridgedPluginTool(
        PluginBridgeProcess bridge,
        string pluginId,
        PluginToolRegistration registration)
    {
        _bridge = bridge;
        _pluginId = pluginId;
        Name = registration.Name;
        Description = registration.Description;
        ParameterSchema = registration.Parameters.GetRawText();
        Optional = registration.Optional;
    }

    public async ValueTask<string> ExecuteAsync(string argumentsJson, CancellationToken ct)
    {
        var result = await _bridge.ExecuteToolAsync(Name, argumentsJson, ct);
        return result;
    }
}

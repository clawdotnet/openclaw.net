using System.Text.Json;
using Microsoft.SemanticKernel;
using Microsoft.SemanticKernel.Functions;
using OpenClaw.Core.Abstractions;

namespace OpenClaw.SemanticKernelAdapter;

/// <summary>
/// Creates OpenClaw tools backed by Semantic Kernel functions.
/// </summary>
public static class SemanticKernelToolFactory
{
    public static IReadOnlyList<ITool> CreateTools(
        Func<CancellationToken, ValueTask<Kernel>> kernelFactory,
        Kernel kernelForDiscovery,
        SemanticKernelInteropOptions? options = null)
    {
        options ??= new SemanticKernelInteropOptions();

        var tools = new List<ITool>();
        var usedNames = new HashSet<string>(StringComparer.Ordinal);

        foreach (var plugin in kernelForDiscovery.Plugins)
        {
            var pluginName = plugin.Name ?? "";
            if (string.IsNullOrWhiteSpace(pluginName))
                continue;

            if (options.AllowedPlugins.Length > 0 && !options.AllowedPlugins.Contains(pluginName, StringComparer.OrdinalIgnoreCase))
                continue;

            foreach (var fn in plugin)
            {
                if (tools.Count >= options.MaxMappedTools)
                    return tools;

                var fnName = fn.Name ?? "";
                if (string.IsNullOrWhiteSpace(fnName))
                    continue;

                if (options.AllowedFunctions.Length > 0 && !options.AllowedFunctions.Contains(fnName, StringComparer.OrdinalIgnoreCase))
                    continue;

                var toolName = SemanticKernelToolName.MakeToolName(options.ToolNamePrefix, pluginName, fnName, options.MaxToolNameLength);
                if (!usedNames.Add(toolName))
                {
                    // Collision: add a stable suffix.
                    toolName = SemanticKernelToolName.MakeToolName(options.ToolNamePrefix, pluginName, fnName + "_" + tools.Count, options.MaxToolNameLength);
                    if (!usedNames.Add(toolName))
                        continue;
                }

                var desc = fn.Description;
                if (string.IsNullOrWhiteSpace(desc))
                    desc = $"Semantic Kernel function {pluginName}.{fnName}";

                var schema = BuildParameterSchema(fn);

                tools.Add(new SemanticKernelFunctionTool(
                    kernelFactory,
                    toolName,
                    pluginName,
                    fnName,
                    desc,
                    schema));
            }
        }

        return tools;
    }

    internal static string BuildParameterSchema(KernelFunction fn)
    {
        // Conservative mapping: represent all params as strings by default.
        var props = new Dictionary<string, object?>(StringComparer.Ordinal);
        var required = new List<string>();

        var parameters = fn.Metadata?.Parameters;
        if (parameters is not null)
        {
            foreach (var p in parameters)
            {
                if (string.IsNullOrWhiteSpace(p.Name))
                    continue;

                props[p.Name] = new Dictionary<string, object?>
                {
                    ["type"] = "string",
                    ["description"] = string.IsNullOrWhiteSpace(p.Description)
                        ? $"Parameter '{p.Name}'"
                        : p.Description
                };

                if (p.IsRequired)
                    required.Add(p.Name);
            }
        }

        var schemaObj = new Dictionary<string, object?>
        {
            ["type"] = "object",
            ["properties"] = props,
            ["additionalProperties"] = false
        };

        if (required.Count > 0)
            schemaObj["required"] = required;

        return JsonSerializer.Serialize(schemaObj);
    }
}

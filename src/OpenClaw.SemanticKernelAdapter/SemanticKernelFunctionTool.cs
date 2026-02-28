using System.Diagnostics;
using System.Text.Json;
using Microsoft.SemanticKernel;
using Microsoft.SemanticKernel.Functions;
using OpenClaw.Core.Abstractions;
using OpenClaw.Core.Observability;

namespace OpenClaw.SemanticKernelAdapter;

/// <summary>
/// Exposes a single SK function as an OpenClaw tool.
/// </summary>
public sealed class SemanticKernelFunctionTool : ITool
{
    private readonly Func<CancellationToken, ValueTask<Kernel>> _kernelFactory;
    private readonly string _pluginName;
    private readonly string _functionName;
    private readonly string _toolName;
    private readonly string _description;
    private readonly string _parameterSchema;

    internal SemanticKernelFunctionTool(
        Func<CancellationToken, ValueTask<Kernel>> kernelFactory,
        string toolName,
        string pluginName,
        string functionName,
        string description,
        string parameterSchema)
    {
        _kernelFactory = kernelFactory;
        _toolName = toolName;
        _pluginName = pluginName;
        _functionName = functionName;
        _description = description;
        _parameterSchema = parameterSchema;
    }

    public string Name => _toolName;
    public string Description => _description;
    public string ParameterSchema => _parameterSchema;

    public async ValueTask<string> ExecuteAsync(string argumentsJson, CancellationToken ct)
    {
        using var activity = Telemetry.ActivitySource.StartActivity("Tool.SemanticKernel.Invoke");
        activity?.SetTag("sk.plugin", _pluginName);
        activity?.SetTag("sk.function", _functionName);
        activity?.SetTag("sk.tool_name", _toolName);

        try
        {
            var kernel = await _kernelFactory(ct);
            KernelFunction fn = kernel.Plugins.GetFunction(_pluginName, _functionName);

            var args = new KernelArguments();
            using var doc = JsonDocument.Parse(argumentsJson);
            foreach (var prop in doc.RootElement.EnumerateObject())
            {
                args[prop.Name] = prop.Value.ValueKind switch
                {
                    JsonValueKind.String => prop.Value.GetString(),
                    JsonValueKind.Number => prop.Value.ToString(),
                    JsonValueKind.True => "true",
                    JsonValueKind.False => "false",
                    JsonValueKind.Null => null,
                    _ => prop.Value.ToString()
                };
            }

            Activity.Current?.SetTag("sk.plugin", _pluginName);
            Activity.Current?.SetTag("sk.function", _functionName);
            Activity.Current?.SetTag("sk.tool_name", _toolName);

            var result = await kernel.InvokeAsync(fn, args, cancellationToken: ct);
            return result?.ToString() ?? "";
        }
        catch (Exception ex)
        {
            activity?.SetTag("error.type", ex.GetType().Name);
            activity?.SetTag("error.message", ex.Message);
            return $"Error: Semantic Kernel invocation failed ({ex.GetType().Name}).";
        }
    }
}

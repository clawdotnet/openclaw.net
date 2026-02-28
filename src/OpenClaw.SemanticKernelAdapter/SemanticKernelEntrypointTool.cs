using System.Diagnostics;
using System.Text.Json;
using Microsoft.SemanticKernel;
using OpenClaw.Core.Abstractions;
using OpenClaw.Core.Observability;

namespace OpenClaw.SemanticKernelAdapter;

/// <summary>
/// A single OpenClaw tool that can invoke any SK function identified by (plugin,function).
/// </summary>
public sealed class SemanticKernelEntrypointTool : ITool
{
    private readonly Func<CancellationToken, ValueTask<Kernel>> _kernelFactory;

    public SemanticKernelEntrypointTool(Func<CancellationToken, ValueTask<Kernel>> kernelFactory)
        => _kernelFactory = kernelFactory;

    public string Name => "semantic_kernel";

    public string Description =>
        "Invoke a Semantic Kernel function by plugin and function name. " +
        "Use this when you want SK orchestration behind OpenClaw tool policies.";

    public string ParameterSchema => """
        {
          "type": "object",
          "properties": {
            "plugin": { "type": "string", "description": "SK plugin name" },
            "function": { "type": "string", "description": "SK function name" },
            "args": { "type": "object", "description": "Arguments object passed to the SK function", "default": {} },
            "format": { "type": "string", "enum": ["text","json"], "default": "text" },
            "timeout_seconds": { "type": "integer", "minimum": 1, "maximum": 600 }
          },
          "required": ["plugin","function"]
        }
        """;

    public async ValueTask<string> ExecuteAsync(string argumentsJson, CancellationToken ct)
    {
        using var activity = Telemetry.ActivitySource.StartActivity("Tool.SemanticKernel.Invoke");
        activity?.SetTag("sk.tool_name", Name);

        var plugin = "";
        var function = "";
        var format = "text";

        try
        {
            using var doc = JsonDocument.Parse(argumentsJson);
            var root = doc.RootElement;

            if (!root.TryGetProperty("plugin", out var pluginEl) || pluginEl.ValueKind != JsonValueKind.String)
                return "Error: 'plugin' is required.";
            if (!root.TryGetProperty("function", out var fnEl) || fnEl.ValueKind != JsonValueKind.String)
                return "Error: 'function' is required.";

            plugin = pluginEl.GetString() ?? "";
            function = fnEl.GetString() ?? "";
            if (string.IsNullOrWhiteSpace(plugin) || string.IsNullOrWhiteSpace(function))
                return "Error: 'plugin' and 'function' are required.";

            format = root.TryGetProperty("format", out var fmtEl) && fmtEl.ValueKind == JsonValueKind.String
                ? (fmtEl.GetString() ?? "text")
                : "text";

            var timeoutSec = root.TryGetProperty("timeout_seconds", out var tEl) && tEl.ValueKind == JsonValueKind.Number
                ? tEl.GetInt32()
                : 0;

            using var timeoutCts = timeoutSec > 0
                ? CancellationTokenSource.CreateLinkedTokenSource(ct)
                : null;
            if (timeoutCts is not null)
                timeoutCts.CancelAfter(TimeSpan.FromSeconds(Math.Clamp(timeoutSec, 1, 600)));
            var effectiveCt = timeoutCts?.Token ?? ct;

            activity?.SetTag("sk.plugin", plugin);
            activity?.SetTag("sk.function", function);

            var kernel = await _kernelFactory(effectiveCt);

            // Resolve the function by name.
            var kfn = kernel.Plugins.GetFunction(plugin, function);

            var args = new KernelArguments();
            if (root.TryGetProperty("args", out var argsEl) && argsEl.ValueKind == JsonValueKind.Object)
            {
                foreach (var prop in argsEl.EnumerateObject())
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
            }

            // Tag the parent activity as well when available.
            Activity.Current?.SetTag("sk.plugin", plugin);
            Activity.Current?.SetTag("sk.function", function);
            Activity.Current?.SetTag("sk.tool_name", Name);

            var result = await kernel.InvokeAsync(kfn, args, cancellationToken: effectiveCt);
            var text = result?.ToString() ?? "";

            if (!string.Equals(format, "json", StringComparison.OrdinalIgnoreCase))
                return text;

            return JsonSerializer.Serialize(new
            {
                ok = true,
                plugin,
                function,
                result = text,
                error = (string?)null
            });
        }
        catch (Exception ex)
        {
            activity?.SetTag("error.type", ex.GetType().Name);
            activity?.SetTag("error.message", ex.Message);

            if (!string.Equals(format, "json", StringComparison.OrdinalIgnoreCase))
                return $"Error: Semantic Kernel invocation failed ({ex.GetType().Name}).";

            return JsonSerializer.Serialize(new
            {
                ok = false,
                plugin,
                function,
                result = (string?)null,
                error = ex.Message
            });
        }
    }
}

using System.Text.Json;
using OpenClaw.Core.Abstractions;
using OpenClaw.Core.Memory;
using OpenClaw.Core.Models;

namespace OpenClaw.Agent.Tools;

public sealed class FractalMemoryWorkflowTool(
    IStructuredMemoryWorkflowProvider provider,
    FractalMemoryConfig config,
    FractalMemoryWorkflow workflow) : ITool, IToolActionDescriptorProvider
{
    public string Name => workflow.ToolName;
    public string Description => workflow.Description;
    public string ParameterSchema => workflow.ParameterSchema;

    public async ValueTask<string> ExecuteAsync(string argumentsJson, CancellationToken ct)
    {
        try
        {
            using var doc = FractalMemoryToolHelpers.Parse(argumentsJson);
            if (workflow.Validate(doc.RootElement) is { } error)
                return FractalMemoryToolHelpers.Error(error);
            if (workflow.IsMutation(doc.RootElement) && !config.AllowWrites)
                return FractalMemoryToolHelpers.Error("Fractal Memory writes are disabled by configuration.");

            var result = await provider.ExecuteWorkflowAsync(workflow.Operation, doc.RootElement, ct);
            return JsonSerializer.Serialize(result, CoreJsonContext.Default.StructuredMemoryWorkflowResult);
        }
        catch (JsonException)
        {
            return FractalMemoryToolHelpers.Error("Arguments must be a valid JSON object.");
        }
    }

    public ToolActionDescriptor ResolveActionDescriptor(string argumentsJson)
    {
        try
        {
            using var doc = FractalMemoryToolHelpers.Parse(argumentsJson);
            if (workflow.Validate(doc.RootElement) is null && !workflow.IsMutation(doc.RootElement))
                return new ToolActionDescriptor
                {
                    Action = workflow.Operation,
                    ReadOnly = true,
                    IsMutation = false,
                    RequiresApproval = false,
                    RiskLevel = "low",
                    Summary = Description
                };
        }
        catch (JsonException)
        {
            // Invalid requests never receive a read-only descriptor.
        }

        return FractalMemoryToolHelpers.BuildWriteDescriptor(Name, workflow.Operation, argumentsJson, config.RequireApprovalForWrites);
    }

    public static IEnumerable<ITool> CreateTools(IStructuredMemoryWorkflowProvider provider, FractalMemoryConfig config)
        => FractalMemoryWorkflows.All
            .Where(workflow => config.Enabled && (!workflow.AlwaysWrites || config.AllowWrites))
            .Select(workflow => new FractalMemoryWorkflowTool(provider, config, workflow));
}

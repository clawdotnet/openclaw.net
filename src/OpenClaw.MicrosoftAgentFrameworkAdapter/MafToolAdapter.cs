using System.Text.Json;
using Microsoft.Extensions.AI;
using OpenClaw.Agent;
using OpenClaw.Core.Abstractions;
using OpenClaw.Core.Models;

namespace OpenClaw.MicrosoftAgentFrameworkAdapter;

public sealed class MafToolAdapter : AIFunction
{
    private readonly ITool _tool;
    private readonly OpenClawToolExecutor _toolExecutor;
    private readonly JsonElement _parameterSchema;

    public MafToolAdapter(ITool tool, OpenClawToolExecutor toolExecutor)
    {
        _tool = tool;
        _toolExecutor = toolExecutor;
        using var schemaDocument = JsonDocument.Parse(tool.ParameterSchema);
        _parameterSchema = schemaDocument.RootElement.Clone();
    }

    public override string Name => _tool.Name;

    public override string Description => _tool.Description;

    public override JsonElement JsonSchema => _parameterSchema;

    public override JsonSerializerOptions JsonSerializerOptions => CoreJsonContext.Default.Options;

    protected override async ValueTask<object?> InvokeCoreAsync(AIFunctionArguments arguments, CancellationToken cancellationToken)
    {
        var executionContext = MafExecutionContextScope.Current;
        var argsJson = JsonSerializer.Serialize(
            arguments.ToDictionary(static entry => entry.Key, static entry => entry.Value),
            CoreJsonContext.Default.DictionaryStringObject);
        var streamEventWriter = executionContext.StreamEventWriter;

        if (streamEventWriter is not null)
            await streamEventWriter(AgentStreamEvent.ToolStarted(_tool.Name, argsJson), cancellationToken);

        ToolExecutionResult result = await _toolExecutor.ExecuteAsync(
            _tool.Name,
            argsJson,
            callId: null,
            executionContext.Session,
            executionContext.TurnContext,
            isStreaming: streamEventWriter is not null,
            executionContext.ApprovalCallback,
            cancellationToken,
            onDelta: streamEventWriter is null
                ? null
                : async chunk => await streamEventWriter(AgentStreamEvent.ToolDelta(_tool.Name, chunk), cancellationToken));

        executionContext.ToolInvocations.Add(result.Invocation);

        if (streamEventWriter is not null)
            await streamEventWriter(AgentStreamEvent.ToolCompleted(_tool.Name, result.ResultText), cancellationToken);

        return result.ResultText;
    }
}

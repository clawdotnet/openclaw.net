using System.Runtime.CompilerServices;
using System.Text.Json;
using System.Threading.Channels;
using Microsoft.Extensions.AI;
using OpenClaw.Core.Models;
using OpenClaw.Core.Observability;
using OpenClaw.Core.Security;

namespace OpenClaw.Agent;

internal sealed class AgentToolCallLoop(OpenClawToolExecutor toolExecutor, bool parallelToolExecution)
{
    public async Task<AgentToolBatchExecution> ExecuteToolCallsAsync(
        List<FunctionCallContent> toolCalls,
        Session session,
        TurnContext turnCtx,
        bool isStreaming,
        ToolApprovalCallback? approvalCallback,
        CancellationToken ct,
        McpCallerCredentialContext? callerCredentialContext = null)
    {
        if (parallelToolExecution && toolCalls.Count > 1)
            return await ExecuteToolCallsParallelAsync(toolCalls, session, turnCtx, isStreaming, approvalCallback, ct, callerCredentialContext);

        return await ExecuteToolCallsSequentialAsync(toolCalls, session, turnCtx, isStreaming, approvalCallback, ct, callerCredentialContext);
    }

    public async IAsyncEnumerable<AgentToolLoopUpdate> ExecuteStreamingToolCallsAsync(
        List<FunctionCallContent> toolCalls,
        Session session,
        TurnContext turnCtx,
        ToolApprovalCallback? approvalCallback,
        [EnumeratorCancellation] CancellationToken ct,
        McpCallerCredentialContext? callerCredentialContext = null)
    {
        var hasStreamingTool = toolCalls.Any(c => toolExecutor.SupportsStreaming(c.Name));

        if (hasStreamingTool)
        {
            var invocations = new List<ToolInvocation>(toolCalls.Count);
            var toolResults = new List<FunctionResultContent>(toolCalls.Count);

            foreach (var call in toolCalls)
            {
                var argsJson = SerializeToolArgumentsForEvent(call.Arguments);
                yield return AgentToolLoopUpdate.Event(AgentStreamEvent.ToolStarted(call.Name, argsJson));

                var channel = Channel.CreateBounded<string>(new BoundedChannelOptions(256)
                {
                    SingleReader = true,
                    SingleWriter = true,
                    FullMode = BoundedChannelFullMode.Wait
                });

                async Task<(ToolExecutionResult, FunctionResultContent)> RunToolAsync()
                {
                    try
                    {
                        var execution = await toolExecutor.ExecuteAsync(
                            call,
                            session,
                            turnCtx,
                            isStreaming: true,
                            approvalCallback,
                            ct,
                            onDelta: async chunk => await channel.Writer.WriteAsync(chunk, ct),
                            toolCallCount: toolCalls.Count,
                            callerCredentialContext: callerCredentialContext);
                        return (execution, execution.ToFunctionResultContent(call.CallId));
                    }
                    finally
                    {
                        channel.Writer.TryComplete();
                    }
                }

                var task = RunToolAsync();

                await foreach (var chunk in channel.Reader.ReadAllAsync(ct))
                    yield return AgentToolLoopUpdate.Event(AgentStreamEvent.ToolDelta(call.Name, chunk));

                var (execution, res) = await task;
                invocations.Add(execution.Invocation);
                toolResults.Add(res);

                yield return AgentToolLoopUpdate.Event(AgentStreamEvent.ToolCompleted(
                    execution.Invocation.ToolName,
                    execution.ResultText,
                    resultStatus: execution.ResultStatus,
                    failureCode: execution.FailureCode,
                    failureMessage: execution.FailureMessage,
                    nextStep: execution.NextStep));
            }

            yield return AgentToolLoopUpdate.Completed(new AgentToolBatchExecution(invocations, toolResults));
            yield break;
        }

        if (parallelToolExecution && toolCalls.Count > 1)
        {
            foreach (var call in toolCalls)
            {
                var argsJson = SerializeToolArgumentsForEvent(call.Arguments);
                yield return AgentToolLoopUpdate.Event(AgentStreamEvent.ToolStarted(call.Name, argsJson));
            }

            var batch = await ExecuteToolCallsAsync(toolCalls, session, turnCtx, isStreaming: true, approvalCallback, ct, callerCredentialContext);

            foreach (var inv in batch.Invocations)
                yield return AgentToolLoopUpdate.Event(CreateToolCompletedEvent(inv));

            yield return AgentToolLoopUpdate.Completed(batch);
            yield break;
        }

        var sequentialInvocations = new List<ToolInvocation>(toolCalls.Count);
        var sequentialResults = new List<FunctionResultContent>(toolCalls.Count);

        foreach (var call in toolCalls)
        {
            var argsJson = SerializeToolArgumentsForEvent(call.Arguments);
            yield return AgentToolLoopUpdate.Event(AgentStreamEvent.ToolStarted(call.Name, argsJson));

            var (invocation, result) = await ExecuteSingleToolCallAsync(
                call, session, turnCtx, isStreaming: true, approvalCallback, ct, onDelta: null, toolCallCount: toolCalls.Count, callerCredentialContext);
            sequentialInvocations.Add(invocation);
            sequentialResults.Add(result);

            yield return AgentToolLoopUpdate.Event(CreateToolCompletedEvent(invocation));
        }

        yield return AgentToolLoopUpdate.Completed(new AgentToolBatchExecution(sequentialInvocations, sequentialResults));
    }

    public static AgentStreamEvent CreateToolCompletedEvent(ToolInvocation invocation) =>
        AgentStreamEvent.ToolCompleted(
            invocation.ToolName,
            invocation.Result ?? "",
            resultStatus: string.IsNullOrWhiteSpace(invocation.ResultStatus)
                ? ToolResultStatuses.Completed
                : invocation.ResultStatus!,
            failureCode: invocation.FailureCode,
            failureMessage: invocation.FailureMessage,
            nextStep: invocation.NextStep);

    private async Task<AgentToolBatchExecution> ExecuteToolCallsSequentialAsync(
        List<FunctionCallContent> toolCalls,
        Session session,
        TurnContext turnCtx,
        bool isStreaming,
        ToolApprovalCallback? approvalCallback,
        CancellationToken ct,
        McpCallerCredentialContext? callerCredentialContext)
    {
        var invocations = new List<ToolInvocation>(toolCalls.Count);
        var toolResults = new List<FunctionResultContent>(toolCalls.Count);

        foreach (var call in toolCalls)
        {
            var (invocation, result) = await ExecuteSingleToolCallAsync(call, session, turnCtx, isStreaming, approvalCallback, ct, onDelta: null, toolCallCount: toolCalls.Count, callerCredentialContext);
            invocations.Add(invocation);
            toolResults.Add(result);
        }

        return new AgentToolBatchExecution(invocations, toolResults);
    }

    private async Task<AgentToolBatchExecution> ExecuteToolCallsParallelAsync(
        List<FunctionCallContent> toolCalls,
        Session session,
        TurnContext turnCtx,
        bool isStreaming,
        ToolApprovalCallback? approvalCallback,
        CancellationToken ct,
        McpCallerCredentialContext? callerCredentialContext)
    {
        using var linkedCts = CancellationTokenSource.CreateLinkedTokenSource(ct);

        var tasks = toolCalls.Select(async call =>
        {
            try
            {
                return await ExecuteSingleToolCallAsync(call, session, turnCtx, isStreaming, approvalCallback, linkedCts.Token, onDelta: null, toolCallCount: toolCalls.Count, callerCredentialContext);
            }
            catch (Exception)
            {
                linkedCts.Cancel();
                throw;
            }
        }).ToArray();

        (ToolInvocation, FunctionResultContent)[] results;
        try
        {
            results = await Task.WhenAll(tasks);
        }
        catch (OperationCanceledException) when (linkedCts.IsCancellationRequested && !ct.IsCancellationRequested)
        {
            results = await Task.WhenAll(tasks);
        }

        var invocations = new List<ToolInvocation>(results.Length);
        var toolResults = new List<FunctionResultContent>(results.Length);

        foreach (var (invocation, result) in results)
        {
            invocations.Add(invocation);
            toolResults.Add(result);
        }

        return new AgentToolBatchExecution(invocations, toolResults);
    }

    private async Task<(ToolInvocation, FunctionResultContent)> ExecuteSingleToolCallAsync(
        FunctionCallContent call,
        Session session,
        TurnContext turnCtx,
        bool isStreaming,
        ToolApprovalCallback? approvalCallback,
        CancellationToken ct,
        Func<string, ValueTask>? onDelta,
        int toolCallCount,
        McpCallerCredentialContext? callerCredentialContext)
    {
        var result = await toolExecutor.ExecuteAsync(
            call,
            session,
            turnCtx,
            isStreaming,
            approvalCallback,
            ct,
            onDelta,
            toolCallCount,
            callerCredentialContext: callerCredentialContext);

        return (result.Invocation, result.ToFunctionResultContent(call.CallId));
    }

    private static string SerializeToolArgumentsForEvent(IDictionary<string, object?>? arguments)
    {
        if (arguments is null || arguments.Count == 0)
            return "{}";

        try
        {
            return JsonSerializer.Serialize(arguments, CoreJsonContext.Default.IDictionaryStringObject);
        }
        catch (Exception ex) when (ex is JsonException or NotSupportedException or InvalidOperationException)
        {
            return "{}";
        }
    }
}

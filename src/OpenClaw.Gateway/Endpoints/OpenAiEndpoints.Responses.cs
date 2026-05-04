using System.Text.Json;
using OpenClaw.Agent;
using OpenClaw.Core.Middleware;
using OpenClaw.Core.Models;
using OpenClaw.Gateway.Bootstrap;
using OpenClaw.Gateway.Composition;

namespace OpenClaw.Gateway.Endpoints;

internal static partial class OpenAiEndpoints
{
    private static void MapResponsesEndpoint(
        WebApplication app,
        GatewayStartupContext startup,
        GatewayAppRuntime runtime)
    {
        app.MapPost("/v1/responses", async (HttpContext ctx) =>
        {
            if (!EndpointHelpers.IsAuthorizedRequest(ctx, startup.Config, startup.IsNonLoopbackBind))
            {
                ctx.Response.StatusCode = StatusCodes.Status401Unauthorized;
                return;
            }

            if (!runtime.Operations.ActorRateLimits.TryConsume("ip", EndpointHelpers.GetRemoteIpKey(ctx), "openai_http", out var blockedByPolicyId))
            {
                ctx.Response.StatusCode = StatusCodes.Status429TooManyRequests;
                await ctx.Response.WriteAsync($"Rate limit exceeded by policy '{blockedByPolicyId}'.", ctx.RequestAborted);
                return;
            }

            OpenAiResponseRequest? req;
            try
            {
                req = await JsonSerializer.DeserializeAsync(
                    ctx.Request.Body,
                    CoreJsonContext.Default.OpenAiResponseRequest,
                    ctx.RequestAborted);
            }
            catch
            {
                ctx.Response.StatusCode = StatusCodes.Status400BadRequest;
                await ctx.Response.WriteAsync("Invalid JSON request body.", ctx.RequestAborted);
                return;
            }

            if (req is null || string.IsNullOrWhiteSpace(req.Input))
            {
                ctx.Response.StatusCode = StatusCodes.Status400BadRequest;
                await ctx.Response.WriteAsync("Request must include an 'input' field.", ctx.RequestAborted);
                return;
            }

            var requesterKey = EndpointHelpers.GetHttpRateLimitKey(ctx, startup.Config);
            if (!TryGetOptionalStableSessionId(ctx, out var stableSessionId, out var stableSessionError))
            {
                ctx.Response.StatusCode = StatusCodes.Status400BadRequest;
                await ctx.Response.WriteAsync(stableSessionError ?? "Invalid stable session id.", ctx.RequestAborted);
                return;
            }
            var stableBinding = !string.IsNullOrWhiteSpace(stableSessionId)
                ? CreateStableSessionBinding(stableSessionId!, requesterKey)
                : null;
            var requestId = $"oai-resp:{Guid.NewGuid():N}";
            var session = stableBinding is not null
                ? await runtime.SessionManager.GetOrCreateByIdAsync(BuildScopedStableSessionId(stableBinding), "openai-responses", requesterKey, ctx.RequestAborted)
                : await runtime.SessionManager.GetOrCreateAsync("openai-responses", requestId, ctx.RequestAborted);
            IAsyncDisposable? stableSessionLock = null;
            var discardStableSessionOnExit = false;
            ToolApprovalCallback? approvalCallback = null;

            try
            {
                if (stableBinding is not null)
                {
                    stableSessionLock = await runtime.SessionManager.AcquireSessionLockAsync(session.Id, ctx.RequestAborted);
                    if (!TryEnsureStableSessionBinding(session, stableBinding, out var bindingError))
                    {
                        ctx.Response.StatusCode = StatusCodes.Status409Conflict;
                        await ctx.Response.WriteAsync(bindingError ?? "Stable session binding is inconsistent with the current requester scope.", ctx.RequestAborted);
                        return;
                    }
                }

                var httpMwCtx = new MessageContext
                {
                    ChannelId = "openai-responses",
                    SenderId = requesterKey,
                    SessionId = session.Id,
                    Text = req.Input,
                    SessionInputTokens = session.TotalInputTokens,
                    SessionOutputTokens = session.TotalOutputTokens
                };
                var allow = await runtime.MiddlewarePipeline.ExecuteAsync(httpMwCtx, ctx.RequestAborted);
                if (!allow)
                {
                    ctx.Response.StatusCode = StatusCodes.Status429TooManyRequests;
                    await ctx.Response.WriteAsync(httpMwCtx.ShortCircuitResponse ?? "Request blocked.", ctx.RequestAborted);
                    return;
                }
                if (req.Model is not null)
                {
                    if (runtime.Operations.ModelProfiles.TryGet(req.Model, out _))
                    {
                        session.ModelProfileId = req.Model;
                        session.ModelOverride = null;
                    }
                    else
                    {
                        session.ModelOverride = req.Model;
                        session.ModelProfileId = null;
                    }
                }
                var responsesPresetHeader = ctx.Request.Headers.TryGetValue("X-OpenClaw-Preset", out var responsesPresetValues)
                    ? responsesPresetValues.ToString()
                    : null;
                if (!string.IsNullOrWhiteSpace(responsesPresetHeader))
                {
                    runtime.Operations.SessionMetadata.Set(session.Id, new SessionMetadataUpdateRequest
                    {
                        ActivePresetId = responsesPresetHeader.Trim()
                    });
                }
                ApplyImplicitToolPolicy(session, runtime, responsesPresetHeader);
                approvalCallback = ToolApprovalCallbackFactory.Create(
                    startup.Config,
                    runtime,
                    session,
                    approvalChannelId: "openai-http",
                    senderId: requesterKey);

                var responseId = $"resp-{Guid.NewGuid():N}"[..24];
                var createdAt = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
                var model = req.Model ?? startup.Config.Llm.Model;
                var historyStartIndex = session.History.Count;

                if (req.Stream)
                {
                    ctx.Response.ContentType = "text/event-stream";
                    ctx.Response.Headers.CacheControl = "no-cache";
                    ctx.Response.Headers.Connection = "keep-alive";

                    var responseOutputItems = new List<OpenAiResponseStreamItem>();
                    var activeToolCalls = new Dictionary<string, Queue<ResponsesToolState>>(StringComparer.Ordinal);
                    var nextOutputIndex = 0;
                    var nextToolCallIndex = 0;
                    var nextSequenceNumber = 0;
                    var responseTerminated = false;
                    ResponsesTextState? textState = null;

                    int NextSequenceNumber() => ++nextSequenceNumber;

                    Task WriteResponsesEventAsync(string eventType, string json)
                        => ctx.Response.WriteAsync($"event: {eventType}\ndata: {json}\n\n", ctx.RequestAborted);

                    ResponsesToolState RegisterToolCall(string toolName, string toolArguments)
                    {
                        var itemId = $"fc_{Guid.NewGuid():N}"[..20];
                        var state = new ResponsesToolState(
                            nextOutputIndex++,
                            itemId,
                            $"call_openclaw_{++nextToolCallIndex}",
                            toolName,
                            string.IsNullOrWhiteSpace(toolArguments) ? "{}" : toolArguments);
                        if (!activeToolCalls.TryGetValue(toolName, out var queue))
                        {
                            queue = new Queue<ResponsesToolState>();
                            activeToolCalls[toolName] = queue;
                        }

                        queue.Enqueue(state);
                        responseOutputItems.Add(CreateFunctionCallItem(state, status: "in_progress"));
                        return state;
                    }

                    ResponsesToolState GetOrCreateToolCall(string toolName, string? toolArguments)
                    {
                        if (activeToolCalls.TryGetValue(toolName, out var queue) && queue.Count > 0)
                            return queue.Peek();

                        return RegisterToolCall(toolName, toolArguments ?? "{}");
                    }

                    void CompleteToolCall(ResponsesToolState state)
                    {
                        if (!activeToolCalls.TryGetValue(state.ToolName, out var queue) || queue.Count == 0)
                            return;

                        queue.Dequeue();
                        if (queue.Count == 0)
                            activeToolCalls.Remove(state.ToolName);
                    }

                    async Task EnsureToolResultItemAddedAsync(ResponsesToolState state)
                    {
                        if (state.ResultOutputIndex is not null && state.ResultItemId is not null)
                            return;

                        state.ResultOutputIndex = nextOutputIndex++;
                        state.ResultItemId = $"fco_{Guid.NewGuid():N}"[..21];
                        var item = CreateFunctionCallOutputItem(state, state.ResultOutput.ToString(), status: "in_progress");
                        responseOutputItems.Add(item);

                        var addedEvent = new OpenAiResponseOutputItemAddedEvent
                        {
                            SequenceNumber = NextSequenceNumber(),
                            ResponseId = responseId,
                            OutputIndex = state.ResultOutputIndex.Value,
                            Item = item
                        };
                        await WriteResponsesEventAsync(
                            addedEvent.Type,
                            JsonSerializer.Serialize(addedEvent, CoreJsonContext.Default.OpenAiResponseOutputItemAddedEvent));
                    }

                    async Task<ResponsesTextState> EnsureTextStateAsync()
                    {
                        if (textState is not null)
                            return textState;

                        var itemId = $"msg_{Guid.NewGuid():N}"[..23];
                        var state = new ResponsesTextState(nextOutputIndex++, itemId);
                        var item = CreateMessageItem(itemId, state.Content.ToString(), status: "in_progress");
                        responseOutputItems.Add(item);

                        var addedEvent = new OpenAiResponseOutputItemAddedEvent
                        {
                            SequenceNumber = NextSequenceNumber(),
                            ResponseId = responseId,
                            OutputIndex = state.OutputIndex,
                            Item = item
                        };
                        await WriteResponsesEventAsync(
                            addedEvent.Type,
                            JsonSerializer.Serialize(addedEvent, CoreJsonContext.Default.OpenAiResponseOutputItemAddedEvent));

                        var partAddedEvent = new OpenAiResponseContentPartAddedEvent
                        {
                            SequenceNumber = NextSequenceNumber(),
                            ResponseId = responseId,
                            OutputIndex = state.OutputIndex,
                            ItemId = state.ItemId,
                            ContentIndex = 0,
                            Part = new OpenAiResponseContent { Text = "" }
                        };
                        await WriteResponsesEventAsync(
                            partAddedEvent.Type,
                            JsonSerializer.Serialize(partAddedEvent, CoreJsonContext.Default.OpenAiResponseContentPartAddedEvent));
                        await ctx.Response.Body.FlushAsync(ctx.RequestAborted);
                        textState = state;
                        return state;
                    }

                    OpenAiUsage BuildUsage()
                        => new()
                        {
                            PromptTokens = (int)session.TotalInputTokens,
                            CompletionTokens = (int)session.TotalOutputTokens,
                            TotalTokens = (int)(session.TotalInputTokens + session.TotalOutputTokens)
                        };

                    async Task FinalizeTextStateAsync(string status)
                    {
                        if (textState is null)
                            return;

                        var fullText = textState.Content.ToString();
                        var finalizedMessageItem = CreateMessageItem(textState.ItemId, fullText, status: status);
                        responseOutputItems[textState.OutputIndex] = finalizedMessageItem;

                        var textDoneEvent = new OpenAiResponseOutputTextDoneEvent
                        {
                            SequenceNumber = NextSequenceNumber(),
                            ResponseId = responseId,
                            OutputIndex = textState.OutputIndex,
                            ItemId = textState.ItemId,
                            ContentIndex = 0,
                            Text = fullText
                        };
                        await WriteResponsesEventAsync(
                            textDoneEvent.Type,
                            JsonSerializer.Serialize(textDoneEvent, CoreJsonContext.Default.OpenAiResponseOutputTextDoneEvent));

                        var partDoneEvent = new OpenAiResponseContentPartDoneEvent
                        {
                            SequenceNumber = NextSequenceNumber(),
                            ResponseId = responseId,
                            OutputIndex = textState.OutputIndex,
                            ItemId = textState.ItemId,
                            ContentIndex = 0,
                            Part = new OpenAiResponseContent { Text = fullText }
                        };
                        await WriteResponsesEventAsync(
                            partDoneEvent.Type,
                            JsonSerializer.Serialize(partDoneEvent, CoreJsonContext.Default.OpenAiResponseContentPartDoneEvent));

                        var itemDoneEvent = new OpenAiResponseOutputItemDoneEvent
                        {
                            SequenceNumber = NextSequenceNumber(),
                            ResponseId = responseId,
                            OutputIndex = textState.OutputIndex,
                            Item = finalizedMessageItem
                        };
                        await WriteResponsesEventAsync(
                            itemDoneEvent.Type,
                            JsonSerializer.Serialize(itemDoneEvent, CoreJsonContext.Default.OpenAiResponseOutputItemDoneEvent));
                    }

                    async Task WriteFailedResponseAsync(string errorMessage, string? errorCode)
                    {
                        if (responseTerminated)
                            return;

                        await FinalizeTextStateAsync("incomplete");

                        var failedEvent = new OpenAiResponseFailedEvent
                        {
                            SequenceNumber = NextSequenceNumber(),
                            Response = new OpenAiResponseStreamResponse
                            {
                                Id = responseId,
                                CreatedAt = createdAt,
                                Model = model,
                                Status = "failed",
                                Output = [.. responseOutputItems],
                                Usage = BuildUsage(),
                                Error = CreateResponseError(errorCode, errorMessage)
                            }
                        };
                        await WriteResponsesEventAsync(
                            failedEvent.Type,
                            JsonSerializer.Serialize(failedEvent, CoreJsonContext.Default.OpenAiResponseFailedEvent));
                        await ctx.Response.Body.FlushAsync(ctx.RequestAborted);
                        responseTerminated = true;
                    }

                    var createdEvent = new OpenAiResponseCreatedEvent
                    {
                        SequenceNumber = NextSequenceNumber(),
                        Response = new OpenAiResponseStreamResponse
                        {
                            Id = responseId,
                            CreatedAt = createdAt,
                            Model = model,
                            Status = "in_progress",
                            Output = []
                        }
                    };
                    await WriteResponsesEventAsync(
                        createdEvent.Type,
                        JsonSerializer.Serialize(createdEvent, CoreJsonContext.Default.OpenAiResponseCreatedEvent));

                    var inProgressEvent = new OpenAiResponseInProgressEvent
                    {
                        SequenceNumber = NextSequenceNumber(),
                        Response = new OpenAiResponseStreamResponse
                        {
                            Id = responseId,
                            CreatedAt = createdAt,
                            Model = model,
                            Status = "in_progress",
                            Output = []
                        }
                    };
                    await WriteResponsesEventAsync(
                        inProgressEvent.Type,
                        JsonSerializer.Serialize(inProgressEvent, CoreJsonContext.Default.OpenAiResponseInProgressEvent));
                    await ctx.Response.Body.FlushAsync(ctx.RequestAborted);

                    try
                    {
                        await foreach (var evt in runtime.AgentRuntime.RunStreamingAsync(
                            session,
                            req.Input,
                            ctx.RequestAborted,
                            approvalCallback: approvalCallback))
                        {
                            if (evt.Type == AgentStreamEventType.TextDelta && !string.IsNullOrEmpty(evt.Content))
                            {
                                var state = await EnsureTextStateAsync();
                                state.Content.Append(evt.Content);

                                var deltaEvent = new OpenAiResponseOutputTextDeltaEvent
                                {
                                    SequenceNumber = NextSequenceNumber(),
                                    ResponseId = responseId,
                                    OutputIndex = state.OutputIndex,
                                    ItemId = state.ItemId,
                                    ContentIndex = 0,
                                    Delta = evt.Content
                                };
                                await WriteResponsesEventAsync(
                                    deltaEvent.Type,
                                    JsonSerializer.Serialize(deltaEvent, CoreJsonContext.Default.OpenAiResponseOutputTextDeltaEvent));
                                await ctx.Response.Body.FlushAsync(ctx.RequestAborted);
                            }
                            else if (evt.Type == AgentStreamEventType.ToolStart && !string.IsNullOrWhiteSpace(evt.ToolName))
                            {
                                var toolState = RegisterToolCall(evt.ToolName, evt.ToolArguments ?? "{}");
                                var addedEvent = new OpenAiResponseOutputItemAddedEvent
                                {
                                    SequenceNumber = NextSequenceNumber(),
                                    ResponseId = responseId,
                                    OutputIndex = toolState.OutputIndex,
                                    Item = CreateFunctionCallItem(toolState, status: "in_progress")
                                };
                                await WriteResponsesEventAsync(
                                    addedEvent.Type,
                                    JsonSerializer.Serialize(addedEvent, CoreJsonContext.Default.OpenAiResponseOutputItemAddedEvent));

                                foreach (var chunk in SplitArguments(toolState.Arguments))
                                {
                                    var deltaEvent = new OpenAiResponseFunctionCallArgumentsDeltaEvent
                                    {
                                        SequenceNumber = NextSequenceNumber(),
                                        ResponseId = responseId,
                                        OutputIndex = toolState.OutputIndex,
                                        ItemId = toolState.ItemId,
                                        Delta = chunk
                                    };
                                    await WriteResponsesEventAsync(
                                        deltaEvent.Type,
                                        JsonSerializer.Serialize(deltaEvent, CoreJsonContext.Default.OpenAiResponseFunctionCallArgumentsDeltaEvent));
                                }

                                var doneEvent = new OpenAiResponseFunctionCallArgumentsDoneEvent
                                {
                                    SequenceNumber = NextSequenceNumber(),
                                    ResponseId = responseId,
                                    OutputIndex = toolState.OutputIndex,
                                    ItemId = toolState.ItemId,
                                    CallId = toolState.CallId,
                                    Name = toolState.ToolName,
                                    Arguments = toolState.Arguments
                                };
                                await WriteResponsesEventAsync(
                                    doneEvent.Type,
                                    JsonSerializer.Serialize(doneEvent, CoreJsonContext.Default.OpenAiResponseFunctionCallArgumentsDoneEvent));

                                var completedFunctionCallItem = CreateFunctionCallItem(toolState, status: "completed");
                                responseOutputItems[toolState.OutputIndex] = completedFunctionCallItem;

                                var itemDoneEvent = new OpenAiResponseOutputItemDoneEvent
                                {
                                    SequenceNumber = NextSequenceNumber(),
                                    ResponseId = responseId,
                                    OutputIndex = toolState.OutputIndex,
                                    Item = completedFunctionCallItem
                                };
                                await WriteResponsesEventAsync(
                                    itemDoneEvent.Type,
                                    JsonSerializer.Serialize(itemDoneEvent, CoreJsonContext.Default.OpenAiResponseOutputItemDoneEvent));
                                await ctx.Response.Body.FlushAsync(ctx.RequestAborted);
                            }
                            else if (evt.Type == AgentStreamEventType.ToolDelta && !string.IsNullOrWhiteSpace(evt.ToolName))
                            {
                                var toolState = GetOrCreateToolCall(evt.ToolName, evt.ToolArguments);
                                toolState.ResultOutput.Append(evt.Content);
                                await EnsureToolResultItemAddedAsync(toolState);
                                responseOutputItems[toolState.ResultOutputIndex!.Value] = CreateFunctionCallOutputItem(
                                    toolState,
                                    toolState.ResultOutput.ToString(),
                                    status: "in_progress");

                                var deltaEvent = new OpenAiResponseToolOutputDeltaEvent
                                {
                                    SequenceNumber = NextSequenceNumber(),
                                    ResponseId = responseId,
                                    OutputIndex = toolState.ResultOutputIndex.Value,
                                    ItemId = toolState.ResultItemId!,
                                    CallId = toolState.CallId,
                                    ToolName = toolState.ToolName,
                                    Delta = evt.Content
                                };
                                await WriteResponsesEventAsync(
                                    deltaEvent.Type,
                                    JsonSerializer.Serialize(deltaEvent, CoreJsonContext.Default.OpenAiResponseToolOutputDeltaEvent));
                                await ctx.Response.Body.FlushAsync(ctx.RequestAborted);
                            }
                            else if (evt.Type == AgentStreamEventType.ToolResult && !string.IsNullOrWhiteSpace(evt.ToolName))
                            {
                                var toolState = GetOrCreateToolCall(evt.ToolName, evt.ToolArguments);
                                toolState.ResultOutput.Clear();
                                toolState.ResultOutput.Append(evt.Content);
                                await EnsureToolResultItemAddedAsync(toolState);

                                var completedToolResultItem = CreateFunctionCallOutputItem(
                                    toolState,
                                    toolState.ResultOutput.ToString(),
                                    status: "completed");
                                responseOutputItems[toolState.ResultOutputIndex!.Value] = completedToolResultItem;

                                var resultEvent = new OpenAiResponseToolResultEvent
                                {
                                    SequenceNumber = NextSequenceNumber(),
                                    ResponseId = responseId,
                                    OutputIndex = toolState.ResultOutputIndex.Value,
                                    ItemId = toolState.ResultItemId!,
                                    CallId = toolState.CallId,
                                    ToolName = toolState.ToolName,
                                    Content = evt.Content,
                                    ResultStatus = evt.ResultStatus ?? ToolResultStatuses.Completed,
                                    FailureCode = evt.FailureCode,
                                    FailureMessage = evt.FailureMessage,
                                    NextStep = evt.NextStep
                                };
                                await WriteResponsesEventAsync(
                                    resultEvent.Type,
                                    JsonSerializer.Serialize(resultEvent, CoreJsonContext.Default.OpenAiResponseToolResultEvent));

                                var itemDoneEvent = new OpenAiResponseOutputItemDoneEvent
                                {
                                    SequenceNumber = NextSequenceNumber(),
                                    ResponseId = responseId,
                                    OutputIndex = toolState.ResultOutputIndex.Value,
                                    Item = completedToolResultItem
                                };
                                await WriteResponsesEventAsync(
                                    itemDoneEvent.Type,
                                    JsonSerializer.Serialize(itemDoneEvent, CoreJsonContext.Default.OpenAiResponseOutputItemDoneEvent));

                                CompleteToolCall(toolState);
                                await ctx.Response.Body.FlushAsync(ctx.RequestAborted);
                            }
                            else if (evt.Type == AgentStreamEventType.Error)
                            {
                                await WriteFailedResponseAsync(evt.Content, evt.ErrorCode);
                                break;
                            }
                            else if (evt.Type == AgentStreamEventType.Done)
                            {
                                if (responseTerminated)
                                    break;

                                await FinalizeTextStateAsync("completed");

                                var completedEvent = new OpenAiResponseCompletedEvent
                                {
                                    SequenceNumber = NextSequenceNumber(),
                                    Response = new OpenAiResponseStreamResponse
                                    {
                                        Id = responseId,
                                        CreatedAt = createdAt,
                                        Model = model,
                                        Status = "completed",
                                        Output = [.. responseOutputItems],
                                        Usage = BuildUsage()
                                    }
                                };
                                await WriteResponsesEventAsync(
                                    completedEvent.Type,
                                    JsonSerializer.Serialize(completedEvent, CoreJsonContext.Default.OpenAiResponseCompletedEvent));
                                await ctx.Response.Body.FlushAsync(ctx.RequestAborted);
                                responseTerminated = true;
                            }
                        }
                    }
                    catch (OperationCanceledException) when (ctx.RequestAborted.IsCancellationRequested)
                    {
                        discardStableSessionOnExit = !string.IsNullOrWhiteSpace(stableSessionId);
                        throw;
                    }
                    catch (Exception)
                    {
                        await WriteFailedResponseAsync(
                            "Sorry, I'm having trouble reaching my AI provider right now. Please try again shortly.",
                            "provider_failure");
                    }
                }
                else
                {
                    var result = await runtime.AgentRuntime.RunAsync(
                        session,
                        req.Input,
                        ctx.RequestAborted,
                        approvalCallback: approvalCallback);
                    var addedTurns = session.History.Skip(historyStartIndex).ToArray();

                    var response = new OpenAiResponseResponse
                    {
                        Id = responseId,
                        CreatedAt = createdAt,
                        Model = model,
                        Status = "completed",
                        Output = BuildResponseOutputItems(addedTurns, result),
                        Usage = new OpenAiUsage
                        {
                            PromptTokens = (int)session.TotalInputTokens,
                            CompletionTokens = (int)session.TotalOutputTokens,
                            TotalTokens = (int)(session.TotalInputTokens + session.TotalOutputTokens)
                        }
                    };

                    ctx.Response.ContentType = "application/json";
                    await ctx.Response.WriteAsync(
                        JsonSerializer.Serialize(response, CoreJsonContext.Default.OpenAiResponseResponse),
                        ctx.RequestAborted);
                }
            }
            catch (OperationCanceledException) when (ctx.RequestAborted.IsCancellationRequested)
            {
                discardStableSessionOnExit = !string.IsNullOrWhiteSpace(stableSessionId);
                throw;
            }
            finally
            {
                if (!string.IsNullOrWhiteSpace(stableSessionId))
                {
                    if (discardStableSessionOnExit)
                    {
                        runtime.SessionManager.RemoveActive(session.Id);
                    }
                    else
                    {
                        await PersistStableSessionAsync(runtime.SessionManager, session, sessionLockHeld: stableSessionLock is not null);
                    }

                    if (stableSessionLock is not null)
                        await stableSessionLock.DisposeAsync();
                }
                else
                {
                    runtime.SessionManager.RemoveActive(session.Id);
                }
            }
        });
    }
}

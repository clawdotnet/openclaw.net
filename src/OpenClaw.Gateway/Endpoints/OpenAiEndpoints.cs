using System.Text;
using System.Text.Json;
using OpenClaw.Agent;
using OpenClaw.Core.Middleware;
using OpenClaw.Core.Models;
using OpenClaw.Core.Sessions;
using OpenClaw.Gateway.Bootstrap;
using OpenClaw.Gateway.Composition;

namespace OpenClaw.Gateway.Endpoints;

internal static class OpenAiEndpoints
{
    private const int MaxChatCompletionRequestBytes = 1024 * 1024;
    private const string StableSessionHeader = "X-OpenClaw-Session-Id";

    public static void MapOpenClawOpenAiEndpoints(
        this WebApplication app,
        GatewayStartupContext startup,
        GatewayAppRuntime runtime)
    {
        app.MapPost("/v1/chat/completions", async (HttpContext ctx) =>
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

            OpenAiChatCompletionRequest? req;
            try
            {
                var (bodyOk, bodyText) = await EndpointHelpers.TryReadBodyTextAsync(ctx, MaxChatCompletionRequestBytes, ctx.RequestAborted);
                if (!bodyOk)
                {
                    ctx.Response.StatusCode = StatusCodes.Status413PayloadTooLarge;
                    await ctx.Response.WriteAsync("Request too large.", ctx.RequestAborted);
                    return;
                }

                req = JsonSerializer.Deserialize(
                    bodyText,
                    CoreJsonContext.Default.OpenAiChatCompletionRequest);
            }
            catch
            {
                ctx.Response.StatusCode = StatusCodes.Status400BadRequest;
                await ctx.Response.WriteAsync("Invalid JSON request body.", ctx.RequestAborted);
                return;
            }

            if (req is null || req.Messages.Count == 0)
            {
                ctx.Response.StatusCode = StatusCodes.Status400BadRequest;
                await ctx.Response.WriteAsync("Request must include at least one message.", ctx.RequestAborted);
                return;
            }

            var lastUserMsg = req.Messages.FindLast(m =>
                string.Equals(m.Role, "user", StringComparison.OrdinalIgnoreCase));
            var userText = lastUserMsg?.Content ?? req.Messages[^1].Content ?? "";

            var requesterKey = EndpointHelpers.GetHttpRateLimitKey(ctx, startup.Config);
            var stableSessionId = GetOptionalStableSessionId(ctx);
            var requestId = $"oai-http:{Guid.NewGuid():N}";
            var session = !string.IsNullOrWhiteSpace(stableSessionId)
                ? await runtime.SessionManager.GetOrCreateByIdAsync(stableSessionId!, "openai-http", requesterKey, ctx.RequestAborted)
                : await runtime.SessionManager.GetOrCreateAsync("openai-http", requestId, ctx.RequestAborted);

            var httpMwCtx = new MessageContext
            {
                ChannelId = "openai-http",
                SenderId = requesterKey,
                SessionId = session.Id,
                Text = userText ?? "",
                SessionInputTokens = session.TotalInputTokens,
                SessionOutputTokens = session.TotalOutputTokens
            };
            var allow = await runtime.MiddlewarePipeline.ExecuteAsync(httpMwCtx, ctx.RequestAborted);
            if (!allow)
            {
                ctx.Response.StatusCode = StatusCodes.Status429TooManyRequests;
                await ctx.Response.WriteAsync(httpMwCtx.ShortCircuitResponse ?? "Request blocked.", ctx.RequestAborted);
                if (string.IsNullOrWhiteSpace(stableSessionId))
                    runtime.SessionManager.RemoveActive(session.Id);
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
            var presetHeader = ctx.Request.Headers.TryGetValue("X-OpenClaw-Preset", out var presetValues)
                ? presetValues.ToString()
                : null;
            if (!string.IsNullOrWhiteSpace(presetHeader))
            {
                runtime.Operations.SessionMetadata.Set(session.Id, new SessionMetadataUpdateRequest
                {
                    ActivePresetId = presetHeader.Trim()
                });
            }
            var approvalCallback = ToolApprovalCallbackFactory.Create(
                startup.Config,
                runtime,
                session,
                approvalChannelId: "openai-http",
                senderId: requesterKey);

            try
            {
                if (ShouldHydrateRequestHistory(stableSessionId, session))
                {
                    var lastUserIndex = req.Messages.FindLastIndex(m =>
                        string.Equals(m.Role, "user", StringComparison.OrdinalIgnoreCase));
                    var excludeIndex = lastUserIndex >= 0 ? lastUserIndex : req.Messages.Count - 1;

                    for (var i = 0; i < req.Messages.Count; i++)
                    {
                        if (i == excludeIndex)
                            continue;

                        var message = req.Messages[i];
                        if (string.Equals(message.Role, "system", StringComparison.OrdinalIgnoreCase)
                            || string.Equals(message.Role, "user", StringComparison.OrdinalIgnoreCase)
                            || string.Equals(message.Role, "assistant", StringComparison.OrdinalIgnoreCase))
                        {
                            session.History.Add(new ChatTurn
                            {
                                Role = message.Role.ToLowerInvariant(),
                                Content = message.Content
                            });
                        }
                    }
                }

                var completionId = $"chatcmpl-{Guid.NewGuid():N}"[..29];
                var created = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
                var model = req.Model ?? startup.Config.Llm.Model;

                if (req.Stream)
                {
                    ctx.Response.ContentType = "text/event-stream";
                    ctx.Response.Headers.CacheControl = "no-cache";
                    ctx.Response.Headers.Connection = "keep-alive";
                    var activeToolCalls = new Dictionary<string, Queue<(int Index, string CallId)>>(StringComparer.Ordinal);
                    var nextToolCallIndex = 0;

                    (int Index, string CallId) RegisterToolCall(string toolName)
                    {
                        var state = (Index: nextToolCallIndex, CallId: $"call_openclaw_{nextToolCallIndex + 1}");
                        nextToolCallIndex++;
                        if (!activeToolCalls.TryGetValue(toolName, out var queue))
                        {
                            queue = new Queue<(int Index, string CallId)>();
                            activeToolCalls[toolName] = queue;
                        }

                        queue.Enqueue(state);
                        return state;
                    }

                    (int Index, string CallId) GetOrCreateToolCall(string toolName)
                    {
                        if (activeToolCalls.TryGetValue(toolName, out var queue) && queue.Count > 0)
                            return queue.Peek();

                        return RegisterToolCall(toolName);
                    }

                    void CompleteToolCall(string toolName)
                    {
                        if (!activeToolCalls.TryGetValue(toolName, out var queue) || queue.Count == 0)
                            return;

                        queue.Dequeue();
                        if (queue.Count == 0)
                            activeToolCalls.Remove(toolName);
                    }

                    Task WriteChunkAsync(OpenAiDelta delta, string? finishReason = null)
                    {
                        var chunk = new OpenAiStreamChunk
                        {
                            Id = completionId,
                            Created = created,
                            Model = model,
                            Choices = [new OpenAiStreamChoice { Index = 0, Delta = delta, FinishReason = finishReason }]
                        };
                        var json = JsonSerializer.Serialize(chunk, CoreJsonContext.Default.OpenAiStreamChunk);
                        return ctx.Response.WriteAsync($"data: {json}\n\n", ctx.RequestAborted);
                    }

                    var roleChunk = new OpenAiStreamChunk
                    {
                        Id = completionId,
                        Created = created,
                        Model = model,
                        Choices = [new OpenAiStreamChoice { Index = 0, Delta = new OpenAiDelta { Role = "assistant" } }]
                    };
                    var roleJson = JsonSerializer.Serialize(roleChunk, CoreJsonContext.Default.OpenAiStreamChunk);
                    await ctx.Response.WriteAsync($"data: {roleJson}\n\n", ctx.RequestAborted);
                    await ctx.Response.Body.FlushAsync(ctx.RequestAborted);

                    await foreach (var evt in runtime.AgentRuntime.RunStreamingAsync(
                        session,
                        userText ?? "",
                        ctx.RequestAborted,
                        approvalCallback: approvalCallback))
                    {
                        if (evt.Type == AgentStreamEventType.TextDelta && !string.IsNullOrEmpty(evt.Content))
                        {
                            await WriteChunkAsync(new OpenAiDelta { Content = evt.Content });
                            await ctx.Response.Body.FlushAsync(ctx.RequestAborted);
                        }
                        else if (evt.Type == AgentStreamEventType.ToolStart && !string.IsNullOrWhiteSpace(evt.ToolName))
                        {
                            var toolCall = RegisterToolCall(evt.ToolName);
                            await WriteChunkAsync(new OpenAiDelta
                            {
                                ToolCalls =
                                [
                                    new OpenAiToolCallDelta
                                    {
                                        Index = toolCall.Index,
                                        Id = toolCall.CallId,
                                        Function = new OpenAiFunctionCallDelta
                                        {
                                            Name = evt.ToolName,
                                            Arguments = ""
                                        }
                                    }
                                ]
                            });
                            await ctx.Response.Body.FlushAsync(ctx.RequestAborted);
                        }
                        else if (evt.Type == AgentStreamEventType.ToolDelta && !string.IsNullOrWhiteSpace(evt.ToolName))
                        {
                            var toolCall = GetOrCreateToolCall(evt.ToolName);
                            await WriteChunkAsync(new OpenAiDelta
                            {
                                ToolDelta = new OpenAiToolOutputDelta
                                {
                                    CallId = toolCall.CallId,
                                    ToolName = evt.ToolName,
                                    Content = evt.Content
                                }
                            });
                            await ctx.Response.Body.FlushAsync(ctx.RequestAborted);
                        }
                        else if (evt.Type == AgentStreamEventType.ToolResult && !string.IsNullOrWhiteSpace(evt.ToolName))
                        {
                            var toolCall = GetOrCreateToolCall(evt.ToolName);
                            await WriteChunkAsync(new OpenAiDelta
                            {
                                ToolResult = new OpenAiToolResultDelta
                                {
                                    CallId = toolCall.CallId,
                                    ToolName = evt.ToolName,
                                    Content = evt.Content
                                },
                                ToolCalls =
                                [
                                    new OpenAiToolCallDelta
                                    {
                                        Index = toolCall.Index,
                                        Id = toolCall.CallId
                                    }
                                ]
                            });
                            CompleteToolCall(evt.ToolName);
                            await ctx.Response.Body.FlushAsync(ctx.RequestAborted);
                        }
                        else if (evt.Type == AgentStreamEventType.Done)
                        {
                            await WriteChunkAsync(new OpenAiDelta(), "stop");
                            await ctx.Response.WriteAsync("data: [DONE]\n\n", ctx.RequestAborted);
                            await ctx.Response.Body.FlushAsync(ctx.RequestAborted);
                        }
                    }
                }
                else
                {
                    var result = await runtime.AgentRuntime.RunAsync(
                        session,
                        userText ?? "",
                        ctx.RequestAborted,
                        approvalCallback: approvalCallback);

                    var response = new OpenAiChatCompletionResponse
                    {
                        Id = completionId,
                        Created = created,
                        Model = model,
                        Choices =
                        [
                            new OpenAiChoice
                            {
                                Index = 0,
                                Message = new OpenAiResponseMessage { Role = "assistant", Content = result },
                                FinishReason = "stop"
                            }
                        ],
                        Usage = new OpenAiUsage
                        {
                            PromptTokens = (int)session.TotalInputTokens,
                            CompletionTokens = (int)session.TotalOutputTokens,
                            TotalTokens = (int)(session.TotalInputTokens + session.TotalOutputTokens)
                        }
                    };

                    ctx.Response.ContentType = "application/json";
                    await ctx.Response.WriteAsync(
                        JsonSerializer.Serialize(response, CoreJsonContext.Default.OpenAiChatCompletionResponse),
                        ctx.RequestAborted);
                }
            }
            finally
            {
                if (!string.IsNullOrWhiteSpace(stableSessionId))
                    await PersistStableSessionAsync(runtime.SessionManager, session);
                else
                    runtime.SessionManager.RemoveActive(session.Id);
            }
        });

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
            var stableSessionId = GetOptionalStableSessionId(ctx);
            var requestId = $"oai-resp:{Guid.NewGuid():N}";
            var session = !string.IsNullOrWhiteSpace(stableSessionId)
                ? await runtime.SessionManager.GetOrCreateByIdAsync(stableSessionId!, "openai-responses", requesterKey, ctx.RequestAborted)
                : await runtime.SessionManager.GetOrCreateAsync("openai-responses", requestId, ctx.RequestAborted);

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
                if (string.IsNullOrWhiteSpace(stableSessionId))
                    runtime.SessionManager.RemoveActive(session.Id);
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
            var approvalCallback = ToolApprovalCallbackFactory.Create(
                startup.Config,
                runtime,
                session,
                approvalChannelId: "openai-http",
                senderId: requesterKey);

            try
            {
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
                                Content = evt.Content
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
            finally
            {
                if (!string.IsNullOrWhiteSpace(stableSessionId))
                    await PersistStableSessionAsync(runtime.SessionManager, session);
                else
                    runtime.SessionManager.RemoveActive(session.Id);
            }
        });
    }

    private static bool ShouldHydrateRequestHistory(string? stableSessionId, Session session)
        => string.IsNullOrWhiteSpace(stableSessionId) || session.History.Count == 0;

    private static async Task PersistStableSessionAsync(SessionManager sessionManager, Session session)
    {
        try
        {
            using var persistCts = new CancellationTokenSource(TimeSpan.FromSeconds(10));
            await sessionManager.PersistAsync(session, persistCts.Token);
        }
        catch
        {
            // Stable-session persistence is best-effort after the response has already been produced.
        }
    }

    private static string? GetOptionalStableSessionId(HttpContext ctx)
    {
        if (!ctx.Request.Headers.TryGetValue(StableSessionHeader, out var values))
            return null;

        var value = values.ToString().Trim();
        return string.IsNullOrWhiteSpace(value) ? null : value;
    }

    private static OpenAiResponseStreamItem CreateFunctionCallItem(ResponsesToolState state, string status)
        => new()
        {
            Id = state.ItemId,
            Type = "function_call",
            Status = status,
            CallId = state.CallId,
            Name = state.ToolName,
            Arguments = state.Arguments
        };

    private static OpenAiResponseStreamItem CreateFunctionCallOutputItem(ResponsesToolState state, string output, string status)
        => new()
        {
            Id = state.ResultItemId ?? throw new InvalidOperationException("Tool output item id has not been assigned."),
            Type = "function_call_output",
            Status = status,
            CallId = state.CallId,
            Output = output
        };

    private static OpenAiResponseError CreateResponseError(string? errorCode, string message)
        => new()
        {
            Code = errorCode switch
            {
                "provider_failure" => "provider_error",
                "session_token_limit" => "session_limit_exceeded",
                "max_iterations" => "orchestration_limit_exceeded",
                _ => string.IsNullOrWhiteSpace(errorCode) ? "runtime_error" : errorCode
            },
            Message = message
        };

    private static OpenAiResponseStreamItem CreateMessageItem(string itemId, string text, string status)
        => new()
        {
            Id = itemId,
            Type = "message",
            Status = status,
            Role = "assistant",
            Content = [new OpenAiResponseContent { Text = text }]
        };

    private static List<OpenAiResponseOutput> BuildResponseOutputItems(
        IReadOnlyList<ChatTurn> addedTurns,
        string fallbackText)
    {
        var outputs = new List<OpenAiResponseOutput>();
        var nextToolIndex = 0;
        var nextMessageIndex = 0;
        var messageAdded = false;

        foreach (var turn in addedTurns)
        {
            if (turn.Content == "[tool_use]" && turn.ToolCalls is { Count: > 0 })
            {
                foreach (var invocation in turn.ToolCalls)
                {
                    var callId = $"call_openclaw_{++nextToolIndex}";
                    outputs.Add(new OpenAiResponseOutput
                    {
                        Id = $"fc_{nextToolIndex}",
                        Type = "function_call",
                        Status = "completed",
                        CallId = callId,
                        Name = invocation.ToolName,
                        Arguments = string.IsNullOrWhiteSpace(invocation.Arguments) ? "{}" : invocation.Arguments
                    });
                    outputs.Add(new OpenAiResponseOutput
                    {
                        Id = $"fco_{nextToolIndex}",
                        Type = "function_call_output",
                        Status = "completed",
                        CallId = callId,
                        Output = invocation.Result ?? ""
                    });
                }

                continue;
            }

            if (turn.Role == "assistant" && turn.Content != "[tool_use]")
            {
                outputs.Add(new OpenAiResponseOutput
                {
                    Id = $"msg_{++nextMessageIndex}",
                    Type = "message",
                    Status = "completed",
                    Role = "assistant",
                    Content = [new OpenAiResponseContent { Text = turn.Content }]
                });
                messageAdded = true;
            }
        }

        if (!messageAdded)
        {
            outputs.Add(new OpenAiResponseOutput
            {
                Id = $"msg_{++nextMessageIndex}",
                Type = "message",
                Status = "completed",
                Role = "assistant",
                Content = [new OpenAiResponseContent { Text = fallbackText }]
            });
        }

        return outputs;
    }

    private static IEnumerable<string> SplitArguments(string arguments, int chunkSize = 48)
    {
        if (string.IsNullOrEmpty(arguments))
        {
            yield return "";
            yield break;
        }

        for (var index = 0; index < arguments.Length; index += chunkSize)
        {
            var length = Math.Min(chunkSize, arguments.Length - index);
            yield return arguments.Substring(index, length);
        }
    }

    private sealed class ResponsesToolState
    {
        public ResponsesToolState(
            int outputIndex,
            string itemId,
            string callId,
            string toolName,
            string arguments)
        {
            OutputIndex = outputIndex;
            ItemId = itemId;
            CallId = callId;
            ToolName = toolName;
            Arguments = arguments;
        }

        public int OutputIndex { get; }
        public string ItemId { get; }
        public string CallId { get; }
        public string ToolName { get; }
        public string Arguments { get; }
        public int? ResultOutputIndex { get; set; }
        public string? ResultItemId { get; set; }
        public StringBuilder ResultOutput { get; } = new();
    }

    private sealed class ResponsesTextState
    {
        public ResponsesTextState(int outputIndex, string itemId)
        {
            OutputIndex = outputIndex;
            ItemId = itemId;
        }

        public int OutputIndex { get; }
        public string ItemId { get; }
        public StringBuilder Content { get; } = new();
    }
}

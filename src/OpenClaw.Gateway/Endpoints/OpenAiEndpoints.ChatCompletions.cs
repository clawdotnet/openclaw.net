using System.Text.Json;
using OpenClaw.Agent;
using OpenClaw.Core.Middleware;
using OpenClaw.Core.Models;
using OpenClaw.Gateway.Bootstrap;
using OpenClaw.Gateway.Composition;

namespace OpenClaw.Gateway.Endpoints;

internal static partial class OpenAiEndpoints
{
    private static void MapChatCompletionsEndpoint(
        WebApplication app,
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
            var userText = (lastUserMsg?.Content ?? req.Messages[^1].Content).ToPromptText();

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
            var requestId = $"oai-http:{Guid.NewGuid():N}";
            var session = stableBinding is not null
                ? await runtime.SessionManager.GetOrCreateByIdAsync(BuildScopedStableSessionId(stableBinding), "openai-http", requesterKey, ctx.RequestAborted)
                : await runtime.SessionManager.GetOrCreateAsync("openai-http", requestId, ctx.RequestAborted);
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
                approvalCallback = ToolApprovalCallbackFactory.Create(
                    startup.Config,
                    runtime,
                    session,
                    approvalChannelId: "openai-http",
                    senderId: requesterKey);

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
                                Content = message.Content.ToPromptText()
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
                                    Content = evt.Content,
                                    ResultStatus = evt.ResultStatus ?? ToolResultStatuses.Completed,
                                    FailureCode = evt.FailureCode,
                                    FailureMessage = evt.FailureMessage,
                                    NextStep = evt.NextStep
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

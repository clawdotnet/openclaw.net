using OpenClaw.Channels;
using OpenClaw.Core.Models;
using OpenClaw.Gateway.Bootstrap;
using OpenClaw.Gateway.Channels;
using OpenClaw.Gateway.Composition;

namespace OpenClaw.Gateway.Endpoints;

/// <summary>
/// Generic channel config admin endpoints.
/// Routes:
///   GET  /admin/channels/{channel}         — read current effective config
///   POST /admin/channels/{channel}/update  — apply in-memory override and reconnect
///
/// To add a new channel, add one case to each switch block below.
/// Auth and rate-limit logic are shared across all channels.
/// </summary>
internal static class AdminChannelEndpoints
{
    public static void MapOpenClawAdminChannelEndpoints(
        this WebApplication app,
        GatewayStartupContext startup,
        GatewayAppRuntime runtime)
    {
        var browserSessions = app.Services.GetRequiredService<BrowserSessionAuthService>();
        var operations = runtime.Operations;

        // Resolve channel adapters at startup (only channels that support runtime updates).
        var feishu = app.Services.GetRequiredService<FeishuChannel>();
        var dingtalk = app.Services.GetRequiredService<DingTalkChannel>();
        var wecom = app.Services.GetRequiredService<WeComChannel>();
        var channelStore = app.Services.GetRequiredService<ChannelConfigStore>();
        var defaultDingTalkConfig = CloneDingTalkConfig(startup.Config.Channels.DingTalk);
        var defaultWeComConfig = CloneWeComConfig(startup.Config.Channels.WeCom);

        // ── GET /admin/channels/{channel} ─────────────────────────────────────
        // Returns the currently effective config for the named channel.
        app.MapGet("/admin/channels/{channel}", (HttpContext ctx, string channel) =>
        {
            var auth = EndpointHelpers.AuthorizeOperatorRequest(ctx, startup, browserSessions, requireCsrf: false);
            if (!auth.IsAuthorized)
                return Results.Unauthorized();

            return channel switch
            {
                "feishu" => Results.Json(feishu.GetEffectiveConfigForAdmin(), CoreJsonContext.Default.FeishuChannelConfig),
                "dingtalk" => Results.Json(GetEffectiveDingTalkConfig(channelStore, defaultDingTalkConfig), CoreJsonContext.Default.DingTalkChannelConfig),
                "wecom" => Results.Json(GetEffectiveWeComConfig(channelStore, defaultWeComConfig), CoreJsonContext.Default.WeComChannelConfig),

                // Add new channels here:
                // "slack"   => Results.Json(slack.GetEffectiveConfig(), CoreJsonContext.Default.SlackChannelConfig),
                // "discord" => Results.Json(discord.GetEffectiveConfig(), CoreJsonContext.Default.DiscordChannelConfig),

                _ => Results.Json(
                    new OperationStatusResponse { Success = false, Error = $"Unknown channel '{channel}'." },
                    CoreJsonContext.Default.OperationStatusResponse,
                    statusCode: StatusCodes.Status404NotFound)
            };
        });

        // ── POST /admin/channels/{channel}/update ─────────────────────────────
        // Applies a full in-memory config replacement and reconnects the channel.
        app.MapPost("/admin/channels/{channel}/update", async (HttpContext ctx, string channel) =>
        {
            var auth = EndpointHelpers.AuthorizeOperatorRequest(ctx, startup, browserSessions, requireCsrf: true);
            if (!auth.IsAuthorized)
                return Results.Unauthorized();

            if (!EndpointHelpers.TryConsumeOperatorRateLimit(ctx, operations, auth, "admin.channels", out var blockedByPolicyId))
                return Results.Json(
                    new OperationStatusResponse { Success = false, Error = $"Rate limit exceeded by policy '{blockedByPolicyId}'." },
                    CoreJsonContext.Default.OperationStatusResponse,
                    statusCode: StatusCodes.Status429TooManyRequests);

            return channel switch
            {
                "feishu" => await HandleFeishuUpdateAsync(ctx, feishu, channelStore),
                "dingtalk" => await HandleDingTalkUpdateAsync(ctx, startup.Config.Channels, channelStore),
                "wecom" => await HandleWeComUpdateAsync(ctx, wecom, channelStore),

                // Add new channels here:
                // "slack"   => await HandleSlackUpdateAsync(ctx, slack, channelStore),
                // "discord" => await HandleDiscordUpdateAsync(ctx, discord, channelStore),

                _ => Results.Json(
                    new OperationStatusResponse { Success = false, Error = $"Unknown channel '{channel}'." },
                    CoreJsonContext.Default.OperationStatusResponse,
                    statusCode: StatusCodes.Status404NotFound)
            };
        });

        // ── DELETE /admin/channels/{channel}/override ─────────────────────────
        // Clears the in-memory + persisted override so the channel falls back to appsettings.
        app.MapDelete("/admin/channels/{channel}/override", async (HttpContext ctx, string channel) =>
        {
            var auth = EndpointHelpers.AuthorizeOperatorRequest(ctx, startup, browserSessions, requireCsrf: true);
            if (!auth.IsAuthorized)
                return Results.Unauthorized();

            if (!EndpointHelpers.TryConsumeOperatorRateLimit(ctx, operations, auth, "admin.channels", out var blockedByPolicyId))
                return Results.Json(
                    new OperationStatusResponse { Success = false, Error = $"Rate limit exceeded by policy '{blockedByPolicyId}'." },
                    CoreJsonContext.Default.OperationStatusResponse,
                    statusCode: StatusCodes.Status429TooManyRequests);

            switch (channel)
            {
                case "feishu":
                    channelStore.Delete("feishu");
                    // Clear override, revert to appsettings config
                    feishu.SetRuntimeConfig(null);
                    await feishu.RestartAsync(ctx.RequestAborted);
                    break;
                case "dingtalk":
                    channelStore.Delete("dingtalk");
                    startup.Config.Channels.DingTalk = CloneDingTalkConfig(defaultDingTalkConfig);
                    break;
                case "wecom":
                    channelStore.Delete("wecom");
                    wecom.SetRuntimeConfig(null);
                    await wecom.RestartAsync(ctx.RequestAborted);
                    break;

                // Add new channels here

                default:
                    return Results.Json(
                        new OperationStatusResponse { Success = false, Error = $"Unknown channel '{channel}'." },
                        CoreJsonContext.Default.OperationStatusResponse,
                        statusCode: StatusCodes.Status404NotFound);
            }

            return Results.Json(
                new OperationStatusResponse { Success = true, Message = $"Channel '{channel}' override cleared; reverted to appsettings." },
                CoreJsonContext.Default.OperationStatusResponse);
        });
    }

    // ── Per-channel update handlers ───────────────────────────────────────────
    // Each handler is responsible for deserializing its own typed config (AOT-safe)
    // and calling the channel's UpdateConfigAsync().

    private static async Task<IResult> HandleFeishuUpdateAsync(HttpContext ctx, FeishuChannel feishu, ChannelConfigStore channelStore)
    {
        FeishuChannelConfig? patch;
        try
        {
            patch = await ctx.Request.ReadFromJsonAsync(CoreJsonContext.Default.FeishuChannelConfig, ctx.RequestAborted);
        }
        catch (Exception ex)
        {
            return Results.Json(
                new OperationStatusResponse { Success = false, Error = $"Invalid JSON: {ex.Message}" },
                CoreJsonContext.Default.OperationStatusResponse,
                statusCode: StatusCodes.Status400BadRequest);
        }

        if (patch is null)
            return Results.Json(
                new OperationStatusResponse { Success = false, Error = "Request body is required." },
                CoreJsonContext.Default.OperationStatusResponse,
                statusCode: StatusCodes.Status400BadRequest);

        // Persist to volume first so the config survives a container restart.
        channelStore.Save("feishu", patch, CoreJsonContext.Default.FeishuChannelConfig);

        // Apply in-memory and reconnect.
        await feishu.UpdateConfigAsync(patch, ctx.RequestAborted);

        return Results.Json(
            new OperationStatusResponse { Success = true, Message = "Feishu config persisted and channel reconnected." },
            CoreJsonContext.Default.OperationStatusResponse);
    }

    private static async Task<IResult> HandleDingTalkUpdateAsync(HttpContext ctx, ChannelsConfig channelsConfig, ChannelConfigStore channelStore)
    {
        DingTalkChannelConfig? patch;
        try
        {
            patch = await ctx.Request.ReadFromJsonAsync(CoreJsonContext.Default.DingTalkChannelConfig, ctx.RequestAborted);
        }
        catch (Exception ex)
        {
            return Results.Json(
                new OperationStatusResponse { Success = false, Error = $"Invalid JSON: {ex.Message}" },
                CoreJsonContext.Default.OperationStatusResponse,
                statusCode: StatusCodes.Status400BadRequest);
        }

        if (patch is null)
            return Results.Json(
                new OperationStatusResponse { Success = false, Error = "Request body is required." },
                CoreJsonContext.Default.OperationStatusResponse,
                statusCode: StatusCodes.Status400BadRequest);

        patch = NormalizeDingTalkConfig(patch);
        channelStore.Save("dingtalk", patch, CoreJsonContext.Default.DingTalkChannelConfig);
        channelsConfig.DingTalk = patch;

        return Results.Json(
            new OperationStatusResponse { Success = true, Message = "DingTalk config persisted." },
            CoreJsonContext.Default.OperationStatusResponse);
    }

    private static DingTalkChannelConfig GetEffectiveDingTalkConfig(ChannelConfigStore channelStore, DingTalkChannelConfig defaultConfig)
    {
        var loaded = channelStore.TryLoad("dingtalk", CoreJsonContext.Default.DingTalkChannelConfig);
        return NormalizeDingTalkConfig(loaded ?? defaultConfig);
    }

    private static DingTalkChannelConfig CloneDingTalkConfig(DingTalkChannelConfig source)
    {
        return NormalizeDingTalkConfig(new DingTalkChannelConfig
        {
            Enabled = source.Enabled,
            AppId = source.AppId,
            AppIdRef = source.AppIdRef,
            AppKey = source.AppKey,
            AppKeyRef = source.AppKeyRef,
            AppSecret = source.AppSecret,
            AppSecretRef = source.AppSecretRef,
            RobotCode = source.RobotCode,
            RobotCodeRef = source.RobotCodeRef,
            GroupPolicy = source.GroupPolicy,
            AllowedFromUserIds = source.AllowedFromUserIds.ToArray(),
            AllowedGroupIds = source.AllowedGroupIds.ToArray(),
            MaxInboundChars = source.MaxInboundChars,
            RequireMentionInGroup = source.RequireMentionInGroup,
            ExposeInboundMediaUrls = source.ExposeInboundMediaUrls,
            StreamPollIntervalMs = source.StreamPollIntervalMs,
        });
    }

    private static DingTalkChannelConfig NormalizeDingTalkConfig(DingTalkChannelConfig source)
    {
        return source;
    }

    // ── WeCom admin API helpers ──

    /// <summary>Handle WeCom config update request (POST /admin/channels/wecom/update)</summary>
    private static async Task<IResult> HandleWeComUpdateAsync(HttpContext ctx, WeComChannel wecom, ChannelConfigStore channelStore)
    {
        WeComChannelConfig? patch;
        try
        {
            patch = await ctx.Request.ReadFromJsonAsync(CoreJsonContext.Default.WeComChannelConfig, ctx.RequestAborted);
        }
        catch (Exception ex)
        {
            return Results.Json(
                new OperationStatusResponse { Success = false, Error = $"Invalid JSON: {ex.Message}" },
                CoreJsonContext.Default.OperationStatusResponse,
                statusCode: StatusCodes.Status400BadRequest);
        }

        if (patch is null)
            return Results.Json(
                new OperationStatusResponse { Success = false, Error = "Request body is required." },
                CoreJsonContext.Default.OperationStatusResponse,
                statusCode: StatusCodes.Status400BadRequest);

        patch = NormalizeWeComConfig(patch);

        // Persist to volume storage so the config survives container restarts.
        channelStore.Save("wecom", patch, CoreJsonContext.Default.WeComChannelConfig);

        // Hot-reload and reconnect
        await wecom.UpdateConfigAsync(patch, ctx.RequestAborted);

        return Results.Json(
                new OperationStatusResponse { Success = true, Message = "WeCom config persisted and reconnected." },
            CoreJsonContext.Default.OperationStatusResponse);
    }

    /// <summary>Get currently effective WeCom config (prefers persisted override)</summary>
    private static WeComChannelConfig GetEffectiveWeComConfig(ChannelConfigStore channelStore, WeComChannelConfig defaultConfig)
    {
        var loaded = channelStore.TryLoad("wecom", CoreJsonContext.Default.WeComChannelConfig);
        return NormalizeWeComConfig(loaded ?? defaultConfig);
    }

    /// <summary>Deep-clone WeCom config (for saving default snapshot)</summary>
    private static WeComChannelConfig CloneWeComConfig(WeComChannelConfig source)
    {
        return NormalizeWeComConfig(new WeComChannelConfig
        {
            Enabled = source.Enabled,
            BotId = source.BotId,
            BotIdRef = source.BotIdRef,
            BotSecret = source.BotSecret,
            BotSecretRef = source.BotSecretRef,
            CorpId = source.CorpId,
            CorpIdRef = source.CorpIdRef,
            AgentId = source.AgentId,
            AgentIdRef = source.AgentIdRef,
            CorpSecret = source.CorpSecret,
            CorpSecretRef = source.CorpSecretRef,
            GroupPolicy = source.GroupPolicy,
            AllowedFromUserIds = source.AllowedFromUserIds.ToArray(),
            AllowedGroupIds = source.AllowedGroupIds.ToArray(),
            MaxInboundChars = source.MaxInboundChars,
            RequireMentionInGroup = source.RequireMentionInGroup,
        });
    }

    /// <summary>Normalize WeCom config (passthrough, reserved for future logic)</summary>
    private static WeComChannelConfig NormalizeWeComConfig(WeComChannelConfig source)
    {
        return source;
    }
}

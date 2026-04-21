using Microsoft.Extensions.Logging.Abstractions;
using OpenClaw.Core.Models;
using OpenClaw.Gateway;
using Xunit;

namespace OpenClaw.Tests;

public sealed class ToolPresetResolverTests
{
    [Fact]
    public void Resolve_ConfiguredPresetFromSessionMetadata_AppliesToolsetRules()
    {
        var storagePath = CreateStoragePath();
        var metadataStore = new SessionMetadataStore(storagePath, NullLogger<SessionMetadataStore>.Instance);
        metadataStore.Set("sess_ops", new SessionMetadataUpdateRequest
        {
            ActivePresetId = "ops"
        });

        var config = new GatewayConfig();
        config.Tooling.Toolsets["agent_ops"] = new ToolsetConfig
        {
            AllowTools = ["automation", "session_search", "profile_read", "todo"],
            DenyTools = ["shell"]
        };
        config.Tooling.Presets["ops"] = new ToolPresetConfig
        {
            Toolsets = ["agent_ops"],
            DenyTools = ["todo"],
            ApprovalRequiredTools = ["automation"],
            Description = "Ops preset"
        };

        var resolver = new ToolPresetResolver(config, metadataStore);
        var resolved = resolver.Resolve(
            new Session
            {
                Id = "sess_ops",
                ChannelId = "websocket",
                SenderId = "user1"
            },
            ["shell", "automation", "session_search", "profile_read", "todo"]);

        Assert.Equal("ops", resolved.PresetId);
        Assert.DoesNotContain("shell", resolved.AllowedTools, StringComparer.OrdinalIgnoreCase);
        Assert.Contains("automation", resolved.AllowedTools, StringComparer.OrdinalIgnoreCase);
        Assert.Contains("session_search", resolved.AllowedTools, StringComparer.OrdinalIgnoreCase);
        Assert.Contains("profile_read", resolved.AllowedTools, StringComparer.OrdinalIgnoreCase);
        Assert.DoesNotContain("todo", resolved.AllowedTools, StringComparer.OrdinalIgnoreCase);
        Assert.Contains("automation", resolved.ApprovalRequiredTools, StringComparer.OrdinalIgnoreCase);
    }

    [Fact]
    public void Resolve_UsesSurfaceBindingWhenNoSessionOverride()
    {
        var storagePath = CreateStoragePath();
        var metadataStore = new SessionMetadataStore(storagePath, NullLogger<SessionMetadataStore>.Instance);
        var config = new GatewayConfig();
        config.Tooling.SurfaceBindings["telegram"] = "readonly";

        var resolver = new ToolPresetResolver(config, metadataStore);
        var resolved = resolver.Resolve(
            new Session
            {
                Id = "sess_telegram",
                ChannelId = "telegram",
                SenderId = "user1"
            },
            ["shell", "session_search"]);

        Assert.Equal("readonly", resolved.PresetId);
        Assert.DoesNotContain("shell", resolved.AllowedTools, StringComparer.OrdinalIgnoreCase);
        Assert.Contains("session_search", resolved.AllowedTools, StringComparer.OrdinalIgnoreCase);
    }

    [Fact]
    public void Resolve_RoutePresetId_TakesPrecedenceOverSessionMetadata()
    {
        var storagePath = CreateStoragePath();
        var metadataStore = new SessionMetadataStore(storagePath, NullLogger<SessionMetadataStore>.Instance);
        metadataStore.Set("sess_route", new SessionMetadataUpdateRequest
        {
            ActivePresetId = "ops"
        });

        var config = new GatewayConfig();
        config.Tooling.Presets["ops"] = new ToolPresetConfig
        {
            DenyTools = ["shell"]
        };
        config.Tooling.Presets["readonly"] = new ToolPresetConfig
        {
            DenyTools = ["shell", "write_file"]
        };

        var resolver = new ToolPresetResolver(config, metadataStore);
        var resolved = resolver.Resolve(
            new Session
            {
                Id = "sess_route",
                ChannelId = "websocket",
                SenderId = "user1",
                RoutePresetId = "readonly"
            },
            ["shell", "write_file", "session_search"]);

        Assert.Equal("readonly", resolved.PresetId);
        Assert.DoesNotContain("shell", resolved.AllowedTools, StringComparer.OrdinalIgnoreCase);
        Assert.DoesNotContain("write_file", resolved.AllowedTools, StringComparer.OrdinalIgnoreCase);
        Assert.Contains("session_search", resolved.AllowedTools, StringComparer.OrdinalIgnoreCase);
    }

    [Fact]
    public void Resolve_OpenAiHttp_DefaultsToWebPreset()
    {
        var resolver = new ToolPresetResolver(new GatewayConfig(), CreateMetadataStore());
        var resolved = resolver.Resolve(
            new Session
            {
                Id = "sess_openai_http",
                ChannelId = "openai-http",
                SenderId = "user1"
            },
            ["shell", "session_search"]);

        Assert.Equal("web", resolved.PresetId);
        Assert.DoesNotContain("shell", resolved.AllowedTools, StringComparer.OrdinalIgnoreCase);
        Assert.Contains("session_search", resolved.AllowedTools, StringComparer.OrdinalIgnoreCase);
    }

    [Fact]
    public void Resolve_OpenAiHttp_SurfaceBinding_OverridesDefaultPreset()
    {
        var config = new GatewayConfig();
        config.Tooling.SurfaceBindings["openai-http"] = "readonly";

        var resolver = new ToolPresetResolver(config, CreateMetadataStore());
        var resolved = resolver.Resolve(
            new Session
            {
                Id = "sess_openai_http_bound",
                ChannelId = "openai-http",
                SenderId = "user1"
            },
            ["shell", "write_file", "session_search"]);

        Assert.Equal("readonly", resolved.PresetId);
        Assert.DoesNotContain("shell", resolved.AllowedTools, StringComparer.OrdinalIgnoreCase);
        Assert.DoesNotContain("write_file", resolved.AllowedTools, StringComparer.OrdinalIgnoreCase);
        Assert.Contains("session_search", resolved.AllowedTools, StringComparer.OrdinalIgnoreCase);
    }

    [Fact]
    public void Resolve_OpenAiResponses_UsesOpenAiHttpAliasBinding()
    {
        var config = new GatewayConfig();
        config.Tooling.SurfaceBindings["openai-http"] = "readonly";

        var resolver = new ToolPresetResolver(config, CreateMetadataStore());
        var resolved = resolver.Resolve(
            new Session
            {
                Id = "sess_openai_responses",
                ChannelId = "openai-responses",
                SenderId = "user1"
            },
            ["shell", "write_file", "session_search"]);

        Assert.Equal("readonly", resolved.PresetId);
        Assert.DoesNotContain("shell", resolved.AllowedTools, StringComparer.OrdinalIgnoreCase);
        Assert.DoesNotContain("write_file", resolved.AllowedTools, StringComparer.OrdinalIgnoreCase);
        Assert.Contains("session_search", resolved.AllowedTools, StringComparer.OrdinalIgnoreCase);
    }

    private static SessionMetadataStore CreateMetadataStore()
    {
        var storagePath = CreateStoragePath();
        return new SessionMetadataStore(storagePath, NullLogger<SessionMetadataStore>.Instance);
    }

    private static string CreateStoragePath()
    {
        var path = Path.Combine(Path.GetTempPath(), "openclaw-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(path);
        return path;
    }
}

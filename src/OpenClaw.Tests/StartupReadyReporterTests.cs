using OpenClaw.Core.Models;
using OpenClaw.Gateway.Bootstrap;
using OpenClaw.Gateway.Pipeline;
using Xunit;

namespace OpenClaw.Tests;

public sealed class StartupReadyReporterTests
{
    [Fact]
    public void Render_LoopbackBind_UsesLocalhostUrls()
    {
        var startup = CreateStartupContext(config => config.BindAddress = "127.0.0.1");

        var text = StartupReadyReporter.Render(startup);

        Assert.Contains("OpenClaw gateway ready.", text, StringComparison.Ordinal);
        Assert.Contains("Listening on http://127.0.0.1:18789", text, StringComparison.Ordinal);
        Assert.Contains("Chat UI: http://localhost:18789/chat", text, StringComparison.Ordinal);
        Assert.Contains("Admin UI: http://localhost:18789/admin", text, StringComparison.Ordinal);
        Assert.Contains("Doctor: http://localhost:18789/doctor/text", text, StringComparison.Ordinal);
        Assert.Contains("WebSocket: ws://localhost:18789/ws", text, StringComparison.Ordinal);
        Assert.Contains("Ctrl-C to stop", text, StringComparison.Ordinal);
        Assert.Contains("Next useful commands:", text, StringComparison.Ordinal);
        Assert.Contains("openclaw models doctor", text, StringComparison.Ordinal);
    }

    [Fact]
    public void Render_WildcardBind_UsesLocalhostUrls()
    {
        var startup = CreateStartupContext(config => config.BindAddress = "0.0.0.0");

        var text = StartupReadyReporter.Render(startup);

        Assert.Contains("Listening on http://0.0.0.0:18789", text, StringComparison.Ordinal);
        Assert.Contains("Chat UI: http://localhost:18789/chat", text, StringComparison.Ordinal);
        Assert.Contains("MCP: http://localhost:18789/mcp", text, StringComparison.Ordinal);
    }

    [Fact]
    public void Render_ExplicitLanBind_UsesBoundAddressUrls()
    {
        var startup = CreateStartupContext(config => config.BindAddress = "192.168.1.25");

        var text = StartupReadyReporter.Render(startup);

        Assert.Contains("Listening on http://192.168.1.25:18789", text, StringComparison.Ordinal);
        Assert.Contains("Chat UI: http://192.168.1.25:18789/chat", text, StringComparison.Ordinal);
        Assert.Contains("Health: http://192.168.1.25:18789/health", text, StringComparison.Ordinal);
    }

    [Fact]
    public void Render_IncludesStartupNoticesAndPersistedConfigCommand()
    {
        var startup = CreateStartupContext(config => config.BindAddress = "127.0.0.1");

        var text = StartupReadyReporter.Render(
            startup,
            notices:
            [
                new StartupNoticeSnapshot("Disabled: browser tool is unavailable because no execution backend or sandbox route is configured.", 1),
                new StartupNoticeSnapshot("Background job 'hourly-news-digest' is still running from an earlier trigger; this tick was skipped.", 2)
            ],
            knownConfigPath: "/Users/test/.openclaw/config/openclaw.settings.json");

        Assert.Contains("Started with notices:", text, StringComparison.Ordinal);
        Assert.Contains("Disabled: browser tool is unavailable", text, StringComparison.Ordinal);
        Assert.Contains("Background job 'hourly-news-digest'", text, StringComparison.Ordinal);
        Assert.Contains("(x2)", text, StringComparison.Ordinal);
        Assert.Contains("openclaw setup verify --config /Users/test/.openclaw/config/openclaw.settings.json", text, StringComparison.Ordinal);
        Assert.DoesNotContain("openclaw models doctor", text, StringComparison.Ordinal);
    }

    private static GatewayStartupContext CreateStartupContext(Action<GatewayConfig>? configure = null)
    {
        var config = new GatewayConfig();
        configure?.Invoke(config);
        return new GatewayStartupContext
        {
            Config = config,
            RuntimeState = new GatewayRuntimeState
            {
                RequestedMode = "auto",
                EffectiveMode = GatewayRuntimeMode.Aot,
                DynamicCodeSupported = false
            },
            IsNonLoopbackBind = !OpenClaw.Core.Security.BindAddressClassifier.IsLoopbackBind(config.BindAddress),
            WorkspacePath = null
        };
    }
}

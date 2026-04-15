using System.Text.Json;
using OpenClaw.Cli;
using Xunit;

namespace OpenClaw.Tests;

public sealed class ChannelSetupCommandTests
{
    [Fact]
    public async Task RunAsync_ChannelTelegram_UpdatesExistingConfig()
    {
        var root = CreateTempRoot();
        try
        {
            var configPath = await CreateBaseConfigAsync(root);
            using var output = new StringWriter();
            using var error = new StringWriter();

            var exitCode = await SetupCommand.RunAsync(
                [
                    "channel",
                    "telegram",
                    "--config", configPath,
                    "--non-interactive",
                    "--bot-token-ref", "env:TELEGRAM_BOT_TOKEN",
                    "--public-base-url", "https://bot.example.com",
                    "--webhook-secret-ref", "env:TELEGRAM_WEBHOOK_SECRET"
                ],
                new StringReader(string.Empty),
                output,
                error,
                root,
                canPrompt: false);

            Assert.Equal(0, exitCode);
            Assert.Equal(string.Empty, error.ToString());

            using var document = JsonDocument.Parse(await File.ReadAllTextAsync(configPath));
            var telegram = document.RootElement.GetProperty("OpenClaw").GetProperty("channels").GetProperty("telegram");
            Assert.True(telegram.GetProperty("enabled").GetBoolean());
            Assert.Equal("env:TELEGRAM_BOT_TOKEN", telegram.GetProperty("botTokenRef").GetString());
            Assert.Equal("https://bot.example.com", telegram.GetProperty("webhookPublicBaseUrl").GetString());
            Assert.True(telegram.GetProperty("validateSignature").GetBoolean());
            Assert.Equal("env:TELEGRAM_WEBHOOK_SECRET", telegram.GetProperty("webhookSecretTokenRef").GetString());
            Assert.Contains("Register Telegram webhook", output.ToString(), StringComparison.Ordinal);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public async Task RunAsync_ChannelWhatsAppBridge_UpdatesExistingConfig()
    {
        var root = CreateTempRoot();
        try
        {
            var configPath = await CreateBaseConfigAsync(root);
            using var output = new StringWriter();
            using var error = new StringWriter();

            var exitCode = await SetupCommand.RunAsync(
                [
                    "channel",
                    "whatsapp",
                    "--config", configPath,
                    "--non-interactive",
                    "--mode", "bridge",
                    "--bridge-url", "https://bridge.example.com/inbound",
                    "--bridge-token-ref", "env:WHATSAPP_BRIDGE_TOKEN"
                ],
                new StringReader(string.Empty),
                output,
                error,
                root,
                canPrompt: false);

            Assert.Equal(0, exitCode);
            Assert.Equal(string.Empty, error.ToString());

            using var document = JsonDocument.Parse(await File.ReadAllTextAsync(configPath));
            var whatsapp = document.RootElement.GetProperty("OpenClaw").GetProperty("channels").GetProperty("whatsApp");
            Assert.True(whatsapp.GetProperty("enabled").GetBoolean());
            Assert.Equal("bridge", whatsapp.GetProperty("type").GetString());
            Assert.Equal("https://bridge.example.com/inbound", whatsapp.GetProperty("bridgeUrl").GetString());
            Assert.Equal("env:WHATSAPP_BRIDGE_TOKEN", whatsapp.GetProperty("bridgeTokenRef").GetString());
            Assert.Contains("WhatsApp bridge inbound URL expected by the gateway", output.ToString(), StringComparison.Ordinal);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    private static async Task<string> CreateBaseConfigAsync(string root)
    {
        var configPath = Path.Combine(root, "config", "openclaw.settings.json");
        var workspace = Path.Combine(root, "workspace");
        using var output = new StringWriter();
        using var error = new StringWriter();

        var exitCode = await SetupCommand.RunAsync(
            [
                "--non-interactive",
                "--profile", "local",
                "--config", configPath,
                "--workspace", workspace,
                "--provider", "openai",
                "--model", "gpt-4o",
                "--api-key", "env:OPENAI_API_KEY"
            ],
            new StringReader(string.Empty),
            output,
            error,
            root,
            canPrompt: false);

        Assert.Equal(0, exitCode);
        Assert.Equal(string.Empty, error.ToString());
        return configPath;
    }

    private static string CreateTempRoot()
    {
        var root = Path.Combine(Path.GetTempPath(), "openclaw-channel-setup-tests", Guid.NewGuid().ToString("n"));
        Directory.CreateDirectory(root);
        return root;
    }
}

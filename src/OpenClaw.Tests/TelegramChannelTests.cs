using Microsoft.Extensions.Logging.Abstractions;
using OpenClaw.Channels;
using OpenClaw.Core.Models;
using Xunit;

namespace OpenClaw.Tests;

[Collection(EnvironmentVariableCollection.Name)]
public sealed class TelegramChannelTests
{
    [Fact]
    public async Task Constructor_ResolvesRawBotTokenRef()
    {
        await using var channel = new TelegramChannel(
            new TelegramChannelConfig
            {
                Enabled = true,
                BotTokenRef = "raw:test-token"
            },
            NullLogger<TelegramChannel>.Instance);

        Assert.Equal("telegram", channel.ChannelId);
    }

    [Fact]
    public async Task Constructor_ResolvesEnvBotTokenRef()
    {
        const string envName = "OPENCLAW_TEST_TELEGRAM_TOKEN";
        Environment.SetEnvironmentVariable(envName, "env-token");

        try
        {
            await using var channel = new TelegramChannel(
                new TelegramChannelConfig
                {
                    Enabled = true,
                    BotTokenRef = $"env:{envName}"
                },
                NullLogger<TelegramChannel>.Instance);

            Assert.Equal("telegram", channel.ChannelId);
        }
        finally
        {
            Environment.SetEnvironmentVariable(envName, null);
        }
    }
}

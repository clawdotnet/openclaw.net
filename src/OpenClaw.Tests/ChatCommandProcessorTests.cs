using Microsoft.Extensions.Logging.Abstractions;
using OpenClaw.Core.Memory;
using OpenClaw.Core.Models;
using OpenClaw.Core.Observability;
using OpenClaw.Core.Pipeline;
using OpenClaw.Core.Sessions;
using Xunit;

namespace OpenClaw.Tests;

public sealed class ChatCommandProcessorTests
{
    [Fact]
    public void RegisterDynamic_BuiltInCommand_IsRejected()
    {
        var store = new FileMemoryStore(System.IO.Path.Combine(System.IO.Path.GetTempPath(), "openclaw-command-tests", Guid.NewGuid().ToString("N")), 4);
        var processor = new ChatCommandProcessor(new SessionManager(store, new GatewayConfig(), NullLogger.Instance));

        var result = processor.RegisterDynamic("/status", static (_, _) => Task.FromResult("nope"));

        Assert.Equal(DynamicCommandRegistrationResult.ReservedBuiltIn, result);
    }

    [Fact]
    public void RegisterDynamic_DuplicateCommand_FirstRegistrationWins()
    {
        var store = new FileMemoryStore(System.IO.Path.Combine(System.IO.Path.GetTempPath(), "openclaw-command-tests", Guid.NewGuid().ToString("N")), 4);
        var processor = new ChatCommandProcessor(new SessionManager(store, new GatewayConfig(), NullLogger.Instance));

        var first = processor.RegisterDynamic("greet", static (_, _) => Task.FromResult("first"));
        var duplicate = processor.RegisterDynamic("greet", static (_, _) => Task.FromResult("second"));

        Assert.Equal(DynamicCommandRegistrationResult.Registered, first);
        Assert.Equal(DynamicCommandRegistrationResult.Duplicate, duplicate);
    }

    [Fact]
    public async Task Compact_Command_ReportsRemainingTurns()
    {
        var store = new FileMemoryStore(System.IO.Path.Combine(System.IO.Path.GetTempPath(), "openclaw-command-tests", Guid.NewGuid().ToString("N")), 4);
        var processor = new ChatCommandProcessor(new SessionManager(store, new GatewayConfig(), NullLogger.Instance));
        processor.SetCompactCallback(static (_, _) => Task.FromResult(6));

        var session = new Session
        {
            Id = "sess-compact",
            ChannelId = "websocket",
            SenderId = "user1"
        };
        session.History.AddRange(
        [
            new ChatTurn { Role = "user", Content = "u1" },
            new ChatTurn { Role = "assistant", Content = "a1" },
            new ChatTurn { Role = "user", Content = "u2" },
            new ChatTurn { Role = "assistant", Content = "a2" }
        ]);

        var (handled, response) = await processor.TryProcessCommandAsync(session, "/compact", CancellationToken.None);

        Assert.True(handled);
        Assert.Equal("Compacted: 4 turns → 6 turns remaining.", response);
    }

    [Fact]
    public async Task Status_Command_UsesRecentUsageFallbackForPromptCacheCounters()
    {
        var store = new FileMemoryStore(System.IO.Path.Combine(System.IO.Path.GetTempPath(), "openclaw-command-tests", Guid.NewGuid().ToString("N")), 4);
        var usage = new ProviderUsageTracker();
        usage.RecordTurn(
            "sess-cache",
            "websocket",
            "openai",
            "gpt-4.1",
            inputTokens: 100,
            outputTokens: 20,
            cacheReadTokens: 512,
            cacheWriteTokens: 0,
            estimatedInputTokensByComponent: new InputTokenComponentEstimate());
        var processor = new ChatCommandProcessor(new SessionManager(store, new GatewayConfig(), NullLogger.Instance), usage);

        var session = new Session
        {
            Id = "sess-cache",
            ChannelId = "websocket",
            SenderId = "user1"
        };

        var (handled, response) = await processor.TryProcessCommandAsync(session, "/status", CancellationToken.None);

        Assert.True(handled);
        Assert.Contains("Prompt Cache: 512 read / 0 write", response, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Concise_Command_TogglesSessionResponseMode()
    {
        var store = new FileMemoryStore(System.IO.Path.Combine(System.IO.Path.GetTempPath(), "openclaw-command-tests", Guid.NewGuid().ToString("N")), 4);
        var processor = new ChatCommandProcessor(new SessionManager(store, new GatewayConfig(), NullLogger.Instance));
        var session = new Session
        {
            Id = "sess-concise",
            ChannelId = "websocket",
            SenderId = "user1"
        };

        var (_, onResponse) = await processor.TryProcessCommandAsync(session, "/concise on", CancellationToken.None);
        Assert.Equal(SessionResponseModes.ConciseOps, session.ResponseMode);
        Assert.Equal("Concise operational mode enabled.", onResponse);

        var (_, offResponse) = await processor.TryProcessCommandAsync(session, "/concise off", CancellationToken.None);
        Assert.Equal(SessionResponseModes.Full, session.ResponseMode);
        Assert.Equal("Concise operational mode disabled for this session.", offResponse);

        var (_, autoResponse) = await processor.TryProcessCommandAsync(session, "/concise auto", CancellationToken.None);
        Assert.Equal(SessionResponseModes.Default, session.ResponseMode);
        Assert.Equal("Concise mode reset to automatic behavior.", autoResponse);
    }

    [Fact]
    public async Task Help_Command_IncludesConciseMode()
    {
        var store = new FileMemoryStore(System.IO.Path.Combine(System.IO.Path.GetTempPath(), "openclaw-command-tests", Guid.NewGuid().ToString("N")), 4);
        var processor = new ChatCommandProcessor(new SessionManager(store, new GatewayConfig(), NullLogger.Instance));
        var session = new Session
        {
            Id = "sess-help",
            ChannelId = "websocket",
            SenderId = "user1"
        };

        var (handled, response) = await processor.TryProcessCommandAsync(session, "/help", CancellationToken.None);

        Assert.True(handled);
        Assert.Contains("/concise on|off|auto", response, StringComparison.Ordinal);
    }
}

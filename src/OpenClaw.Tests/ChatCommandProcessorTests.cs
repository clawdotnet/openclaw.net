using Microsoft.Extensions.Logging.Abstractions;
using OpenClaw.Core.Memory;
using OpenClaw.Core.Models;
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
}

#if OPENCLAW_ENABLE_MAF_EXPERIMENT
using System.Text.Json;
using Microsoft.Agents.AI;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using OpenClaw.Core.Memory;
using OpenClaw.Core.Models;
using OpenClaw.MicrosoftAgentFrameworkAdapter;
using Xunit;

namespace OpenClaw.Tests;

public sealed class MafAdapterTests
{
    [Fact]
    public void MafCapabilities_JitMode_IsSupported()
    {
        var runtimeState = new GatewayRuntimeState
        {
            RequestedMode = "jit",
            EffectiveMode = GatewayRuntimeMode.Jit,
            DynamicCodeSupported = true
        };

        MafCapabilities.EnsureSupported(runtimeState);
    }

    [Fact]
    public void MafCapabilities_AotMode_IsSupported()
    {
        var runtimeState = new GatewayRuntimeState
        {
            RequestedMode = "aot",
            EffectiveMode = GatewayRuntimeMode.Aot,
            DynamicCodeSupported = false
        };

        MafCapabilities.EnsureSupported(runtimeState);
    }

    [Fact]
    public async Task MafSessionStateStore_RoundTripsSerializedSession_WhenHistoryMatches()
    {
        var storagePath = Path.Combine(Path.GetTempPath(), "openclaw-maf-sidecar-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(storagePath);

        try
        {
            var store = CreateStore(storagePath);
            var agent = CreateAgent();
            var session = CreateSession("maf-roundtrip");
            var agentSession = await CreatePopulatedAgentSessionAsync(agent);
            var savedState = NormalizeJson(await agent.SerializeSessionAsync(agentSession, jsonSerializerOptions: null, CancellationToken.None));

            await store.SaveAsync(agent, session, agentSession, CancellationToken.None);
            var loadedSession = await store.LoadAsync(agent, session, CancellationToken.None);
            var loadedState = NormalizeJson(await agent.SerializeSessionAsync(loadedSession, jsonSerializerOptions: null, CancellationToken.None));

            Assert.Equal(savedState, loadedState);
        }
        finally
        {
            Directory.Delete(storagePath, recursive: true);
        }
    }

    [Fact]
    public async Task MafSessionStateStore_HistoryHashMismatch_RebuildsFreshSession()
    {
        var storagePath = Path.Combine(Path.GetTempPath(), "openclaw-maf-sidecar-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(storagePath);

        try
        {
            var store = CreateStore(storagePath);
            var agent = CreateAgent();
            var session = CreateSession("maf-mismatch");
            var agentSession = await CreatePopulatedAgentSessionAsync(agent);

            await store.SaveAsync(agent, session, agentSession, CancellationToken.None);

            session.History.Add(new ChatTurn
            {
                Role = "assistant",
                Content = "history changed"
            });

            var loadedSession = await store.LoadAsync(agent, session, CancellationToken.None);
            var loadedState = NormalizeJson(await agent.SerializeSessionAsync(loadedSession, jsonSerializerOptions: null, CancellationToken.None));
            var freshState = NormalizeJson(await agent.SerializeSessionAsync(await agent.CreateSessionAsync(CancellationToken.None), jsonSerializerOptions: null, CancellationToken.None));

            Assert.Equal(freshState, loadedState);
        }
        finally
        {
            Directory.Delete(storagePath, recursive: true);
        }
    }

    [Fact]
    public async Task MafSessionStateStore_CorruptedSidecar_RebuildsFreshSession()
    {
        var storagePath = Path.Combine(Path.GetTempPath(), "openclaw-maf-sidecar-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(storagePath);

        try
        {
            var store = CreateStore(storagePath);
            var agent = CreateAgent();
            var session = CreateSession("maf-corrupt");
            var agentSession = await CreatePopulatedAgentSessionAsync(agent);

            await store.SaveAsync(agent, session, agentSession, CancellationToken.None);
            await File.WriteAllTextAsync(store.GetSessionPath(session.Id), "{not-json", CancellationToken.None);

            var loadedSession = await store.LoadAsync(agent, session, CancellationToken.None);
            var loadedState = NormalizeJson(await agent.SerializeSessionAsync(loadedSession, jsonSerializerOptions: null, CancellationToken.None));
            var freshState = NormalizeJson(await agent.SerializeSessionAsync(await agent.CreateSessionAsync(CancellationToken.None), jsonSerializerOptions: null, CancellationToken.None));

            Assert.Equal(freshState, loadedState);
        }
        finally
        {
            Directory.Delete(storagePath, recursive: true);
        }
    }

    [Fact]
    public void MafSessionStateStore_HistoryHash_ChangesWhenModelOverrideChanges()
    {
        var session = CreateSession("maf-hash");
        var baseline = MafSessionStateStore.ComputeHistoryHash(session);
        session.ModelOverride = "gpt-maf";

        var updated = MafSessionStateStore.ComputeHistoryHash(session);

        Assert.NotEqual(baseline, updated);
    }

    [Fact]
    public async Task MafSessionStateStore_HistoryHash_RemainsStableAcrossFileSessionReload()
    {
        var storagePath = Path.Combine(Path.GetTempPath(), "openclaw-maf-sidecar-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(storagePath);

        try
        {
            var session = CreateSession("maf-file-hash");
            session.History.Add(new ChatTurn
            {
                Role = "assistant",
                Content = "[tool_use]",
                ToolCalls =
                [
                    new ToolInvocation
                    {
                        ToolName = "memory",
                        Arguments = """{"action":"write","key":"note","content":"hello"}""",
                        Result = "Saved note: note",
                        Duration = TimeSpan.FromMilliseconds(12)
                    }
                ]
            });
            session.History.Add(new ChatTurn
            {
                Role = "assistant",
                Content = "Tool said: Saved note: note"
            });

            var expectedHash = MafSessionStateStore.ComputeHistoryHash(session);

            var writerStore = new FileMemoryStore(storagePath, 4);
            await writerStore.SaveSessionAsync(session, CancellationToken.None);

            var readerStore = new FileMemoryStore(storagePath, 4);
            var loaded = await readerStore.GetSessionAsync(session.Id, CancellationToken.None);

            Assert.NotNull(loaded);
            Assert.Equal(expectedHash, MafSessionStateStore.ComputeHistoryHash(loaded!));
        }
        finally
        {
            Directory.Delete(storagePath, recursive: true);
        }
    }

    private static MafSessionStateStore CreateStore(string storagePath)
    {
        var config = new GatewayConfig
        {
            Memory = new MemoryConfig
            {
                StoragePath = storagePath
            }
        };

        return new MafSessionStateStore(
            config,
            Options.Create(new MafOptions()),
            NullLogger<MafSessionStateStore>.Instance);
    }

    private static ChatClientAgent CreateAgent()
    {
        var factory = new MafAgentFactory(
            Options.Create(new MafOptions()),
            NullLoggerFactory.Instance,
            new ServiceCollection().BuildServiceProvider());

        return factory.Create(new MafTestChatClient(), "Test instructions", []);
    }

    private static Session CreateSession(string sessionId)
    {
        var session = new Session
        {
            Id = sessionId,
            ChannelId = "test",
            SenderId = "user"
        };
        session.History.Add(new ChatTurn
        {
            Role = "user",
            Content = "hello"
        });
        return session;
    }

    private static async Task<AgentSession> CreatePopulatedAgentSessionAsync(ChatClientAgent agent)
    {
        var agentSession = await agent.CreateSessionAsync(CancellationToken.None);
        _ = await agent.RunAsync(
            [new ChatMessage(ChatRole.User, "hello from maf sidecar")],
            agentSession,
            new ChatClientAgentRunOptions(new ChatOptions()),
            CancellationToken.None);
        return agentSession;
    }

    private static string NormalizeJson(JsonElement element)
        => JsonSerializer.Serialize(element, new JsonSerializerOptions { WriteIndented = false });

    private sealed class MafTestChatClient : IChatClient
    {
        public Task<ChatResponse> GetResponseAsync(
            IEnumerable<ChatMessage> messages,
            ChatOptions? options = null,
            CancellationToken cancellationToken = default)
        {
            _ = messages;
            _ = options;
            return Task.FromResult(new ChatResponse([new ChatMessage(ChatRole.Assistant, "ok")]));
        }

        public async IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(
            IEnumerable<ChatMessage> messages,
            ChatOptions? options = null,
            [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken = default)
        {
            _ = messages;
            _ = options;
            _ = cancellationToken;
            await Task.CompletedTask;
            yield break;
        }

        public object? GetService(Type serviceType, object? serviceKey = null) => null;

        public void Dispose()
        {
        }
    }
}
#endif

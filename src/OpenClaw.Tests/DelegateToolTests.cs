using System.Text.Json;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging.Abstractions;
using OpenClaw.Agent;
using OpenClaw.Agent.Tools;
using OpenClaw.Core.Abstractions;
using OpenClaw.Core.Memory;
using OpenClaw.Core.Models;
using OpenClaw.Core.Observability;
using OpenClaw.Core.Skills;
using Xunit;

namespace OpenClaw.Tests;

public sealed class DelegateToolTests
{
    [Fact]
    public async Task ExecuteAsync_UsesInjectedRuntimeFactory()
    {
        var storagePath = Path.Combine(Path.GetTempPath(), "openclaw-delegate-tool-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(storagePath);

        try
        {
            var llmConfig = new LlmProviderConfig
            {
                Provider = "test",
                Model = "test-model"
            };
            var delegation = new DelegationConfig
            {
                Enabled = true,
                Profiles = new Dictionary<string, AgentProfile>(StringComparer.Ordinal)
                {
                    ["reviewer"] = new()
                    {
                        Name = "reviewer",
                        SystemPrompt = "Review the change.",
                        MaxHistoryTurns = 6,
                        MaxIterations = 2
                    }
                }
            };

            var wasCalled = false;
            IReadOnlyList<ITool>? capturedTools = null;
            LlmProviderConfig? capturedConfig = null;
            AgentProfile? capturedProfile = null;

            var tool = new DelegateTool(
                new TestChatClient(),
                [new TestTool()],
                new FileMemoryStore(storagePath, 4),
                llmConfig,
                delegation,
                logger: NullLogger.Instance,
                runtimeFactory: (tools, config, profile) =>
                {
                    wasCalled = true;
                    capturedTools = tools;
                    capturedConfig = config;
                    capturedProfile = profile;
                    return new FakeRuntime("delegated-result");
                });

            var result = await tool.ExecuteAsync("""
                {"profile":"reviewer","task":"Inspect the change"}
                """, TestContext.Current.CancellationToken);

            Assert.Equal("delegated-result", result);
            Assert.True(wasCalled);
            Assert.NotNull(capturedTools);
            Assert.Contains(capturedTools!, candidate => candidate.Name == "test_tool");
            Assert.NotNull(capturedConfig);
            Assert.Equal("test", capturedConfig!.Provider);
            Assert.Equal("test-model", capturedConfig.Model);
            Assert.Same(delegation.Profiles["reviewer"], capturedProfile);
        }
        finally
        {
            Directory.Delete(storagePath, recursive: true);
        }
    }

    [Fact]
    public async Task ExecuteAsync_WithContext_PersistsDelegationMetadataAndParentSummary()
    {
        var storagePath = Path.Combine(Path.GetTempPath(), "openclaw-delegate-context-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(storagePath);

        try
        {
            var llmConfig = new LlmProviderConfig
            {
                Provider = "test",
                Model = "test-model"
            };
            var delegation = new DelegationConfig
            {
                Enabled = true,
                Profiles = new Dictionary<string, AgentProfile>(StringComparer.Ordinal)
                {
                    ["reviewer"] = new()
                    {
                        Name = "reviewer",
                        SystemPrompt = "Review the change.",
                        MaxHistoryTurns = 6,
                        MaxIterations = 2
                    }
                }
            };

            var memoryStore = new FileMemoryStore(storagePath, 4);
            var tool = new DelegateTool(
                new TestChatClient(),
                [new TestTool()],
                memoryStore,
                llmConfig,
                delegation,
                logger: NullLogger.Instance,
                runtimeFactory: (_, _, _) => new FakeRuntime("delegated-result", static session =>
                {
                    session.History.Add(new ChatTurn
                    {
                        Role = "assistant",
                        Content = "Delegated work completed.",
                        ToolCalls =
                        [
                            new ToolInvocation
                            {
                                ToolName = "shell",
                                Arguments = """{"command":"pwd"}""",
                                Result = "/tmp",
                                Duration = TimeSpan.FromMilliseconds(15)
                            }
                        ]
                    });
                }));

            var parentSession = new Session
            {
                Id = "parent-session",
                ChannelId = "api",
                SenderId = "operator"
            };
            var context = new ToolExecutionContext
            {
                Session = parentSession,
                TurnContext = new TurnContext
                {
                    SessionId = parentSession.Id,
                    ChannelId = parentSession.ChannelId
                }
            };

            var result = await tool.ExecuteAsync("""
                {"profile":"reviewer","task":"Inspect the change"}
                """, context, TestContext.Current.CancellationToken);

            Assert.Equal("delegated-result", result);
            var parentSummary = Assert.Single(parentSession.DelegatedSessions);
            Assert.Equal("reviewer", parentSummary.Profile);
            Assert.Equal("completed", parentSummary.Status);
            Assert.Contains(parentSummary.ToolUsage, item => item.ToolName == "shell");

            var persisted = await memoryStore.GetSessionAsync(parentSummary.SessionId, TestContext.Current.CancellationToken);
            Assert.NotNull(persisted);
            Assert.Equal("delegation", persisted!.ChannelId);
            Assert.NotNull(persisted.Delegation);
            Assert.Equal("parent-session", persisted.Delegation!.ParentSessionId);
            Assert.Equal("reviewer", persisted.Delegation.Profile);
            Assert.Equal("completed", persisted.Delegation.Status);
            Assert.Contains(persisted.Delegation.ToolUsage, item => item.ToolName == "shell" && item.IsMutation);
        }
        finally
        {
            Directory.Delete(storagePath, recursive: true);
        }
    }

    private sealed class TestTool : ITool
    {
        public string Name => "test_tool";

        public string Description => "Test tool.";

        public string ParameterSchema => """{"type":"object"}""";

        public ValueTask<string> ExecuteAsync(string argumentsJson, CancellationToken ct)
        {
            _ = argumentsJson;
            _ = ct;
            return ValueTask.FromResult("ok");
        }
    }

    private sealed class FakeRuntime(string response, Action<Session>? mutateSession = null) : IAgentRuntime
    {
        public CircuitState CircuitBreakerState => CircuitState.Closed;

        public IReadOnlyList<string> LoadedSkillNames => [];

        public IReadOnlyList<SkillDefinition> LoadedSkills => [];

        public Task<string> RunAsync(
            Session session,
            string userMessage,
            CancellationToken ct,
            ToolApprovalCallback? approvalCallback = null,
            JsonElement? responseSchema = null,
            string? correlationId = null)
        {
            _ = userMessage;
            _ = ct;
            _ = approvalCallback;
            _ = responseSchema;
            mutateSession?.Invoke(session);
            return Task.FromResult(response);
        }

        public Task<AgentTurnResult> RunTurnAsync(
            Session session,
            string userMessage,
            CancellationToken ct,
            ToolApprovalCallback? approvalCallback = null,
            JsonElement? responseSchema = null,
            string? correlationId = null)
        {
            _ = userMessage;
            _ = ct;
            _ = approvalCallback;
            _ = responseSchema;
            mutateSession?.Invoke(session);
            return Task.FromResult(AgentTurnResult.Completed(response));
        }

        public Task<IReadOnlyList<string>> ReloadSkillsAsync(CancellationToken ct = default)
        {
            _ = ct;
            return Task.FromResult<IReadOnlyList<string>>([]);
        }

        public async IAsyncEnumerable<AgentStreamEvent> RunStreamingAsync(
            Session session,
            string userMessage,
            [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken ct,
            ToolApprovalCallback? approvalCallback = null,
            string? correlationId = null)
        {
            _ = session;
            _ = userMessage;
            _ = ct;
            _ = approvalCallback;
            yield break;
        }
    }

    private sealed class TestChatClient : IChatClient
    {
        public Task<ChatResponse> GetResponseAsync(
            IEnumerable<ChatMessage> messages,
            ChatOptions? options = null,
            CancellationToken cancellationToken = default)
        {
            _ = messages;
            _ = options;
            _ = cancellationToken;
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

        public object? GetService(Type serviceType, object? serviceKey = null)
        {
            _ = serviceType;
            _ = serviceKey;
            return null;
        }

        public void Dispose()
        {
        }
    }
}

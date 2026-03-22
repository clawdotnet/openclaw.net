using System.Text.Json;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging.Abstractions;
using OpenClaw.Agent;
using OpenClaw.Agent.Tools;
using OpenClaw.Core.Abstractions;
using OpenClaw.Core.Memory;
using OpenClaw.Core.Models;
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
                """, CancellationToken.None);

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

    private sealed class FakeRuntime(string response) : IAgentRuntime
    {
        public CircuitState CircuitBreakerState => CircuitState.Closed;

        public IReadOnlyList<string> LoadedSkillNames => [];

        public Task<string> RunAsync(
            Session session,
            string userMessage,
            CancellationToken ct,
            ToolApprovalCallback? approvalCallback = null,
            JsonElement? responseSchema = null)
        {
            _ = session;
            _ = userMessage;
            _ = ct;
            _ = approvalCallback;
            _ = responseSchema;
            return Task.FromResult(response);
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
            ToolApprovalCallback? approvalCallback = null)
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

using System.Net;
using System.Text;
using System.Text.Json;
using Avalonia.Headless.XUnit;
using Avalonia.Threading;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;
using OpenClaw.Agent;
using OpenClaw.Companion.Services;
using OpenClaw.Companion.ViewModels;
using OpenClaw.Companion.Views;
using OpenClaw.Core.Abstractions;
using OpenClaw.Core.Models;
using OpenClaw.Core.Setup;
using OpenClaw.Gateway.Extensions;
using OpenClaw.Gateway.Models;
using Xunit;

namespace OpenClaw.Tests;

public sealed class DesktopFirstSuccessContractTests
{
    [AvaloniaFact]
    [Trait("Category", "DesktopRelease")]
    public async Task OllamaAgentic_FromCompanionBinding_SelectsSequentialModelAndCompletesToolCall()
    {
        var directory = Path.Combine(Path.GetTempPath(), "desktop-first-success", Guid.NewGuid().ToString("N"));
        var store = new SettingsStore(directory);
        var viewModel = new MainWindowViewModel(store, new GatewayWebSocketClient());
        var window = new MainWindow { DataContext = viewModel };
        try
        {
            window.Show();
            viewModel.SetupProvider = "ollama";
            viewModel.SetupModel = "fixture-model";
            viewModel.SetupModelPreset = "ollama-agentic";
            Dispatcher.UIThread.RunJobs();

            var settings = store.Load();
            Assert.Equal("ollama-agentic", settings.SetupModelPreset);
            Assert.All(viewModel.SetupModelPresetOptions, id =>
                Assert.True(LocalModelPresetCatalog.TryGet(id, out _), $"Setup offers unknown preset '{id}'."));

            var config = GatewaySetupProfileFactory.CreateProfileConfig(
                "local",
                "127.0.0.1",
                18789,
                "test-token",
                directory,
                Path.Combine(directory, "memory"),
                settings.SetupProvider!,
                settings.SetupModel,
                "",
                settings.SetupModelPreset);
            var profile = Assert.Single(config.Models.Profiles);
            Assert.Equal("ollama-agentic", profile.PresetId);
            Assert.True(profile.Capabilities.SupportsTools);
            Assert.False(profile.Capabilities.SupportsParallelToolCalls);

            using var registry = new ConfiguredModelProfileRegistry(config, NullLogger<ConfiguredModelProfileRegistry>.Instance);
            registry.SetDefaultProfileId();
            var selectionPolicy = new DefaultModelSelectionPolicy(registry);
            using var schema = JsonDocument.Parse("""{"type":"object","properties":{"value":{"type":"string"}}}""");
            var selection = selectionPolicy.Resolve(new ModelSelectionRequest
            {
                Session = new Session
                {
                    Id = "desktop-first-success",
                    ChannelId = "desktop",
                    SenderId = "operator",
                    ModelProfileId = "local-primary"
                },
                Messages = [new ChatMessage(ChatRole.User, "Record the first successful tool call.")],
                Options = new ChatOptions
                {
                    Tools =
                    [
                        AIFunctionFactory.CreateDeclaration("record_observation", "Record an observation", schema.RootElement.Clone(), returnJsonSchema: null),
                        AIFunctionFactory.CreateDeclaration("read_observation", "Read an observation", schema.RootElement.Clone(), returnJsonSchema: null)
                    ]
                }
            });

            Assert.Equal("local-primary", selection.SelectedProfileId);
            Assert.True(selection.Requirements.SupportsTools);
            Assert.Null(selection.Requirements.SupportsParallelToolCalls);

            var handler = new SequentialOllamaHandler();
            using var httpClient = new HttpClient(handler);
            using var ollama = new OllamaChatClient(config.Llm, httpClient);
            var tool = new RecordingTool();
            var runtime = new AgentRuntime(
                ollama,
                [tool, new NoOpTool()],
                Substitute.For<IMemoryStore>(),
                config.Llm,
                maxHistoryTurns: 10);
            var result = await runtime.RunAsync(
                new Session { Id = "tool-round-trip", ChannelId = "desktop", SenderId = "operator" },
                "Record the first successful tool call.",
                TestContext.Current.CancellationToken);

            Assert.Equal("desktop first success complete", result);
            Assert.Equal(1, tool.CallCount);
            Assert.Equal(2, handler.RequestBodies.Count);
            Assert.Contains("\"record_observation\"", handler.RequestBodies[0], StringComparison.Ordinal);
            Assert.Contains("\"read_observation\"", handler.RequestBodies[0], StringComparison.Ordinal);
            Assert.Contains("\"role\":\"tool\"", handler.RequestBodies[1], StringComparison.Ordinal);
        }
        finally
        {
            window.Close();
            if (Directory.Exists(directory)) Directory.Delete(directory, recursive: true);
        }
    }

    private sealed class SequentialOllamaHandler : HttpMessageHandler
    {
        public List<string> RequestBodies { get; } = [];

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Assert.Equal("/api/chat", request.RequestUri!.AbsolutePath);
            RequestBodies.Add(await request.Content!.ReadAsStringAsync(cancellationToken));
            var body = RequestBodies.Count == 1
                ? """{"message":{"content":"","tool_calls":[{"function":{"name":"record_observation","arguments":{"value":"ready"}}}]},"done_reason":"stop","prompt_eval_count":12,"eval_count":4}"""
                : """{"message":{"content":"desktop first success complete"},"done_reason":"stop","prompt_eval_count":18,"eval_count":5}""";
            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(body, Encoding.UTF8, "application/json")
            };
        }
    }

    private sealed class RecordingTool : ITool
    {
        private int _callCount;

        public int CallCount => Volatile.Read(ref _callCount);
        public string Name => "record_observation";
        public string Description => "Record an observation.";
        public string ParameterSchema => """{"type":"object","properties":{"value":{"type":"string"}},"required":["value"]}""";

        public ValueTask<string> ExecuteAsync(string argumentsJson, CancellationToken ct)
        {
            Interlocked.Increment(ref _callCount);
            return ValueTask.FromResult("recorded");
        }
    }

    private sealed class NoOpTool : ITool
    {
        public string Name => "read_observation";
        public string Description => "Read an observation.";
        public string ParameterSchema => """{"type":"object","properties":{"value":{"type":"string"}}}""";
        public ValueTask<string> ExecuteAsync(string argumentsJson, CancellationToken ct) => ValueTask.FromResult("none");
    }
}

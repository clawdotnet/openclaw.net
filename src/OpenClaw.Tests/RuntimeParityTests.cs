using Microsoft.Extensions.AI;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using OpenClaw.Agent;
using OpenClaw.Core.Abstractions;
using OpenClaw.Core.Memory;
using OpenClaw.Core.Models;
using OpenClaw.Core.Observability;
using OpenClaw.Core.Skills;
using OpenClaw.MicrosoftAgentFrameworkAdapter;
using Xunit;

namespace OpenClaw.Tests;

/// <summary>
/// Runs the same turn through both orchestrators and compares what each one produced.
/// </summary>
/// <remarks>
/// <para>
/// <b>Why this exists.</b> Everything shared between <see cref="AgentRuntime"/> and
/// <c>MafAgentRuntime</c> is protected by construction — one composer, one inliner, one renderer — but
/// the <em>write</em> path is not: each runtime calls
/// <see cref="AttachmentReferences.CreateUserTurn"/> at two of its own call sites, with its own
/// arguments. Nothing stopped those four sites from drifting, and the previous parity work only got as
/// far as "the structure is equivalent and both reach the composer". This fixture is the missing half:
/// same inbound text, same config, two runtimes, compared field by field.
/// </para>
/// <para>
/// <b>Why it was not written earlier.</b> <c>MafAgentRuntime</c> takes a whole MAF service graph, so a
/// parity test looked like it needed the Gateway. It does not — <see cref="BuildMafRuntime"/> assembles
/// the smallest graph that completes a turn, which is what makes comparing the two affordable.
/// </para>
/// </remarks>
public sealed class RuntimeParityTests : IDisposable
{
    private const string AssetName = "media_0123456789abcdef.png";
    private const string AssetBytes = "parity-payload";

    private readonly string _workspace;
    private readonly string _cacheRoot;

    public RuntimeParityTests()
    {
        _workspace = Path.Join(Path.GetTempPath(), $"openclaw-parity-{Guid.NewGuid():N}");
        _cacheRoot = Path.Join(_workspace, "media-cache");
        Directory.CreateDirectory(_cacheRoot);
        File.WriteAllText(Path.Join(_cacheRoot, AssetName), AssetBytes);
    }

    public void Dispose()
    {
        try
        {
            Directory.Delete(_workspace, recursive: true);
        }
        catch (IOException)
        {
            // A leftover temp directory is not a test failure.
        }
        catch (UnauthorizedAccessException)
        {
            // Same.
        }
    }

    [Fact]
    public async Task The_same_inbound_text_persists_the_same_turn_on_both_runtimes()
    {
        var agent = await RunAsync(useMaf: false, AttachmentText());
        var maf = await RunAsync(useMaf: true, AttachmentText());

        var agentTurn = FirstUserTurn(agent.Session);
        var mafTurn = FirstUserTurn(maf.Session);

        Assert.Equal(agentTurn.Content, mafTurn.Content);

        var agentAttachments = Assert.IsAssignableFrom<IReadOnlyList<ChatAttachmentRef>>(agentTurn.Attachments);
        var mafAttachments = Assert.IsAssignableFrom<IReadOnlyList<ChatAttachmentRef>>(mafTurn.Attachments);

        var expected = Assert.Single(agentAttachments);
        var actual = Assert.Single(mafAttachments);

        // Compares every field the composer and the inliner read, not just the count: an attachment
        // that survives with the wrong kind or a lost asset id is exactly the drift this looks for.
        Assert.Equal(expected.Kind, actual.Kind);
        Assert.Equal(expected.Value, actual.Value);
        Assert.Equal(expected.MimeType, actual.MimeType);
        Assert.Equal(expected.FileName, actual.FileName);
        Assert.Equal(expected.AssetId, actual.AssetId);
        Assert.NotNull(actual.AssetId);
    }

    [Fact]
    public async Task Both_runtimes_send_the_cached_bytes_to_the_model()
    {
        var agent = await RunAsync(useMaf: false, AttachmentText());
        var maf = await RunAsync(useMaf: true, AttachmentText());

        foreach (var run in new[] { agent, maf })
        {
            var data = Assert.Single(run.Request.SelectMany(message => message.Contents).OfType<DataContent>());
            Assert.Equal("image/png", data.MediaType);
            Assert.Equal(AssetBytes, System.Text.Encoding.UTF8.GetString(data.Data.Span));
        }
    }

    [Fact]
    public async Task Turning_reference_mode_off_rolls_both_runtimes_back_to_marker_text()
    {
        // The rollback switch is read by each runtime's own write path, so this is the case most likely
        // to be honoured on one orchestrator and forgotten on the other.
        var agent = await RunAsync(useMaf: false, AttachmentText(), referenceMode: false);
        var maf = await RunAsync(useMaf: true, AttachmentText(), referenceMode: false);

        foreach (var run in new[] { agent, maf })
        {
            var turn = FirstUserTurn(run.Session);
            Assert.Contains("[IMAGE_PATH:", turn.Content, StringComparison.Ordinal);
            Assert.Null(turn.Attachments);
        }
    }

    // ── Harness ──────────────────────────────────────────────────────────────

    private string AttachmentText() => $"look at this\n[IMAGE_PATH:{Path.Join(_cacheRoot, AssetName)}]";

    private static ChatTurn FirstUserTurn(Session session)
        => Assert.Single(session.History, turn => turn.Role == "user");

    private sealed record ParityRun(Session Session, IReadOnlyList<ChatMessage> Request);

    private async Task<ParityRun> RunAsync(bool useMaf, string inboundText, bool referenceMode = true)
    {
        var storagePath = Path.Join(_workspace, $"sessions-{(useMaf ? "maf" : "agent")}-{referenceMode}");
        Directory.CreateDirectory(storagePath);

        var config = new GatewayConfig
        {
            Memory = new MemoryConfig { StoragePath = storagePath },
            Multimodal = new MultimodalConfig
            {
                MediaCachePath = _cacheRoot,
                Attachments = new AttachmentConfig { ReferenceMode = referenceMode }
            }
        };

        var session = new Session { Id = $"parity-{(useMaf ? "maf" : "agent")}", ChannelId = "websocket", SenderId = "user" };
        var captured = new List<ChatMessage>();

        if (useMaf)
        {
            var runtime = BuildMafRuntime(config, storagePath, captured);
            await runtime.RunAsync(session, inboundText, TestContext.Current.CancellationToken);
        }
        else
        {
            var runtime = new AgentRuntime(
                new RecordingChatClient(captured),
                [],
                new FileMemoryStore(storagePath, 4),
                config.Llm,
                maxHistoryTurns: 20,
                gatewayConfig: config);
            await runtime.RunAsync(session, inboundText, TestContext.Current.CancellationToken);
        }

        return new ParityRun(session, captured);
    }

    /// <summary>
    /// Smallest MAF graph that completes a turn: no tools, no skills, no routing policy, one in-memory
    /// service provider. Everything the runtime reads from DI has a documented fallback, so this is
    /// enough to exercise the write and read paths without standing up the Gateway.
    /// </summary>
    private static MafAgentRuntime BuildMafRuntime(GatewayConfig config, string storagePath, List<ChatMessage> captured)
    {
        var services = new ServiceCollection().BuildServiceProvider();
        var options = new MafOptions();

        return new MafAgentRuntime(
            new AgentRuntimeFactoryContext
            {
                Services = services,
                Config = config,
                RuntimeState = new GatewayRuntimeState
                {
                    RequestedMode = "jit",
                    EffectiveMode = GatewayRuntimeMode.Jit,
                    DynamicCodeSupported = true
                },
                ChatClient = new RecordingChatClient(captured),
                Tools = [],
                MemoryStore = new FileMemoryStore(storagePath, 4),
                RuntimeMetrics = new RuntimeMetrics(),
                ProviderUsage = new ProviderUsageTracker(),
                LlmExecutionService = new RecordingExecutionService(captured),
                Skills = [],
                SkillsConfig = new SkillsConfig(),
                WorkspacePath = null,
                PluginSkillDirs = [],
                Logger = NullLogger.Instance,
                Hooks = [],
                RequireToolApproval = false,
                ApprovalRequiredTools = [],
                IsContractTokenBudgetExceeded = null,
                IsContractRuntimeBudgetExceeded = null,
                RecordContractTurnUsage = null,
                AppendContractSnapshot = null
            },
            options,
            new MafAgentFactory(Options.Create(options), NullLoggerFactory.Instance, services),
            new MafSessionStateStore(config, Options.Create(options), NullLogger<MafSessionStateStore>.Instance),
            new MafTelemetryAdapter(),
            NullLogger<MafAgentRuntime>.Instance);
    }

    /// <summary>Chat client that answers immediately and records what it was asked.</summary>
    private sealed class RecordingChatClient(List<ChatMessage> captured) : IChatClient
    {
        public Task<ChatResponse> GetResponseAsync(
            IEnumerable<ChatMessage> messages,
            ChatOptions? options = null,
            CancellationToken cancellationToken = default)
        {
            _ = options;
            captured.AddRange(messages);
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

    /// <summary>
    /// The MAF runtime reaches the model through this service rather than an <see cref="IChatClient"/>,
    /// which is why the two runtimes cannot share one capture point.
    /// </summary>
    private sealed class RecordingExecutionService(List<ChatMessage> captured) : ILlmExecutionService
    {
        public CircuitState DefaultCircuitState => CircuitState.Closed;

        public Task<LlmExecutionResult> GetResponseAsync(
            Session session,
            IReadOnlyList<ChatMessage> messages,
            ChatOptions options,
            TurnContext turnContext,
            LlmExecutionEstimate estimate,
            CancellationToken ct)
        {
            _ = session;
            _ = options;
            _ = turnContext;
            _ = estimate;
            _ = ct;
            captured.AddRange(messages);
            return Task.FromResult(new LlmExecutionResult
            {
                ProviderId = "test-parity",
                ModelId = "parity-model",
                Response = new ChatResponse([new ChatMessage(ChatRole.Assistant, "ok")])
            });
        }

        public Task<LlmStreamingExecutionResult> StartStreamingAsync(
            Session session,
            IReadOnlyList<ChatMessage> messages,
            ChatOptions options,
            TurnContext turnContext,
            LlmExecutionEstimate estimate,
            CancellationToken ct)
        {
            _ = session;
            _ = messages;
            _ = options;
            _ = turnContext;
            _ = estimate;
            _ = ct;
            return Task.FromResult(new LlmStreamingExecutionResult
            {
                ProviderId = "test-parity",
                ModelId = "parity-model",
                Updates = AsyncEnumerable.Empty<ChatResponseUpdate>()
            });
        }
    }
}

using System.Net;
using System.Text.Json;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.DependencyInjection;
using OpenClaw.Agent.Routing;
using OpenClaw.Core.Models;
using OpenClaw.Core.Security;
using OpenClaw.Core.Validation;
using OpenClaw.Gateway.Routing;
using OpenClaw.Routing.Decisions;
using Xunit;

namespace OpenClaw.Tests;

public sealed class JevRoutingTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Theory]
    [InlineData("noul", "{\"type\":\"noul\",\"noul\":0.7}", true)]
    [InlineData("noul", "{\"type\":\"noul\",\"noul\":1.1}", false)]
    [InlineData("score", "{\"type\":\"score\",\"score\":0.8,\"confidence\":0.7,\"probabilities\":{\"0\":0.2,\"1\":0.8},\"legend\":{\"0\":\"Low\",\"1\":\"High\"}}", true)]
    [InlineData("score", "{\"type\":\"score\",\"score\":0.8,\"confidence\":0.7,\"probabilities\":{\"0\":0.2,\"1\":0.8}}", false)]
    public async Task DecisionClientSupportsNoulAndScore_WithTypedValidation(string type, string answer, bool valid)
    {
        using var criteria = JsonDocument.Parse("[\"Low\",\"High\"]");
        using var state = JsonDocument.Parse("\"A sample to judge.\"");
        var handler = new Handler
        {
            ResponseBody = "{\"model\":\"jev-1.13.0\",\"answers\":{\"question\":" + answer + "},\"usage\":{\"input_tokens\":100,\"output_tokens\":10}}"
        };
        using var client = new TypeSafeDecisionClient(new HttpClient(handler), new Uri("https://api.typesafe.ai/v1/systemone"), _ => ValueTask.FromResult<string?>("test"));
        var request = new DecisionRequest
        {
            Model = "jev-1.13.0", State = state.RootElement.Clone(),
            Questions = new() { ["question"] = new() { Type = type, Instructions = "Judge this sample.", Criteria = type == "score" ? criteria.RootElement.Clone() : null } }
        };
        if (valid)
            Assert.Equal(type, (await client.EvaluateAsync(request, Ct)).Answers["question"].Type);
        else
            await Assert.ThrowsAsync<DecisionException>(() => client.EvaluateAsync(request, Ct));
    }

    [Fact]
    public async Task OversizedResponseFallsBackBeforeDeserialization()
    {
        using var harness = new Harness("active", "T0");
        harness.Handler.ResponseBody = new string('x', 256 * 1024 + 1);
        Assert.Same(harness.Baseline.Decision, await harness.Policy.ResolveAsync(Request(), Ct));
        Assert.Equal("response_too_large", Assert.Single(harness.Observer.Items).Reason);
    }

    [Fact]
    public async Task Shadow_RecordsProposalAndUsage_ButReturnsExactBaseline()
    {
        using var harness = new Harness("shadow", "T0");
        var request = Request();
        var decision = await harness.Policy.ResolveAsync(request, Ct);

        Assert.Same(harness.Baseline.Decision, decision);
        Assert.Null(request.Session.ModelProfileId);
        var observation = Assert.Single(harness.Observer.Items);
        Assert.Equal("T0", observation.ProposedTier);
        Assert.Equal("T2", observation.AppliedTier);
        Assert.Equal("jev-1.13.0", observation.Model);
        Assert.Equal(0.000084m, observation.EstimatedCostUsd);
        Assert.Equal(2000, observation.InputTokens);
        Assert.Equal(HttpMethod.Post, harness.Handler.Method);
        Assert.Equal("https://api.typesafe.ai/v1/systemone", harness.Handler.Uri!.AbsoluteUri);
        Assert.Equal("Bearer test-key", harness.Handler.Authorization);
    }

    [Fact]
    public async Task Active_OnlyChangesModelAndReasoning_PreservesBaselineToolScope()
    {
        using var harness = new Harness("active", "T0");
        var baseline = harness.Baseline.Decision;
        var decision = await harness.Policy.ResolveAsync(Request(), Ct);
        Assert.Equal("profile-0", decision.ModelProfileId);
        Assert.Equal("low", decision.ReasoningLevel);
        Assert.Equal(baseline.DisableTools, decision.DisableTools);
        Assert.Same(baseline.AllowedTools, decision.AllowedTools);
        Assert.Same(baseline.PreferredTags, decision.PreferredTags);
        Assert.Equal(baseline.SystemPromptSuffix, decision.SystemPromptSuffix);
        Assert.Equal(baseline.ResponsePolicy, decision.ResponsePolicy);
    }

    [Theory]
    [InlineData("uncertain", "T0", 0.9)]
    [InlineData("abstain", "abstain", 0.99)]
    public async Task UncertainOrAbstainedDecision_PreservesBaseline(string reason, string choice, double confidence)
    {
        using var harness = new Harness("active", choice, confidence);
        Assert.Same(harness.Baseline.Decision, await harness.Policy.ResolveAsync(Request(), Ct));
        Assert.Equal(reason, Assert.Single(harness.Observer.Items).Reason);
    }

    [Fact]
    public async Task Upgrade_UsesLowerConfidenceThresholdThanDowngrade()
    {
        using var harness = new Harness("active", "T3", 0.9);
        Assert.Equal("T3", (await harness.Policy.ResolveAsync(Request(), Ct)).Tier);
    }

    [Theory]
    [InlineData("Deploy this change to production.", null, "T2")]
    [InlineData("Compare architecture and plan the migration.", null, "T3")]
    [InlineData("Make this sentence shorter.", "T3", "T3")]
    public async Task DeterministicFloorsSurviveAConfidentCheapPrediction(string text, string? previousTier, string expected)
    {
        using var harness = new Harness("active", "T0");
        var request = Request(text);
        request.Session.RouteModelTier = previousTier;
        request.Session.RouteReason = previousTier is null ? null : "jev";
        Assert.Equal(expected, (await harness.Policy.ResolveAsync(request, Ct)).Tier);
        Assert.Equal("safety_floor", Assert.Single(harness.Observer.Items).Reason);
    }

    [Fact]
    public async Task SemanticRiskRaisesFloor()
    {
        using var harness = new Harness("active", "T0", highRisk: 0.7);
        Assert.Equal("T2", (await harness.Policy.ResolveAsync(Request(), Ct)).Tier);
    }

    [Fact]
    public async Task ToolNeedRaisesFloor()
    {
        using var harness = new Harness("active", "T0", requiresTools: 0.9);
        Assert.Equal("T1", (await harness.Policy.ResolveAsync(Request(), Ct)).Tier);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task ExplicitModelSelectionIsPreserved(bool profile)
    {
        using var harness = new Harness("active", "T0");
        var request = Request();
        if (profile) request.Session.ModelProfileId = "pinned";
        else request.Session.ModelOverride = "pinned";
        Assert.Same(harness.Baseline.Decision, await harness.Policy.ResolveAsync(request, Ct));
        Assert.Equal("explicit_model_selection", Assert.Single(harness.Observer.Items).Reason);
        Assert.Equal(0, harness.Handler.Calls);
    }

    [Fact]
    public async Task StickyTierOnlyUsesAConfirmedDecisionProviderRoute()
    {
        using var harness = new Harness("active", "T0");
        var request = Request();
        request.Session.RouteModelTier = "T3";
        request.Session.RouteReason = "default";
        Assert.Equal("T0", (await harness.Policy.ResolveAsync(request, Ct)).Tier);
        request.Session.RouteReason = "jev";
        Assert.Equal("T3", (await harness.Policy.ResolveAsync(request, Ct)).Tier);
    }

    [Fact]
    public async Task DeepConversationFloorCountsPriorUserMessagesOnly()
    {
        using var harness = new Harness("active", "T0", configurePolicy: p => p.DeepConversationTurnIndexThreshold = 2);
        var messages = new List<ChatMessage>
        {
            new(ChatRole.User, "Prior question."),
            new(ChatRole.Assistant, "Prior answer."),
            new(ChatRole.User, "Current question.")
        };
        var request = Request("Current question.", messages);
        request.Session.History.AddRange([
            new ChatTurn { Role = "user", Content = "Prior question." },
            new ChatTurn { Role = "user", Content = "Current question." }
        ]);
        Assert.Equal("T0", (await harness.Policy.ResolveAsync(request, Ct)).Tier);
    }

    [Fact]
    public async Task RedactionExpansionDoesNotOpenTheProviderCircuit()
    {
        var redactor = new RedactionPipeline([new ExpandingRedactor()]);
        using var harness = new Harness("active", "T0", configure: c =>
        {
            c.MaxStateChars = 256;
            c.CircuitFailureThreshold = 1;
        }, redactor: redactor);
        Assert.Same(harness.Baseline.Decision, await harness.Policy.ResolveAsync(Request("expand"), Ct));
        Assert.Equal("request_too_large", Assert.Single(harness.Observer.Items).Reason);
        Assert.Equal("T0", (await harness.Policy.ResolveAsync(Request("short"), Ct)).Tier);
        Assert.Equal(1, harness.Handler.Calls);
    }

    [Fact]
    public async Task SecretResolutionFailureFallsBackWithoutEscapingTheRouter()
    {
        using var harness = new Harness("active", "T0", secretFailure: true);
        Assert.Same(harness.Baseline.Decision, await harness.Policy.ResolveAsync(Request(), Ct));
        Assert.Equal("request_failed", Assert.Single(harness.Observer.Items).Reason);
    }

    [Fact]
    public async Task OutboundStateIsBoundedAndRedacted_ExcludesSystemAndToolSummaries()
    {
        using var harness = new Harness("shadow", "T0");
        var request = Request("Rephrase Authorization: Bearer super-secret-token", new List<ChatMessage>
        {
            new(ChatRole.System, "private system prompt"),
            new(ChatRole.Assistant, "[Previous tool calls:\nprivate tool output]"),
            new(ChatRole.User, "The prior request."),
            new(ChatRole.Assistant, "A reply.")
        });
        await harness.Policy.ResolveAsync(request, Ct);
        Assert.DoesNotContain("super-secret-token", harness.Handler.Body);
        Assert.DoesNotContain("private system", harness.Handler.Body);
        Assert.DoesNotContain("private tool", harness.Handler.Body);
        Assert.Contains("REDACTED", harness.Handler.Body);
        using var body = JsonDocument.Parse(harness.Handler.Body!);
        Assert.Equal("jev-1.13.0", body.RootElement.GetProperty("model").GetString());
        Assert.Equal(2, body.RootElement.GetProperty("state").GetProperty("recent_conversation").GetArrayLength());
        Assert.Equal(3, body.RootElement.GetProperty("questions").EnumerateObject().Count());
    }

    [Fact]
    public async Task OmittedHistoryPreventsDowngrade()
    {
        using var harness = new Harness("active", "T0", configure: c => c.HistoryMessages = 1);
        var request = Request(messages: new List<ChatMessage> { new(ChatRole.User, "earlier"), new(ChatRole.Assistant, "reply") });
        Assert.Same(harness.Baseline.Decision, await harness.Policy.ResolveAsync(request, Ct));
        Assert.True(Assert.Single(harness.Observer.Items).ContextTruncated);
        Assert.Equal("incomplete_context", harness.Observer.Items[0].Reason);
    }

    [Theory]
    [InlineData("disabled")]
    [InlineData("request_too_large")]
    [InlineData("non_text_input")]
    [InlineData("tool_continuation")]
    public async Task IneligibleInputsMakeNoNetworkRequest(string scenario)
    {
        using var harness = new Harness(scenario == "disabled" ? "disabled" : "active", "T0");
        var messages = new List<ChatMessage>();
        if (scenario == "non_text_input")
            messages.Add(new ChatMessage(ChatRole.User, new AIContent[] { new UriContent(new Uri("https://example.com/image.png"), "image/png") }));
        if (scenario == "tool_continuation") messages.Add(new(ChatRole.Tool, "result"));
        var request = Request(scenario == "request_too_large" ? new string('x', 12001) : "Hi", messages);
        Assert.Same(harness.Baseline.Decision, await harness.Policy.ResolveAsync(request, Ct));
        Assert.Equal(0, harness.Handler.Calls);
    }

    [Theory]
    [InlineData(401)]
    [InlineData(429)]
    [InlineData(529)]
    public async Task HttpFailuresFallBackWithoutRetryingOrLoggingBody(int status)
    {
        using var harness = new Harness("active", "T0");
        harness.Handler.Status = status;
        harness.Handler.ResponseBody = "upstream-sensitive-content";
        Assert.Same(harness.Baseline.Decision, await harness.Policy.ResolveAsync(Request(), Ct));
        Assert.Equal(1, harness.Handler.Calls);
        Assert.Equal($"http_{status}", Assert.Single(harness.Observer.Items).Reason);
    }

    [Theory]
    [InlineData("{}")]
    [InlineData("null")]
    [InlineData("<html>error</html>")]
    public async Task MalformedResponsesFallBack(string body)
    {
        using var harness = new Harness("active", "T0");
        harness.Handler.ResponseBody = body;
        Assert.Same(harness.Baseline.Decision, await harness.Policy.ResolveAsync(Request(), Ct));
        Assert.Equal("invalid_response", Assert.Single(harness.Observer.Items).Reason);
    }

    [Theory]
    [InlineData("model", "model_version_mismatch")]
    [InlineData("choice", "invalid_choice")]
    [InlineData("probabilities", "invalid_probabilities")]
    [InlineData("missing_answer", "invalid_response")]
    public async Task InvalidTypedAnswersCannotAffectRouting(string corruption, string reason)
    {
        using var harness = new Harness("active", "T0");
        var json = System.Text.Json.Nodes.JsonNode.Parse(harness.Handler.ResponseBody)!;
        if (corruption == "model") json["model"] = "jev-9.0.0";
        if (corruption == "choice") json["answers"]!["tier"]!["choice"] = "arbitrary-profile";
        if (corruption == "probabilities") json["answers"]!["tier"]!["probabilities"]!["T0"] = -1;
        if (corruption == "missing_answer") json["answers"]!.AsObject().Remove("high_risk");
        harness.Handler.ResponseBody = json.ToJsonString();
        Assert.Same(harness.Baseline.Decision, await harness.Policy.ResolveAsync(Request(), Ct));
        Assert.Equal(reason, Assert.Single(harness.Observer.Items).Reason);
    }

    [Fact]
    public async Task MissingKeyDoesNotSendRequest()
    {
        using var harness = new Harness("active", "T0", key: null);
        Assert.Same(harness.Baseline.Decision, await harness.Policy.ResolveAsync(Request(), Ct));
        Assert.Equal(0, harness.Handler.Calls);
        Assert.Equal("missing_api_key", Assert.Single(harness.Observer.Items).Reason);
    }

    [Fact]
    public async Task TimeoutFallsBack_ButCallerCancellationPropagates()
    {
        using var harness = new Harness("active", "T0", configure: c => c.TimeoutMs = 30);
        harness.Handler.Block = true;
        Assert.Same(harness.Baseline.Decision, await harness.Policy.ResolveAsync(Request(), Ct));
        Assert.Equal("timeout", Assert.Single(harness.Observer.Items).Reason);
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(Ct);
        await cts.CancelAsync();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => harness.Policy.ResolveAsync(Request(), cts.Token).AsTask());
    }

    [Fact]
    public async Task CircuitSkipsRequestsAndRecoversAfterCooldown()
    {
        var clock = new FakeClock();
        using var harness = new Harness("active", "T0", configure: c => c.CircuitFailureThreshold = 1, clock: clock);
        harness.Handler.Status = 529;
        await harness.Policy.ResolveAsync(Request(), Ct);
        await harness.Policy.ResolveAsync(Request(), Ct);
        Assert.Equal(1, harness.Handler.Calls);
        Assert.Equal("circuit_open", harness.Observer.Items[^1].Reason);
        clock.Now += TimeSpan.FromSeconds(31);
        harness.Handler.Status = 200;
        Assert.Equal("T0", (await harness.Policy.ResolveAsync(Request(), Ct)).Tier);
        Assert.Equal(2, harness.Handler.Calls);
    }

    [Fact]
    public async Task ConcurrencyLimitDoesNotQueueAgentTurns()
    {
        using var harness = new Harness("active", "T0", configure: c => c.MaxConcurrentRequests = 1);
        harness.Handler.Block = true;
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(Ct);
        var first = harness.Policy.ResolveAsync(Request(), cts.Token).AsTask();
        await harness.Handler.Entered.Task.WaitAsync(Ct);
        Assert.Same(harness.Baseline.Decision, await harness.Policy.ResolveAsync(Request(), Ct));
        Assert.Equal("concurrency_limit", Assert.Single(harness.Observer.Items).Reason);
        await cts.CancelAsync();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => first);
    }

    [Fact]
    public void Composition_DisabledDoesNotRegisterHostedClient_ShadowWorksWithoutOnnx()
    {
        var disabled = new ServiceCollection().AddLogging().AddDynamicTurnRouting(new(), "/tmp");
        using var disabledServices = disabled.BuildServiceProvider();
        Assert.Same(NoopTurnRoutingPolicy.Instance, disabledServices.GetRequiredService<ITurnRoutingPolicy>());
        Assert.Null(disabledServices.GetService<IDecisionClient>());

        var config = new DynamicTurnRoutingConfig { Jev = new() { Mode = "shadow", DiagnosticsPath = "" } };
        var enabled = new ServiceCollection().AddLogging();
        enabled.AddSingleton<IRedactionPipeline>(new NoopRedactionPipeline());
        enabled.AddDynamicTurnRouting(config, "/tmp");
        using var enabledServices = enabled.BuildServiceProvider();
        Assert.IsType<DecisionTurnRoutingPolicy>(enabledServices.GetRequiredService<ITurnRoutingPolicy>());
    }

    [Theory]
    [InlineData("http://remote.example/v1/systemone")]
    [InlineData("https://user:secret@example.com/v1/systemone")]
    [InlineData("https://example.com/v1/systemone?key=secret")]
    public void ConfigurationRejectsUnsafeEndpoint(string endpoint)
        => Assert.Throws<ArgumentException>(() => JevRoutingConfiguration.Validate(new() { Endpoint = endpoint }));

    [Fact]
    public void ConfigValidationIncludesJevWithoutOnnx_AndConfigurationRoundTrips()
    {
        var config = new GatewayConfig();
        config.DynamicTurnRouting.Jev.Mode = "active";
        config.DynamicTurnRouting.Policy.Tiers.T0.ModelProfileId = "missing";
        var errors = ConfigValidator.Validate(config);
        Assert.Contains(errors, error => error.Contains("Policy.Tiers.T0.ModelProfileId", StringComparison.Ordinal));
        var json = JsonSerializer.Serialize(config, CoreJsonContext.Default.GatewayConfig);
        var restored = JsonSerializer.Deserialize(json, CoreJsonContext.Default.GatewayConfig)!;
        Assert.Equal("active", restored.DynamicTurnRouting.Jev.Mode);
        Assert.Equal("env:TYPESAFE_API_KEY", restored.DynamicTurnRouting.Jev.ApiKeyRef);
        config.DynamicTurnRouting.Jev.MinConfidence = double.NaN;
        Assert.Contains(ConfigValidator.Validate(config), error => error.StartsWith("DynamicTurnRouting.Jev:", StringComparison.Ordinal));
    }

    private static TurnRoutingRequest Request(string text = "Make this sentence shorter.", IReadOnlyList<ChatMessage>? messages = null) => new()
    {
        Session = new Session { Id = "test-session", ChannelId = "test", SenderId = "test" },
        UserMessage = text, Messages = messages ?? [], BaseOptions = new ChatOptions()
    };

    private sealed class Harness : IDisposable
    {
        public Handler Handler { get; } = new();
        public FixedPolicy Baseline { get; } = new();
        public Observer Observer { get; } = new();
        public DecisionTurnRoutingPolicy Policy { get; }
        private readonly HttpClient _http;

        public Harness(string mode, string choice, double confidence = 0.99, double highRisk = 0.01,
            double requiresTools = 0.01, Action<JevRoutingConfig>? configure = null, string? key = "test-key", TimeProvider? clock = null,
            Action<DynamicTurnRoutingPolicyConfig>? configurePolicy = null, IRedactionPipeline? redactor = null, bool secretFailure = false)
        {
            var probabilities = new[] { "T0", "T1", "T2", "T3", "abstain" }.ToDictionary(tier => tier, tier => tier == choice ? 0.96 : 0.01);
            Handler.ResponseBody = JsonSerializer.Serialize(new
            {
                model = "jev-1.13.0",
                answers = new
                {
                    tier = new { type = "choice", choice, confidence, probabilities },
                    high_risk = new { type = "noul", noul = highRisk },
                    requires_tools = new { type = "noul", noul = requiresTools }
                },
                usage = new { input_tokens = 2000, output_tokens = 30 }
            });
            _http = new HttpClient(Handler);
            var config = new JevRoutingConfig { Mode = mode };
            configure?.Invoke(config);
            var policy = new DynamicTurnRoutingPolicyConfig
            {
                Tiers = new()
                {
                    T0 = new() { ModelProfileId = "profile-0", ReasoningLevel = "low", DisableTools = true },
                    T1 = new() { ModelProfileId = "profile-1" },
                    T2 = new() { ModelProfileId = "profile-2" },
                    T3 = new() { ModelProfileId = "profile-3", ReasoningLevel = "high" }
                }
            };
            configurePolicy?.Invoke(policy);
            Policy = new(config, policy, Baseline,
                new TypeSafeDecisionClient(_http, new Uri(config.Endpoint), _ => secretFailure
                    ? throw new SecretResolutionException("secret backend unavailable")
                    : ValueTask.FromResult(key)),
                redactor ?? new RedactionPipeline([new BaselineSecretRedactor()]), Observer, clock);
        }

        public void Dispose() { Policy.Dispose(); _http.Dispose(); }
    }

    private sealed class FixedPolicy : ITurnRoutingPolicy
    {
        public TurnRoutingDecision Decision { get; } = new()
        {
            Tier = "T2", ModelProfileId = "baseline-profile", AllowedTools = ["read_file"],
            PreferredTags = ["baseline"], SystemPromptSuffix = "baseline prompt", ResponsePolicy = "concise"
        };
        public ValueTask<TurnRoutingDecision> ResolveAsync(TurnRoutingRequest request, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return ValueTask.FromResult(Decision);
        }
    }

    private sealed class ExpandingRedactor : ISensitiveDataRedactor
    {
        public string Name => "expanding-test";
        public string Redact(string? value) => value == "expand" ? new string('x', 257) : value ?? string.Empty;
    }

    private sealed class Observer : IDecisionRoutingObserver
    {
        public List<DecisionRoutingDiagnostic> Items { get; } = [];
        public void Record(DecisionRoutingDiagnostic diagnostic) => Items.Add(diagnostic);
    }

    private sealed class FakeClock : TimeProvider
    {
        public DateTimeOffset Now { get; set; } = DateTimeOffset.UtcNow;
        public override DateTimeOffset GetUtcNow() => Now;
    }

    private sealed class Handler : HttpMessageHandler
    {
        public int Calls;
        public int Status { get; set; } = 200;
        public bool Block { get; set; }
        public string ResponseBody { get; set; } = "";
        public string? Body { get; private set; }
        public string? Authorization { get; private set; }
        public HttpMethod? Method { get; private set; }
        public Uri? Uri { get; private set; }
        public TaskCompletionSource Entered { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Interlocked.Increment(ref Calls);
            Body = await request.Content!.ReadAsStringAsync(cancellationToken);
            Authorization = request.Headers.Authorization?.ToString();
            Method = request.Method;
            Uri = request.RequestUri;
            Entered.TrySetResult();
            if (Block) await Task.Delay(Timeout.Infinite, cancellationToken);
            return new HttpResponseMessage((HttpStatusCode)Status) { Content = new StringContent(ResponseBody) };
        }
    }
}

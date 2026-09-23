using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
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

public sealed class LayaRoutingTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;
    private static readonly FixedPolicy Baseline = new();
    private const string Calibration = "aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa";
    private const string RubricHash = "8c8de008c11734cf12b58c7914581ea2779c2e44356f3c5b1cf79df7fa953d9c";

    [Theory]
    [InlineData("http://example.com/v1/decisions")]
    [InlineData("https://example.com/v1/decisions")]
    [InlineData("http://localhost/v1/decisions")]
    [InlineData("http://127.0.0.1/v1/decisions?secret=x")]
    [InlineData("http://user:secret@127.0.0.1/v1/decisions")]
    public void LocalEndpointCannotSendToRemoteOrDnsHost(string endpoint)
        => Assert.Throws<ArgumentException>(() => DecisionRoutingConfiguration.Validate(new LayaRoutingConfig { Endpoint = endpoint }));

    [Fact]
    public void ActiveRequiresCalibration_ProvidersAreExclusive_AndConfigRoundTrips()
    {
        var config = new GatewayConfig();
        config.DynamicTurnRouting.Laya.Mode = "active";
        Assert.Contains(ConfigValidator.Validate(config), error => error.Contains("CalibrationId", StringComparison.Ordinal));
        config.DynamicTurnRouting.Laya.CalibrationId = Calibration;
        config.DynamicTurnRouting.Laya.Language = "de-DE";
        var restored = JsonSerializer.Deserialize(JsonSerializer.Serialize(config, CoreJsonContext.Default.GatewayConfig), CoreJsonContext.Default.GatewayConfig)!;
        Assert.Equal(Calibration, restored.DynamicTurnRouting.Laya.CalibrationId);
        Assert.Equal("de-DE", restored.DynamicTurnRouting.Laya.Language);
        config.DynamicTurnRouting.Jev.Mode = "shadow";
        Assert.Contains(ConfigValidator.Validate(config), error => error.Contains("only one decision provider", StringComparison.Ordinal));
        Assert.Throws<ArgumentException>(() => new ServiceCollection().AddDynamicTurnRouting(config.DynamicTurnRouting, "/tmp"));
    }

    [Fact]
    public void LocalCompositionDoesNotRequireHostedCredentials()
    {
        var services = new ServiceCollection().AddLogging();
        services.AddSingleton<IRedactionPipeline>(new NoopRedactionPipeline());
        services.AddDynamicTurnRouting(new() { Laya = new() { Mode = "shadow", DiagnosticsPath = "" } }, "/tmp");
        using var provider = services.BuildServiceProvider();
        Assert.IsType<LayaDecisionClient>(provider.GetRequiredService<IDecisionClient>());
        Assert.IsType<DecisionTurnRoutingPolicy>(provider.GetRequiredService<ITurnRoutingPolicy>());
    }

    [Theory]
    [InlineData("shadow")]
    [InlineData("active")]
    public async Task LocalPolicyUsesPinnedMetadataAndPreservesBaselinePermissions(string mode)
    {
        var config = new LayaRoutingConfig { Mode = mode, CalibrationId = Calibration, Language = "en" };
        using var handler = new Handler(config);
        using var client = new LayaDecisionClient(new HttpClient(handler), config);
        var observer = new Observer();
        using var policy = CreatePolicy(config, client, observer);
        var decision = await policy.ResolveAsync(Request(), Ct);
        Assert.Equal(mode == "active" ? "local-small" : "baseline", decision.ModelProfileId);
        Assert.True(decision.DisableTools);
        Assert.Equal(new[] { "read_file" }, decision.AllowedTools);
        Assert.Null(handler.Authorization);
        Assert.True(handler.ContentLength > 0);
        Assert.Equal(System.Text.Encoding.UTF8.GetByteCount(handler.Body!), handler.ContentLength);
        using var body = JsonDocument.Parse(handler.Body!);
        Assert.Equal("en", body.RootElement.GetProperty("language").GetString());
        Assert.Equal("openclaw-laya-tiers-v1", body.RootElement.GetProperty("rubric_version").GetString());
        Assert.Equal("laya", Assert.Single(observer.Items).Provider);
        Assert.Equal("english", observer.Items[0].Metadata!.Checkpoint);
        Assert.Equal("openclaw-laya-tiers-v1", observer.Items[0].RubricVersion);
        Assert.Equal(0, observer.Items[0].EstimatedCostUsd);
    }

    [Theory]
    [InlineData(false, RubricHash)]
    [InlineData(true, "2089e651a62138bf97aaf6c0ee38e4791540f7ec372064384cc7caf831b9da9c")]
    public async Task SchemaHashMatchesPythonGoldenValues(bool unicode, string expectedHash)
    {
        var config = new LayaRoutingConfig();
        using var handler = new Handler(config);
        handler.Result["metadata"]!["schema_hash"] = expectedHash;
        using var client = new LayaDecisionClient(new HttpClient(handler), config);
        using var stream = typeof(DecisionTurnRoutingPolicy).Assembly.GetManifestResourceStream("OpenClaw.LayaRoutingRubric.json")!;
        using var rubric = JsonDocument.Parse(stream);
        var questions = new Dictionary<string, DecisionQuestion>();
        foreach (var item in rubric.RootElement.GetProperty("questions").EnumerateObject())
        {
            questions[item.Name] = new()
            {
                Type = item.Value.GetProperty("type").GetString()!,
                Instructions = unicode && item.Name == "tier"
                    ? "Judge l'utilisateur <x> & C++ café 中文 😀\u2028\u0001\n\t"
                    : item.Value.GetProperty("instructions").GetString()!,
                Criteria = item.Value.TryGetProperty("criteria", out var criteria) ? criteria.Clone() : null
            };
        }
        using var state = JsonDocument.Parse("\"hello\"");
        var result = await client.EvaluateAsync(new()
        {
            Model = config.Model, State = state.RootElement.Clone(), Questions = questions,
            RubricVersion = "openclaw-laya-tiers-v1"
        }, Ct);
        Assert.Equal(expectedHash, result.Metadata!.SchemaHash);
    }

    [Theory]
    [InlineData('x', 31990)]
    [InlineData('\n', 16000)]
    [InlineData('中', 11000)]
    public async Task OversizedSerializedStateDoesNotReachServiceOrOpenCircuit(char character, int count)
    {
        var config = new LayaRoutingConfig { Mode = "active", CalibrationId = Calibration, MaxStateChars = 32000, CircuitFailureThreshold = 1 };
        using var handler = new Handler(config);
        using var client = new LayaDecisionClient(new HttpClient(handler), config);
        var observer = new Observer();
        using var policy = CreatePolicy(config, client, observer);
        var oversized = new TurnRoutingRequest
        {
            Session = Request().Session, UserMessage = "prefix" + new string(character, count),
            Messages = [], BaseOptions = new ChatOptions()
        };
        Assert.Same(Baseline.Decision, await policy.ResolveAsync(oversized, Ct));
        Assert.Equal("request_too_large", Assert.Single(observer.Items).Reason);
        Assert.Equal(0, handler.Calls);
        Assert.Equal("local-small", (await policy.ResolveAsync(Request(), Ct)).ModelProfileId);
        Assert.Equal(1, handler.Calls);
    }

    [Theory]
    [InlineData("truncated", "laya_truncated_input")]
    [InlineData("revision", "laya_metadata_mismatch")]
    [InlineData("calibration_id", "laya_calibration_mismatch")]
    [InlineData("schema_hash", "laya_metadata_mismatch")]
    [InlineData("sdk_version", "laya_metadata_mismatch")]
    [InlineData("missing_metadata", "laya_metadata_mismatch")]
    [InlineData("http_error", "http_503")]
    public async Task UnusableLocalResponsesKeepBaselineWithoutHostedFallback(string corruption, string expected)
    {
        var config = new LayaRoutingConfig { Mode = "active", CalibrationId = Calibration };
        using var handler = new Handler(config);
        if (corruption == "missing_metadata") handler.Result.Remove("metadata");
        else if (corruption == "http_error") handler.Status = HttpStatusCode.ServiceUnavailable;
        else if (corruption == "truncated") handler.Result["metadata"]![corruption] = true;
        else handler.Result["metadata"]![corruption] = "mismatch";
        using var client = new LayaDecisionClient(new HttpClient(handler), config);
        var observer = new Observer();
        using var policy = CreatePolicy(config, client, observer);
        var actual = await policy.ResolveAsync(Request(), Ct);
        Assert.Same(Baseline.Decision, actual);
        Assert.Equal(expected, Assert.Single(observer.Items).Reason);
        Assert.Equal(1, handler.Calls);
    }

    private static DecisionTurnRoutingPolicy CreatePolicy(LayaRoutingConfig config, IDecisionClient client, Observer observer)
        => new(config, new() { Tiers = new() { T0 = new() { ModelProfileId = "local-small" } } },
            Baseline, client, new NoopRedactionPipeline(), observer);

    private static TurnRoutingRequest Request() => new()
    {
        Session = new Session { Id = "laya-test", ChannelId = "test", SenderId = "test" },
        UserMessage = "Rewrite hello in uppercase.", Messages = [], BaseOptions = new ChatOptions()
    };

    private sealed class FixedPolicy : ITurnRoutingPolicy
    {
        public TurnRoutingDecision Decision { get; } = new() { ModelProfileId = "baseline", DisableTools = true, AllowedTools = ["read_file"] };
        public ValueTask<TurnRoutingDecision> ResolveAsync(TurnRoutingRequest request, CancellationToken cancellationToken)
            => ValueTask.FromResult(Decision);
    }

    private sealed class Observer : IDecisionRoutingObserver
    {
        public List<DecisionRoutingDiagnostic> Items { get; } = [];
        public void Record(DecisionRoutingDiagnostic diagnostic) => Items.Add(diagnostic);
    }

    private sealed class Handler(LayaRoutingConfig config) : HttpMessageHandler
    {
        public int Calls { get; private set; }
        public string? Authorization { get; private set; }
        public string? Body { get; private set; }
        public long? ContentLength { get; private set; }
        public HttpStatusCode Status { get; set; } = HttpStatusCode.OK;
        public JsonObject Result { get; } = JsonNode.Parse(JsonSerializer.Serialize(new
        {
            model = config.Model,
            answers = new
            {
                tier = new { type = "choice", choice = "T0", confidence = .99, probabilities = new Dictionary<string, double> { ["T0"] = .96, ["T1"] = .01, ["T2"] = .01, ["T3"] = .01, ["abstain"] = .01 } },
                high_risk = new { type = "noul", noul = .01 }, requires_tools = new { type = "noul", noul = .01 }
            },
            usage = new { input_tokens = 150, output_tokens = 0 },
            metadata = new { checkpoint = "english", revision = config.Model[5..], calibration_id = Calibration,
                schema_hash = Calibration, rubric_version = "openclaw-laya-tiers-v1", device = "cpu", sdk_version = "0.3.4", truncated = false }
        }))!.AsObject();

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Calls++;
            Authorization = request.Headers.Authorization?.ToString();
            ContentLength = request.Content!.Headers.ContentLength;
            Body = await request.Content.ReadAsStringAsync(cancellationToken);
            if (Result["metadata"]?["schema_hash"]?.GetValue<string>() == Calibration)
            {
                Result["metadata"]!["schema_hash"] = RubricHash;
            }
            return new HttpResponseMessage(Status) { Content = new StringContent(Result.ToJsonString()) };
        }
    }
}

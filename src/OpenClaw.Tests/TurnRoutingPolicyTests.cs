using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging.Abstractions;
using OpenClaw.Agent.Routing;
using OpenClaw.Core.Models;
using OpenClaw.Gateway.Routing;
using OpenClaw.Routing.Onnx;
using Xunit;

namespace OpenClaw.Tests;

public sealed class TurnRoutingPolicyTests
{
    [Fact]
    public async Task ResolveAsync_WhenClassifierAssetsMissing_FallsBackToT2()
    {
        var policy = new OnnxTurnRoutingPolicy(
            new DynamicTurnRoutingConfig
            {
                Enabled = true,
                Assets = new DynamicTurnRoutingAssetsConfig
                {
                    ClassifierModelPath = "missing-classifier.onnx",
                    EmbeddingModelPath = "missing-embedding.onnx",
                    TokenizerPath = "missing-tokenizer.json"
                },
                Policy = new DynamicTurnRoutingPolicyConfig
                {
                    Tiers = BuildTierMap()
                }
            },
            NullLogger<OnnxTurnRoutingPolicy>.Instance);

        var decision = await policy.ResolveAsync(BuildRequest("hello"), TestContext.Current.CancellationToken);

        Assert.Equal("T2", decision.Tier);
        Assert.Equal("classifier_unavailable", decision.Reason);
    }

    [Fact]
    public void IsClassifierFeatureCountCompatible_WhenExpectedCountDiffers_ReturnsFalse()
    {
        Assert.True(OnnxTurnRoutingPolicy.IsClassifierFeatureCountCompatible(null));
        Assert.True(OnnxTurnRoutingPolicy.IsClassifierFeatureCountCompatible(PromptFeatureExtractor.FeatureVectorDimensions));
        Assert.False(OnnxTurnRoutingPolicy.IsClassifierFeatureCountCompatible(PromptFeatureExtractor.FeatureVectorDimensions + 1));
    }

    [Fact]
    public void BuildFeatureVector_ConcatenatesThreeEmbeddingSegmentsTo1536()
    {
        var input = new RoutingFeatureInput(
            CurrentUserText: "current",
            PriorUserTurns: ["prior"],
            PreviousAssistantText: "assistant",
            TurnIndex: 1,
            PreviousTier: "T1",
            ToolCount: 0,
            ContextTextLength: 0);

        var current = Enumerable.Repeat(0.1f, 512).ToArray();
        var history = Enumerable.Repeat(0.2f, 512).ToArray();
        var assistant = Enumerable.Repeat(0.3f, 512).ToArray();

        var features = PromptFeatureExtractor.BuildFeatureVector(input, current, history, assistant);

        Assert.Equal(1536, features.Length);
        Assert.All(features.Take(512), value => Assert.Equal(0.1f, value));
        Assert.All(features.Skip(512).Take(512), value => Assert.Equal(0.2f, value));
        Assert.All(features.Skip(1024).Take(512), value => Assert.Equal(0.3f, value));
    }

    [Fact]
    public async Task ResolveAsync_WithCompatBundleAssets_DoesNotReturnClassifierUnavailable()
    {
        var bundlePath = ResolveRepoBundlePath();
        var policy = new OnnxTurnRoutingPolicy(
            new DynamicTurnRoutingConfig
            {
                Enabled = true,
                Assets = new DynamicTurnRoutingAssetsConfig
                {
                    ClassifierModelPath = Path.Join(bundlePath, "classifier.onnx"),
                    EmbeddingModelPath = Path.Join(bundlePath, "embeddings.onnx"),
                    TokenizerPath = Path.Join(bundlePath, "tokenizer.json"),
                    RuntimeConfigPath = Path.Join(bundlePath, "runtime-config.json"),
                    Dimensions = 512
                },
                Policy = new DynamicTurnRoutingPolicyConfig
                {
                    Tiers = BuildTierMap()
                }
            },
            NullLogger<OnnxTurnRoutingPolicy>.Instance);

        var decision = await policy.ResolveAsync(BuildRequest("Read README and summarize the key modules."), TestContext.Current.CancellationToken);

        Assert.True(
            string.Equals(decision.Tier, "T0", StringComparison.Ordinal)
            || string.Equals(decision.Tier, "T1", StringComparison.Ordinal)
            || string.Equals(decision.Tier, "T2", StringComparison.Ordinal)
            || string.Equals(decision.Tier, "T3", StringComparison.Ordinal),
            $"Unexpected tier: {decision.Tier}");
        Assert.NotEqual("classifier_unavailable", decision.Reason);
        Assert.NotEqual("classifier_runtime_error", decision.Reason);
    }

    [Fact]
    public async Task ResolveAsync_WithBundlePathAndNormalizer_UsesRepoCompatBundle()
    {
        var bundlePath = ResolveRepoBundlePath();
        var config = new DynamicTurnRoutingConfig
        {
            Enabled = true,
            BundlePath = bundlePath,
            Policy = new DynamicTurnRoutingPolicyConfig
            {
                Tiers = BuildTierMap()
            }
        };

        var resolved = DynamicTurnRoutingConfigNormalizer.Normalize(config, new OpenSquillaBundleLoader());
        var policy = new OnnxTurnRoutingPolicy(resolved, NullLogger<OnnxTurnRoutingPolicy>.Instance);

        var decision = await policy.ResolveAsync(BuildRequest("Read README and summarize the key modules."), TestContext.Current.CancellationToken);

        Assert.Equal("bundle", resolved.Source);
        Assert.Equal(Path.Join(bundlePath, "classifier.onnx"), resolved.Assets.ClassifierModelPath);
        Assert.Equal(Path.Join(bundlePath, "embeddings.onnx"), resolved.Assets.EmbeddingModelPath);
        Assert.Equal(Path.Join(bundlePath, "tokenizer.json"), resolved.Assets.TokenizerPath);
        Assert.Equal(Path.Join(bundlePath, "runtime-config.json"), resolved.Assets.RuntimeConfigPath);
        Assert.Equal(512, resolved.Assets.EmbeddingDimensions);
        Assert.True(
            string.Equals(decision.Tier, "T0", StringComparison.Ordinal)
            || string.Equals(decision.Tier, "T1", StringComparison.Ordinal)
            || string.Equals(decision.Tier, "T2", StringComparison.Ordinal)
            || string.Equals(decision.Tier, "T3", StringComparison.Ordinal),
            $"Unexpected tier: {decision.Tier}");
        Assert.NotEqual("classifier_unavailable", decision.Reason);
        Assert.NotEqual("classifier_runtime_error", decision.Reason);
    }

    [Fact]
    public async Task ResolveAsync_T0DisableTools_ProducesExplicitDisableToolsDecision()
    {
        var policy = new OnnxTurnRoutingPolicy(
            BuildRoutingConfig(),
            predictedTier: 0,
            NullLogger<OnnxTurnRoutingPolicy>.Instance);

        var decision = await policy.ResolveAsync(BuildRequest("answer directly without tools"), TestContext.Current.CancellationToken);

        Assert.Equal("T0", decision.Tier);
        Assert.True(decision.DisableTools);
        Assert.Empty(decision.AllowedTools);
        Assert.Contains("do not call tools", decision.SystemPromptSuffix, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task ResolveAsync_T2EmptyAllowedTools_RemainsUnspecifiedWithoutDisablingTools()
    {
        var policy = new OnnxTurnRoutingPolicy(
            BuildRoutingConfig(),
            predictedTier: 2,
            NullLogger<OnnxTurnRoutingPolicy>.Instance);

        var decision = await policy.ResolveAsync(BuildRequest("use the default tool route"), TestContext.Current.CancellationToken);

        Assert.Equal("T2", decision.Tier);
        Assert.False(decision.DisableTools);
        Assert.Empty(decision.AllowedTools);
    }

    [Fact]
    public async Task ResolveAsync_T1Request_UsesConfiguredAllowedTools_AndPromptMode()
    {
        var policy = new OnnxTurnRoutingPolicy(
            BuildRoutingConfig(),
            predictedTier: 1,
            NullLogger<OnnxTurnRoutingPolicy>.Instance);

        var decision = await policy.ResolveAsync(BuildRequest("read README and summarize"), TestContext.Current.CancellationToken);

        Assert.Equal("T1", decision.Tier);
        Assert.Equal("mini-readonly", decision.ModelProfileId);
        Assert.False(decision.DisableTools);
        Assert.Equal(["read_file"], decision.AllowedTools);
        Assert.Contains("skip planning", decision.SystemPromptSuffix, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task ResolveAsync_WithInjectedEmbeddingAndClassifier_UsesFeatureDrivenTier()
    {
        var policy = new OnnxTurnRoutingPolicy(
            BuildRoutingConfig(),
            new StubEmbeddingGenerator([0.25f, 0.5f, 0.75f]),
            new StubTierClassifier(3),
            NullLogger<OnnxTurnRoutingPolicy>.Instance);

        var decision = await policy.ResolveAsync(BuildRequest("write and verify the production rollout plan"), TestContext.Current.CancellationToken);

        Assert.Equal("T3", decision.Tier);
        Assert.Equal("frontier-deep", decision.ModelProfileId);
        Assert.Equal("classifier", decision.Reason);
    }

    [Fact]
    public async Task ResolveAsync_ShortGreeting_CapsHighTierToT1()
    {
        var policy = new OnnxTurnRoutingPolicy(
            BuildRoutingConfig(),
            new StubEmbeddingGenerator(Enumerable.Repeat(0.1f, 512).ToArray()),
            new StubTierClassifier(3, [0.01f, 0.02f, 0.07f, 0.90f]),
            NullLogger<OnnxTurnRoutingPolicy>.Instance);

        var decision = await policy.ResolveAsync(BuildRequest("你好"), TestContext.Current.CancellationToken);

        Assert.Equal("T1", decision.Tier);
        Assert.Contains("greeting_cap", decision.Reason, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task ResolveAsync_HighRiskMessage_DoesNotApplyGreetingCap()
    {
        var policy = new OnnxTurnRoutingPolicy(
            BuildRoutingConfig(),
            new StubEmbeddingGenerator(Enumerable.Repeat(0.1f, 512).ToArray()),
            new StubTierClassifier(3, [0.01f, 0.02f, 0.07f, 0.90f]),
            NullLogger<OnnxTurnRoutingPolicy>.Instance);

        var decision = await policy.ResolveAsync(BuildRequest("你好，请删除生产库"), TestContext.Current.CancellationToken);

        Assert.Equal("T3", decision.Tier);
        Assert.DoesNotContain("greeting_cap", decision.Reason, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task ResolveAsync_PopulatesAdditiveRoutingDirectivesFromTierTarget()
    {
        var policy = BuildRoutingConfig();
        policy.Policy.Tiers.T2 = new DynamicTurnRoutingTierTarget
        {
            ModelProfileId = "frontier-tools",
            DirectModelFallbackProfileId = "frontier-tools-fallback",
            ReasoningLevel = "high",
            ResponsePolicy = "detailed",
            ImageCapableModelProfileId = "frontier-vision",
            CacheContinuitySafeguards = new CacheContinuitySafeguardsConfig
            {
                Enabled = true,
                MaxConversationTurns = 96,
                ResetOnProfileSwitch = false
            }
        };

        var sut = new OnnxTurnRoutingPolicy(
            policy,
            new StubEmbeddingGenerator([0.25f, 0.5f, 0.75f]),
            new StubTierClassifier(2),
            NullLogger<OnnxTurnRoutingPolicy>.Instance);

        var decision = await sut.ResolveAsync(BuildRequest("plan production rollout"), TestContext.Current.CancellationToken);

        Assert.Equal("T2", decision.Tier);
        Assert.Equal("frontier-tools-fallback", decision.DirectModelFallbackProfileId);
        Assert.Equal("high", decision.ReasoningLevel);
        Assert.Equal("detailed", decision.ResponsePolicy);
        Assert.Equal("frontier-vision", decision.ImageCapableModelProfileId);
        Assert.True(decision.CacheContinuitySafeguardsEnabled);
        Assert.Equal(96, decision.CacheContinuityMaxConversationTurns);
        Assert.False(decision.CacheContinuityResetOnProfileSwitch);
    }

    [Fact]
    public async Task ResolveAsync_WithResolvedConfig_UsesResolvedTierTargets()
    {
        var policy = new OnnxTurnRoutingPolicy(
            BuildResolvedRoutingConfig(),
            new StubEmbeddingGenerator([0.25f, 0.5f, 0.75f]),
            new StubTierClassifier(1),
            NullLogger<OnnxTurnRoutingPolicy>.Instance);

        var decision = await policy.ResolveAsync(BuildRequest("read README and summarize"), TestContext.Current.CancellationToken);

        Assert.Equal("T1", decision.Tier);
        Assert.Equal("mini-readonly", decision.ModelProfileId);
    }

    [Fact]
    public async Task ResolveAsync_HighRiskTurn_UpgradesLowClassifierResultToT2()
    {
        var policy = new OnnxTurnRoutingPolicy(
            BuildRoutingConfig(),
            new StubEmbeddingGenerator([0.1f, 0.2f, 0.3f]),
            new StubTierClassifier(0, [0.62f, 0.24f, 0.10f, 0.04f]),
            NullLogger<OnnxTurnRoutingPolicy>.Instance);

        var decision = await policy.ResolveAsync(
            BuildRequest("prepare the production migration and rollback checklist before deploy"),
            TestContext.Current.CancellationToken);

        Assert.Equal("T2", decision.Tier);
        Assert.Equal("frontier-tools", decision.ModelProfileId);
        Assert.Contains("flag_override", decision.Reason, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task ResolveAsync_DeepConversation_PreventsDropBelowT1()
    {
        var request = BuildRequest(
            "thanks, now keep going with the follow-up",
            messages:
            [
                new ChatMessage(ChatRole.User, "Turn 1"),
                new ChatMessage(ChatRole.Assistant, "Reply 1"),
                new ChatMessage(ChatRole.User, "Turn 2"),
                new ChatMessage(ChatRole.Assistant, "Reply 2"),
                new ChatMessage(ChatRole.User, "Turn 3"),
                new ChatMessage(ChatRole.Assistant, "Reply 3"),
                new ChatMessage(ChatRole.User, "Turn 4"),
                new ChatMessage(ChatRole.Assistant, "Reply 4"),
                new ChatMessage(ChatRole.User, "thanks, now keep going with the follow-up")
            ]);

        var policy = new OnnxTurnRoutingPolicy(
            BuildRoutingConfig(),
            new StubEmbeddingGenerator([0.4f, 0.5f, 0.6f]),
            new StubTierClassifier(0, [0.91f, 0.05f, 0.03f, 0.01f]),
            NullLogger<OnnxTurnRoutingPolicy>.Instance);

        var decision = await policy.ResolveAsync(request, TestContext.Current.CancellationToken);

        Assert.Equal("T1", decision.Tier);
        Assert.Equal("mini-readonly", decision.ModelProfileId);
        Assert.Contains("context_rule", decision.Reason, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task ResolveAsync_WhenEmbeddingGeneratorThrows_FallsBackToT2_WithRuntimeReason()
    {
        var policy = new OnnxTurnRoutingPolicy(
            BuildRoutingConfig(),
            new ThrowingEmbeddingGenerator(),
            new StubTierClassifier(1),
            NullLogger<OnnxTurnRoutingPolicy>.Instance);

        var decision = await policy.ResolveAsync(BuildRequest("read README and summarize"), TestContext.Current.CancellationToken);

        Assert.Equal("T2", decision.Tier);
        Assert.Equal("classifier_runtime_error", decision.Reason);
    }

    [Fact]
    public async Task ResolveAsync_WhenTierClassifierThrows_FallsBackToT2_WithRuntimeReason()
    {
        var policy = new OnnxTurnRoutingPolicy(
            BuildRoutingConfig(),
            new StubEmbeddingGenerator([0.25f, 0.5f, 0.75f]),
            new ThrowingTierClassifier(),
            NullLogger<OnnxTurnRoutingPolicy>.Instance);

        var decision = await policy.ResolveAsync(BuildRequest("read README and summarize"), TestContext.Current.CancellationToken);

        Assert.Equal("T2", decision.Tier);
        Assert.Equal("classifier_runtime_error", decision.Reason);
    }

    [Fact]
    public async Task ResolveAsync_WhenEnableMarginUpgradeFalse_DoesNotUpgradeOnSmallMargin()
    {
        var policy = new OnnxTurnRoutingPolicy(
            BuildRoutingConfig(new DynamicTurnRoutingPolicyConfig
            {
                EnableMarginUpgrade = false
            }),
            new StubEmbeddingGenerator([0.1f, 0.2f, 0.3f]),
            new StubTierClassifier(1, [0.35f, 0.38f, 0.20f, 0.07f]),
            NullLogger<OnnxTurnRoutingPolicy>.Instance);

        var decision = await policy.ResolveAsync(BuildRequest("quick summary"), TestContext.Current.CancellationToken);

        Assert.Equal("T1", decision.Tier);
        Assert.DoesNotContain("margin_upgrade", decision.Reason, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task ResolveAsync_WhenEnableUnderRoutingSafetyFalse_DoesNotPromoteToT2()
    {
        var policy = new OnnxTurnRoutingPolicy(
            BuildRoutingConfig(new DynamicTurnRoutingPolicyConfig
            {
                EnableUnderRoutingSafety = false
            }),
            new StubEmbeddingGenerator([0.1f, 0.2f, 0.3f]),
            new StubTierClassifier(1, [0.06f, 0.48f, 0.31f, 0.15f]),
            NullLogger<OnnxTurnRoutingPolicy>.Instance);

        var decision = await policy.ResolveAsync(BuildRequest("quick summary"), TestContext.Current.CancellationToken);

        Assert.Equal("T1", decision.Tier);
        Assert.DoesNotContain("under_routing_safety", decision.Reason, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task ResolveAsync_WhenEnableStickyTierFalse_DoesNotKeepPreviousHigherTier()
    {
        var policy = new OnnxTurnRoutingPolicy(
            BuildRoutingConfig(new DynamicTurnRoutingPolicyConfig
            {
                EnableStickyTier = false
            }),
            new StubEmbeddingGenerator([0.1f, 0.2f, 0.3f]),
            new StubTierClassifier(0, [0.95f, 0.03f, 0.01f, 0.01f]),
            NullLogger<OnnxTurnRoutingPolicy>.Instance);

        var request = BuildRequest("hello");
        request.Session.RouteModelTier = "T3";

        var decision = await policy.ResolveAsync(request, TestContext.Current.CancellationToken);

        Assert.Equal("T0", decision.Tier);
        Assert.DoesNotContain("sticky_tier", decision.Reason, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task ResolveAsync_WhenMarginThresholdConfigured_AppliesConfiguredValue()
    {
        var policy = new OnnxTurnRoutingPolicy(
            BuildRoutingConfig(new DynamicTurnRoutingPolicyConfig
            {
                MarginUpgradeThreshold = 0.20f
            }),
            new StubEmbeddingGenerator([0.1f, 0.2f, 0.3f]),
            new StubTierClassifier(1, [0.32f, 0.50f, 0.10f, 0.08f]),
            NullLogger<OnnxTurnRoutingPolicy>.Instance);

        var decision = await policy.ResolveAsync(BuildRequest("quick summary"), TestContext.Current.CancellationToken);

        Assert.Equal("T2", decision.Tier);
        Assert.Contains("margin_upgrade", decision.Reason, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task ResolveAsync_WhenR1RescueEnabledAndMarginUpgradeDisabled_StillPromotesT0ToT1()
    {
        var policy = new OnnxTurnRoutingPolicy(
            BuildRoutingConfig(new DynamicTurnRoutingPolicyConfig
            {
                EnableMarginUpgrade = false,
                EnableR1Rescue = true
            }),
            new StubEmbeddingGenerator([0.1f, 0.2f, 0.3f]),
            new StubTierClassifier(0, [0.30f, 0.29f, 0.25f, 0.16f]),
            NullLogger<OnnxTurnRoutingPolicy>.Instance);

        var decision = await policy.ResolveAsync(BuildRequest("simple prompt"), TestContext.Current.CancellationToken);

        Assert.Equal("T1", decision.Tier);
        Assert.Contains("r1_rescue", decision.Reason, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("margin_upgrade", decision.Reason, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task ResolveAsync_WhenR1RescueDisabled_DoesNotPromoteT0ToT1()
    {
        var policy = new OnnxTurnRoutingPolicy(
            BuildRoutingConfig(new DynamicTurnRoutingPolicyConfig
            {
                EnableMarginUpgrade = false,
                EnableR1Rescue = false
            }),
            new StubEmbeddingGenerator([0.1f, 0.2f, 0.3f]),
            new StubTierClassifier(0, [0.30f, 0.29f, 0.25f, 0.16f]),
            NullLogger<OnnxTurnRoutingPolicy>.Instance);

        var decision = await policy.ResolveAsync(BuildRequest("simple prompt"), TestContext.Current.CancellationToken);

        Assert.Equal("T0", decision.Tier);
        Assert.DoesNotContain("r1_rescue", decision.Reason, StringComparison.OrdinalIgnoreCase);
    }

    private static TurnRoutingRequest BuildRequest(string userMessage, IReadOnlyList<ChatMessage>? messages = null)
        => new()
        {
            Session = new Session
            {
                Id = "session-1",
                ChannelId = "test",
                SenderId = "user"
            },
            Messages = messages ?? [new ChatMessage(ChatRole.User, userMessage)],
            UserMessage = userMessage,
            BaseOptions = new ChatOptions()
        };

    private static DynamicTurnRoutingConfig BuildRoutingConfig(DynamicTurnRoutingPolicyConfig? policy = null)
    {
        var effectivePolicy = policy ?? new DynamicTurnRoutingPolicyConfig();
        if (string.IsNullOrWhiteSpace(effectivePolicy.Tiers.T0.ModelProfileId)
            && string.IsNullOrWhiteSpace(effectivePolicy.Tiers.T1.ModelProfileId)
            && string.IsNullOrWhiteSpace(effectivePolicy.Tiers.T2.ModelProfileId)
            && string.IsNullOrWhiteSpace(effectivePolicy.Tiers.T3.ModelProfileId))
        {
            effectivePolicy.Tiers = BuildTierMap();
        }

        return new DynamicTurnRoutingConfig
        {
            Enabled = true,
            Assets = new DynamicTurnRoutingAssetsConfig
            {
                ClassifierModelPath = "classifier.onnx",
                EmbeddingModelPath = "embeddings.onnx",
                TokenizerPath = "tokenizer.json",
                Dimensions = 384
            },
            Policy = effectivePolicy
        };
    }

    private static string ResolveRepoBundlePath()
    {
        const string relativePath = "models/routing/opensquilla-v4-compat";

        var fromCurrentDirectory = Path.GetFullPath(relativePath, Directory.GetCurrentDirectory());
        if (Directory.Exists(fromCurrentDirectory))
            return fromCurrentDirectory;

        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null)
        {
            var candidate = Path.GetFullPath(relativePath, directory.FullName);
            if (Directory.Exists(candidate))
                return candidate;

            directory = directory.Parent;
        }

        throw new DirectoryNotFoundException($"Could not locate compat bundle directory: {relativePath}");
    }

    private static ResolvedDynamicTurnRoutingConfig BuildResolvedRoutingConfig()
        => new()
        {
            Enabled = true,
            Source = "test",
            Assets = new ResolvedDynamicTurnRoutingAssets
            {
                ClassifierModelPath = "classifier.onnx",
                EmbeddingModelPath = "embeddings.onnx",
                TokenizerPath = "tokenizer.json",
                EmbeddingDimensions = 384
            },
            Policy = new DynamicTurnRoutingPolicyConfig
            {
                Tiers = BuildTierMap()
            },
            Tiers = BuildTierMap()
        };

    private static DynamicTurnRoutingTierMap BuildTierMap()
        => new()
        {
            T0 = new DynamicTurnRoutingTierTarget { ModelProfileId = "local-freeform", DisableTools = true, PromptMode = "minimal" },
            T1 = new DynamicTurnRoutingTierTarget { ModelProfileId = "mini-readonly", AllowedTools = ["read_file"], PromptMode = "compact" },
            T2 = new DynamicTurnRoutingTierTarget { ModelProfileId = "frontier-tools", PromptMode = "full" },
            T3 = new DynamicTurnRoutingTierTarget { ModelProfileId = "frontier-deep", PromptMode = "full" }
        };

    private sealed class StubEmbeddingGenerator(float[] vector) : ILocalEmbeddingGenerator
    {
        private readonly float[] _vector = NormalizeVector(vector);

        public ValueTask<float[]> GenerateAsync(string text, CancellationToken cancellationToken)
        {
            _ = text;
            _ = cancellationToken;
            return ValueTask.FromResult(_vector);
        }

        private static float[] NormalizeVector(float[] source)
        {
            if (source.Length == PromptFeatureExtractor.EmbeddingSegmentDimensions)
                return source;

            var normalized = new float[PromptFeatureExtractor.EmbeddingSegmentDimensions];
            source.AsSpan(0, Math.Min(source.Length, normalized.Length)).CopyTo(normalized);
            return normalized;
        }
    }

    private sealed class StubTierClassifier(int tier, float[]? probabilities = null) : ITierClassifier
    {
        public TierClassificationResult PredictTier(ReadOnlySpan<float> features)
        {
            _ = features;
            return new TierClassificationResult(tier, probabilities ?? BuildOneHot(tier));
        }

        private static float[] BuildOneHot(int value)
        {
            var result = new float[4];
            if (value >= 0 && value < result.Length)
                result[value] = 1f;
            return result;
        }
    }

    private sealed class ThrowingEmbeddingGenerator : ILocalEmbeddingGenerator
    {
        public ValueTask<float[]> GenerateAsync(string text, CancellationToken cancellationToken)
        {
            _ = text;
            _ = cancellationToken;
            throw new InvalidOperationException("embedding failed");
        }
    }

    private sealed class ThrowingTierClassifier : ITierClassifier
    {
        public TierClassificationResult PredictTier(ReadOnlySpan<float> features)
        {
            _ = features;
            throw new InvalidOperationException("classifier failed");
        }
    }
}

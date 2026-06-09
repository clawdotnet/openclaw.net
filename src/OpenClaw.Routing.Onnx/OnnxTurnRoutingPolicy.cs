using System.Text.Json;
using Microsoft.Extensions.Logging;
using Microsoft.ML.OnnxRuntime;
using Microsoft.ML.OnnxRuntime.Tensors;
using Microsoft.Extensions.AI;
using OpenClaw.Agent.Routing;
using OpenClaw.Core.Models;

namespace OpenClaw.Routing.Onnx;

public readonly record struct TierClassificationResult(int Tier, float[] Probabilities);

public interface ITierClassifier
{
    TierClassificationResult PredictTier(ReadOnlySpan<float> features);
}

public sealed class OnnxTurnRoutingPolicy : ITurnRoutingPolicy, IDisposable
{
    private readonly ResolvedDynamicTurnRoutingConfig _config;
    private readonly ILogger<OnnxTurnRoutingPolicy> _logger;
    private readonly bool _classifierAvailable;
    private readonly int? _predictedTier;
    private readonly ILocalEmbeddingGenerator? _embeddingGenerator;
    private readonly ITierClassifier? _tierClassifier;
    private int _disposed;

    public OnnxTurnRoutingPolicy(DynamicTurnRoutingConfig config, ILogger<OnnxTurnRoutingPolicy> logger)
        : this(ToResolvedConfig(config), logger)
    {
    }

    public OnnxTurnRoutingPolicy(ResolvedDynamicTurnRoutingConfig config, ILogger<OnnxTurnRoutingPolicy> logger)
    {
        _config = config;
        _logger = logger;
        _classifierAvailable =
            File.Exists(config.Assets.ClassifierModelPath) &&
            File.Exists(config.Assets.EmbeddingModelPath) &&
            File.Exists(config.Assets.TokenizerPath);

        if (_classifierAvailable)
        {
            LocalOnnxEmbeddingGenerator? embeddingGenerator = null;
            OnnxTierClassifier? tierClassifier = null;

            try
            {
                if (config.Assets.EmbeddingDimensions != PromptFeatureExtractor.EmbeddingSegmentDimensions)
                {
                    logger.LogWarning(
                        "ONNX routing embedding dimension mismatch during initialization. Configuration declares {ConfiguredDimensions} dimensions but runtime extractor requires {ExpectedDimensions}. Dynamic routing will fall back to T2.",
                        config.Assets.EmbeddingDimensions,
                        PromptFeatureExtractor.EmbeddingSegmentDimensions);
                    _classifierAvailable = false;
                    return;
                }

                embeddingGenerator = new LocalOnnxEmbeddingGenerator(
                    config.Assets.EmbeddingModelPath,
                    config.Assets.TokenizerPath,
                    config.Assets.EmbeddingDimensions);
                tierClassifier = new OnnxTierClassifier(config.Assets.ClassifierModelPath);

                if (!IsClassifierFeatureCountCompatible(tierClassifier.ExpectedFeatureCount))
                {
                    logger.LogWarning(
                        "ONNX routing classifier input dimension mismatch during initialization. Model expects {ExpectedFeatureCount} features but runtime extractor produces {ActualFeatureCount}. Dynamic routing will fall back to T2.",
                        tierClassifier.ExpectedFeatureCount,
                        PromptFeatureExtractor.FeatureVectorDimensions);
                    _classifierAvailable = false;
                    embeddingGenerator.Dispose();
                    tierClassifier.Dispose();
                    return;
                }

                _embeddingGenerator = embeddingGenerator;
                _tierClassifier = tierClassifier;
            }
            catch (Exception ex) when (IsRecoverableOnnxException(ex))
            {
                logger.LogWarning(ex, "Failed to initialize ONNX routing components; dynamic routing will fall back to T2.");
                _classifierAvailable = false;
                embeddingGenerator?.Dispose();
                tierClassifier?.Dispose();
            }
        }
    }

    public OnnxTurnRoutingPolicy(DynamicTurnRoutingConfig config, int predictedTier, ILogger<OnnxTurnRoutingPolicy> logger)
        : this(ToResolvedConfig(config), predictedTier, logger)
    {
    }

    public OnnxTurnRoutingPolicy(ResolvedDynamicTurnRoutingConfig config, int predictedTier, ILogger<OnnxTurnRoutingPolicy> logger)
    {
        _config = config;
        _logger = logger;
        _classifierAvailable = true;
        _predictedTier = predictedTier;
    }

    public OnnxTurnRoutingPolicy(
        DynamicTurnRoutingConfig config,
        ILocalEmbeddingGenerator embeddingGenerator,
        ITierClassifier tierClassifier,
        ILogger<OnnxTurnRoutingPolicy> logger)
        : this(ToResolvedConfig(config), embeddingGenerator, tierClassifier, logger)
    {
    }

    public OnnxTurnRoutingPolicy(
        ResolvedDynamicTurnRoutingConfig config,
        ILocalEmbeddingGenerator embeddingGenerator,
        ITierClassifier tierClassifier,
        ILogger<OnnxTurnRoutingPolicy> logger)
    {
        _config = config;
        _logger = logger;
        _classifierAvailable = true;
        _embeddingGenerator = embeddingGenerator;
        _tierClassifier = tierClassifier;
    }

    public async ValueTask<TurnRoutingDecision> ResolveAsync(TurnRoutingRequest request, CancellationToken cancellationToken)
    {
        if (!_classifierAvailable)
            return BuildFallbackDecision("classifier_unavailable");

        if (_predictedTier.HasValue)
            return BuildDecision(Math.Clamp(_predictedTier.Value, 0, 3));

        if (_embeddingGenerator is null || _tierClassifier is null)
            return BuildFallbackDecision("classifier_unavailable");

        try
        {
            var featureInput = BuildFeatureInput(request);
            var currentEmbedding = await _embeddingGenerator.GenerateAsync(featureInput.CurrentUserText, cancellationToken);
            var historyEmbedding = await GenerateOptionalEmbeddingAsync(_embeddingGenerator, string.Join("\n", featureInput.PriorUserTurns), cancellationToken);
            var assistantEmbedding = await GenerateOptionalEmbeddingAsync(_embeddingGenerator, featureInput.PreviousAssistantText, cancellationToken);

            var features = PromptFeatureExtractor.BuildFeatureVector(featureInput, currentEmbedding, historyEmbedding, assistantEmbedding);
            var classification = _tierClassifier.PredictTier(features);
            var postProcess = PostProcessClassification(featureInput, classification);
            return BuildDecision(postProcess.Tier, postProcess.Reason);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex) when (IsRecoverableOnnxException(ex))
        {
            _logger.LogWarning(ex, "Dynamic turn routing failed during inference; falling back to T2.");
            return BuildFallbackDecision("classifier_runtime_error");
        }
    }

    private static bool IsRecoverableOnnxException(Exception ex)
        => ex is OnnxRuntimeException
            or IOException
            or JsonException
            or InvalidOperationException
            or NotSupportedException
            or ArgumentException;

    private TurnRoutingDecision BuildFallbackDecision(string reason)
    {
        _logger.LogDebug("Dynamic turn routing falling back to T2 with reason {Reason}.", reason);
        return BuildDecision(2, reason);
    }

    private TurnRoutingDecision BuildDecision(int tier, string? reason = null)
    {
        var (tierName, target) = tier switch
        {
            0 => ("T0", _config.Tiers.T0),
            1 => ("T1", _config.Tiers.T1),
            2 => ("T2", _config.Tiers.T2),
            3 => ("T3", _config.Tiers.T3),
            _ => ("T2", _config.Tiers.T2)
        };

        return new TurnRoutingDecision
        {
            Tier = tierName,
            ModelProfileId = target.ModelProfileId,
            DirectModelFallbackProfileId = string.IsNullOrWhiteSpace(target.DirectModelFallbackProfileId) ? null : target.DirectModelFallbackProfileId,
            DisableTools = target.DisableTools,
            AllowedTools = target.DisableTools ? [] : target.AllowedTools,
            PreferredTags = target.PreferredTags,
            ReasoningLevel = string.IsNullOrWhiteSpace(target.ReasoningLevel) ? null : target.ReasoningLevel,
            ResponsePolicy = string.IsNullOrWhiteSpace(target.ResponsePolicy) ? null : target.ResponsePolicy,
            ImageCapableModelProfileId = string.IsNullOrWhiteSpace(target.ImageCapableModelProfileId) ? null : target.ImageCapableModelProfileId,
            CacheContinuitySafeguardsEnabled = target.CacheContinuitySafeguards.Enabled,
            CacheContinuityMaxConversationTurns = target.CacheContinuitySafeguards.MaxConversationTurns,
            CacheContinuityResetOnProfileSwitch = target.CacheContinuitySafeguards.ResetOnProfileSwitch,
            SystemPromptSuffix = BuildPromptSuffix(target.PromptMode, target.DisableTools),
            Reason = reason ?? "classifier"
        };
    }

    private static string? BuildPromptSuffix(string? promptMode, bool disableTools)
    {
        if (disableTools)
            return "Respond directly and do not call tools.";

        return promptMode?.Trim().ToLowerInvariant() switch
        {
            "minimal" => "Respond directly with minimal reasoning.",
            "compact" => "Keep the reply short and skip planning.",
            _ => null
        };
    }

    private static async ValueTask<float[]> GenerateOptionalEmbeddingAsync(
        ILocalEmbeddingGenerator embeddingGenerator,
        string? text,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(text))
            return new float[PromptFeatureExtractor.EmbeddingSegmentDimensions];

        return await embeddingGenerator.GenerateAsync(text, cancellationToken);
    }

    private static RoutingFeatureInput BuildFeatureInput(TurnRoutingRequest request)
    {
        var priorUserTurns = new List<string>();
        string? previousAssistantText = null;

        foreach (var message in request.Messages)
        {
            var text = ExtractText(message);
            if (string.IsNullOrWhiteSpace(text))
                continue;

            if (message.Role == ChatRole.User)
            {
                if (!string.Equals(text, request.UserMessage, StringComparison.Ordinal))
                    priorUserTurns.Add(text);
            }
            else if (message.Role == ChatRole.Assistant)
            {
                previousAssistantText = text;
            }
        }

        return new RoutingFeatureInput(
            request.UserMessage,
            priorUserTurns.ToArray(),
            previousAssistantText,
            TurnIndex: Math.Max(priorUserTurns.Count, 0),
            request.Session.RouteModelTier,
            ToolCount: request.BaseOptions.Tools?.Count ?? 0,
            ContextTextLength: request.Messages.Sum(message => ExtractText(message)?.Length ?? 0));
    }

    private static string? ExtractText(ChatMessage message)
        => string.Concat(message.Contents.OfType<TextContent>().Select(static content => content.Text));

    private RoutingPostProcessResult PostProcessClassification(RoutingFeatureInput input, TierClassificationResult classification)
    {
        var policy = _config.Policy ?? new DynamicTurnRoutingPolicyConfig();
        var probabilities = NormalizeProbabilities(classification.Probabilities, classification.Tier);
        var tier = Math.Clamp(classification.Tier, 0, 3);
        var reasons = new List<string> { "classifier" };
        var margin = ComputeMargin(probabilities);
        var signals = PromptFeatureExtractor.ExtractSignals(input.CurrentUserText, input.TurnIndex);

        var marginTier = ApplyMarginUpgrade(tier, margin, policy.EnableMarginUpgrade, policy.MarginUpgradeThreshold);
        if (marginTier != tier)
        {
            tier = marginTier;
            reasons.Add("margin_upgrade");
        }

        var r1RescueTier = ApplyR1Rescue(tier, probabilities, policy.EnableR1Rescue, policy.R1RescueThreshold);
        if (r1RescueTier != tier)
        {
            tier = r1RescueTier;
            reasons.Add("r1_rescue");
        }

        var safetyTier = ApplyUnderRoutingSafety(tier, probabilities, policy.EnableUnderRoutingSafety, policy.UnderRoutingSafetyThreshold);
        if (safetyTier != tier)
        {
            tier = safetyTier;
            reasons.Add("under_routing_safety");
        }

        var flaggedTier = ApplyFlagOverrides(tier, signals);
        if (flaggedTier != tier)
        {
            tier = flaggedTier;
            reasons.Add("flag_override");
        }

        var contextTier = ApplyContextRule(tier, input.TurnIndex, policy.DeepConversationTurnIndexThreshold);
        if (contextTier != tier)
        {
            tier = contextTier;
            reasons.Add("context_rule");
        }

        var stickyTier = ApplyStickyTier(tier, input.PreviousTier, policy.EnableStickyTier);
        if (stickyTier != tier)
        {
            tier = stickyTier;
            reasons.Add("sticky_tier");
        }

        var greetingTier = ApplyGreetingCap(tier, input, signals);
        if (greetingTier != tier)
        {
            tier = greetingTier;
            reasons.Add("greeting_cap");
        }

        return new RoutingPostProcessResult(tier, string.Join('+', reasons));
    }

    private static float[] NormalizeProbabilities(float[] probabilities, int fallbackTier)
    {
        // Fail fast if the classifier model has an unexpected number of output classes;
        // this exception propagates to ResolveAsync which falls back to T2 with a log.
        if (probabilities.Length is not 0 and not 4)
            throw new InvalidOperationException(
                $"Classifier model returned {probabilities.Length} probability outputs but 4 (T0/T1/T2/T3) were expected. " +
                $"Ensure the model was trained with 4 tier classes.");

        if (probabilities.Length == 4)
        {
            var nonNegative = probabilities.All(static value => value >= 0f);
            var sum = probabilities.Sum();
            if (nonNegative && sum > 0.99f && sum < 1.01f)
                return [.. probabilities];  // return a defensive copy

            return Softmax(probabilities);
        }

        var oneHot = new float[4];
        var index = Math.Clamp(fallbackTier, 0, oneHot.Length - 1);
        oneHot[index] = 1f;
        return oneHot;
    }

    internal static bool IsClassifierFeatureCountCompatible(int? expectedFeatureCount)
        => !expectedFeatureCount.HasValue || expectedFeatureCount.Value == PromptFeatureExtractor.FeatureVectorDimensions;

    private static float ComputeMargin(IReadOnlyList<float> probabilities)
    {
        var ordered = probabilities.OrderByDescending(static value => value).Take(2).ToArray();
        return ordered.Length < 2 ? 1f : ordered[0] - ordered[1];
    }

    private static int ApplyMarginUpgrade(int tier, float margin, bool enabled, float threshold)
        => enabled && margin < threshold ? Math.Min(tier + 1, 3) : tier;

    private static int ApplyR1Rescue(int tier, IReadOnlyList<float> probabilities, bool enabled, float threshold)
    {
        if (!enabled || tier != 0 || probabilities.Count < 2)
            return tier;

        return probabilities[0] - probabilities[1] < threshold ? 1 : tier;
    }

    private static int ApplyUnderRoutingSafety(int tier, IReadOnlyList<float> probabilities, bool enabled, float threshold)
    {
        if (!enabled || tier >= 2 || probabilities.Count < 4)
            return tier;

        return probabilities[2] + probabilities[3] > threshold ? 2 : tier;
    }

    private static int ApplyFlagOverrides(int tier, RoutingSignals signals)
    {
        var result = tier;
        if (signals.HighRisk)
            result = Math.Max(result, 2);
        if (signals.Debug && signals.LongContext)
            result = Math.Max(result, 2);
        if ((signals.Research && signals.Planning) || (signals.RepoArch && signals.Planning))
            result = Math.Max(result, 3);
        if (signals.RepoArch)
            result = Math.Max(result, 1);
        if (signals.Planning)
            result = Math.Max(result, 1);
        return result;
    }

    private static int ApplyContextRule(int tier, int turnIndex, int turnIndexThreshold)
        => turnIndex >= turnIndexThreshold ? Math.Max(tier, 1) : tier;

    private static int ApplyStickyTier(int tier, string? previousTier, bool enabled)
    {
        if (!enabled)
            return tier;

        var previous = previousTier?.Trim().ToUpperInvariant() switch
        {
            "T0" => 0,
            "T1" => 1,
            "T2" => 2,
            "T3" => 3,
            _ => -1
        };

        return previous > tier ? previous : tier;
    }

    private static int ApplyGreetingCap(int tier, RoutingFeatureInput input, RoutingSignals signals)
    {
        if (tier <= 1)
            return tier;

        if (signals.HighRisk
            || signals.Debug
            || signals.RepoArch
            || signals.LongContext
            || signals.HasCodeBlock
            || signals.HasFileReference
            || signals.HasUrl
            || signals.DeepConversation)
            return tier;

        return IsShortGreeting(input.CurrentUserText) ? 1 : tier;
    }

    private static bool IsShortGreeting(string? text)
    {
        if (string.IsNullOrWhiteSpace(text))
            return false;

        var normalized = text.Trim().TrimEnd('!', '?', '.', ',', '，', '。', '！', '？').Trim();
        if (normalized.Length is < 1 or > 24)
            return false;

        return normalized.Equals("hi", StringComparison.OrdinalIgnoreCase)
            || normalized.Equals("hello", StringComparison.OrdinalIgnoreCase)
            || normalized.Equals("hey", StringComparison.OrdinalIgnoreCase)
            || normalized.Equals("yo", StringComparison.OrdinalIgnoreCase)
            || normalized.Equals("你好", StringComparison.Ordinal)
            || normalized.Equals("您好", StringComparison.Ordinal)
            || normalized.Equals("嗨", StringComparison.Ordinal)
            || normalized.Equals("在吗", StringComparison.Ordinal)
            || normalized.Equals("在嗎", StringComparison.Ordinal);
    }

    private static float[] Softmax(IReadOnlyList<float> values)
    {
        if (values.Count == 0)
            return [1f, 0f, 0f, 0f];

        var max = values.Max();
        var exps = new float[values.Count];
        var sum = 0f;
        for (var i = 0; i < values.Count; i++)
        {
            exps[i] = MathF.Exp(values[i] - max);
            sum += exps[i];
        }

        if (sum <= 0f)
            sum = 1f;

        for (var i = 0; i < exps.Length; i++)
            exps[i] /= sum;

        return exps.Length == 4
            ? exps
            : exps.Concat(Enumerable.Repeat(0f, Math.Max(0, 4 - exps.Length))).Take(4).ToArray();
    }

    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
            return;

        if (_embeddingGenerator is IDisposable disposableEmbeddingGenerator)
            disposableEmbeddingGenerator.Dispose();

        if (_tierClassifier is IDisposable disposableTierClassifier)
            disposableTierClassifier.Dispose();
    }

    private static ResolvedDynamicTurnRoutingConfig ToResolvedConfig(DynamicTurnRoutingConfig config)
    {
        var tierMap = config.Policy.Tiers;

        return new()
        {
            Enabled = config.Enabled,
            Source = "direct",
            Assets = new ResolvedDynamicTurnRoutingAssets
            {
                ClassifierModelPath = config.Assets.ClassifierModelPath,
                EmbeddingModelPath = config.Assets.EmbeddingModelPath,
                TokenizerPath = config.Assets.TokenizerPath,
                EmbeddingDimensions = config.Assets.Dimensions
            },
            Policy = new DynamicTurnRoutingPolicyConfig
            {
                Tiers = tierMap,
                EnableDiagnostics = config.Policy.EnableDiagnostics,
                EnableStickyTier = config.Policy.EnableStickyTier,
                EnableMarginUpgrade = config.Policy.EnableMarginUpgrade,
                EnableR1Rescue = config.Policy.EnableR1Rescue,
                EnableUnderRoutingSafety = config.Policy.EnableUnderRoutingSafety,
                MarginUpgradeThreshold = config.Policy.MarginUpgradeThreshold,
                R1RescueThreshold = config.Policy.R1RescueThreshold,
                UnderRoutingSafetyThreshold = config.Policy.UnderRoutingSafetyThreshold,
                DeepConversationTurnIndexThreshold = config.Policy.DeepConversationTurnIndexThreshold
            },
            Tiers = tierMap
        };
    }

    private sealed class OnnxTierClassifier : ITierClassifier, IDisposable
    {
        private readonly InferenceSession _session;
        private readonly string _inputName;
        private readonly int? _expectedFeatureCount;
        private int _disposed;

        public int? ExpectedFeatureCount => _expectedFeatureCount;

        public OnnxTierClassifier(string modelPath)
        {
            _session = new InferenceSession(modelPath);
            _inputName = FindInputName(_session, ["float_input", "features", "input"]);
            _expectedFeatureCount = FindExpectedFeatureCount(_session);
        }

        public TierClassificationResult PredictTier(ReadOnlySpan<float> features)
        {
            ValidateFeatureCount(features, _expectedFeatureCount);
            var tensor = new DenseTensor<float>(features.ToArray(), [1, features.Length]);
            using var results = _session.Run([NamedOnnxValue.CreateFromTensor(_inputName, tensor)]);

            float[]? probabilityVector = null;
            int? predictedTier = null;

            foreach (var result in results)
            {
                if (result.Value is Tensor<long> longTensor)
                {
                    predictedTier ??= (int)longTensor.ToArray()[0];
                    continue;
                }

                if (result.Value is Tensor<int> intTensor)
                {
                    predictedTier ??= intTensor.ToArray()[0];
                    continue;
                }

                if (result.Value is Tensor<float> floatTensor)
                {
                    var values = floatTensor.ToArray();
                    if (values.Length == 1)
                    {
                        predictedTier ??= (int)values[0];
                        continue;
                    }

                    probabilityVector ??= NormalizeProbabilities(values, ArgMax(values));
                    predictedTier ??= ArgMax(probabilityVector);
                }
            }

            predictedTier ??= 2;
            probabilityVector ??= NormalizeProbabilities([], predictedTier.Value);
            return new TierClassificationResult(predictedTier.Value, probabilityVector);
        }

        public void Dispose()
        {
            if (Interlocked.Exchange(ref _disposed, 1) != 0)
                return;

            _session.Dispose();
        }

        private static string FindInputName(InferenceSession session, string[] candidates)
        {
            foreach (var candidate in candidates)
            {
                var match = session.InputMetadata.Keys.FirstOrDefault(name => string.Equals(name, candidate, StringComparison.OrdinalIgnoreCase));
                if (match is not null)
                    return match;
            }

            return session.InputMetadata.Keys.First();
        }

        private static int? FindExpectedFeatureCount(InferenceSession session)
        {
            var input = session.InputMetadata.FirstOrDefault();
            if (input.Value.Dimensions is null || input.Value.Dimensions.Length < 2)
                return null;

            var count = input.Value.Dimensions[^1];
            return count > 0 ? count : null;
        }

        private static void ValidateFeatureCount(ReadOnlySpan<float> features, int? expectedFeatureCount)
        {
            if (!expectedFeatureCount.HasValue || expectedFeatureCount.Value == features.Length)
                return;

            throw new InvalidOperationException(
                $"Feature vector length mismatch: classifier model expects {expectedFeatureCount.Value} features " +
                $"but {features.Length} were provided. Ensure the feature extractor and classifier model are from the same training run.");
        }

        private static int ArgMax(float[] values)
        {
            if (values.Length == 0)
                return 2;

            var bestIndex = 0;
            var bestValue = values[0];
            for (var i = 1; i < values.Length; i++)
            {
                if (values[i] <= bestValue)
                    continue;

                bestValue = values[i];
                bestIndex = i;
            }

            return bestIndex;
        }
    }

    private readonly record struct RoutingPostProcessResult(int Tier, string Reason);
}

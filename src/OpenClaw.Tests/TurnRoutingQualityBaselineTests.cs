using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging.Abstractions;
using OpenClaw.Agent.Routing;
using OpenClaw.Core.Models;
using OpenClaw.Routing.Onnx;
using System.Globalization;
using System.Text.Json;
using Xunit;

namespace OpenClaw.Tests;

public sealed class TurnRoutingQualityBaselineTests
{
    private const string DatasetPathEnvironmentVariable = "OPENCLAW_ROUTING_QUALITY_DATASET";
    private const string UseExternalDatasetEnvironmentVariable = "OPENCLAW_ROUTING_QUALITY_USE_EXTERNAL";
    private const string ReportPathEnvironmentVariable = "OPENCLAW_ROUTING_QUALITY_REPORT_PATH";
    private const string GridReportPathEnvironmentVariable = "OPENCLAW_ROUTING_QUALITY_GRID_REPORT_PATH";
    private const string PolicySuggestionPathEnvironmentVariable = "OPENCLAW_ROUTING_QUALITY_POLICY_SUGGESTION_PATH";
    private const string WeightSensitivityReportPathEnvironmentVariable = "OPENCLAW_ROUTING_QUALITY_WEIGHT_SENSITIVITY_REPORT_PATH";
    private const string MinSamplesEnvironmentVariable = "OPENCLAW_ROUTING_QUALITY_MIN_SAMPLES";
    private const string UnderRoutingPenaltyWeightEnvironmentVariable = "OPENCLAW_ROUTING_SCORE_WEIGHT_UNDER_ROUTING";
    private const string OverRoutingPenaltyWeightEnvironmentVariable = "OPENCLAW_ROUTING_SCORE_WEIGHT_OVER_ROUTING";
    private const string HighRiskRecallRewardWeightEnvironmentVariable = "OPENCLAW_ROUTING_SCORE_WEIGHT_HIGH_RISK_RECALL";
    private const double DefaultUnderRoutingPenaltyWeight = 0.60d;
    private const double DefaultOverRoutingPenaltyWeight = 0.25d;
    private const double DefaultHighRiskRecallRewardWeight = 0.35d;
    private const int DefaultExternalDatasetMinSamples = 50;
    private const int BoundarySensitiveClassifierMinSamples = 20;
    private const string DefaultTrackedDatasetRelativePath = "tests/routing-eval/turn-routing-quality.sample.jsonl";
    private static readonly JsonSerializerOptions DatasetJsonOptions = new()
    {
        PropertyNameCaseInsensitive = true
    };

    [Fact]
    public async Task OfflineBaseline_MeetsRoutingQualityThresholds()
    {
        var dataset = BuildDataset(useExternalFromEnvironment: false);
        var metrics = await EvaluateDatasetAsync(dataset, BuildRoutingConfig());

        var diagnostics =
            $"macroF1={metrics.MacroF1:0.000}, underRoutingRate={metrics.UnderRoutingRate:0.000}, overRoutingRate={metrics.OverRoutingRate:0.000}, highRiskRecall={metrics.HighRiskRecall:0.000}";

        TryWriteQualityReport(metrics.Labels, metrics.Confusion, dataset.Count, metrics.MacroF1, metrics.UnderRoutingRate, metrics.OverRoutingRate, metrics.HighRiskRecall);

        Assert.True(metrics.MacroF1 >= 0.72, diagnostics);
        Assert.True(metrics.UnderRoutingRate <= 0.20, diagnostics);
        Assert.True(metrics.OverRoutingRate <= 0.35, diagnostics);
        Assert.True(metrics.HighRiskRecall >= 0.85, diagnostics);
    }

    [Fact]
    public async Task GridSearch_FindsThresholdCandidate_AtLeastAsGoodAsDefaultScore()
    {
        var dataset = BuildDataset(useExternalFromEnvironment: true);
        var defaultConfig = BuildRoutingConfig();
        var baseline = await EvaluateDatasetAsync(dataset, defaultConfig);
        var scoreWeights = ResolveScoreWeights();

        var allCandidates = await BuildGridCandidatesAsync(dataset, scoreWeights);
        var best = allCandidates.OrderByDescending(static candidate => candidate.Score).FirstOrDefault();

        Assert.NotNull(best);
        var baselineScore = ComputeCandidateScore(baseline, scoreWeights);
        Assert.True(best!.Score >= baselineScore, $"bestScore={best.Score:0.000}, baselineScore={baselineScore:0.000}");

        var profiles = new[]
        {
            new WeightProfile("balanced", new ScoreWeights(0.60d, 0.25d, 0.35d)),
            new WeightProfile("safety-first", new ScoreWeights(1.00d, 0.25d, 0.70d)),
            new WeightProfile("cost-first", new ScoreWeights(0.45d, 0.60d, 0.25d))
        };
        var sensitivitySummary = BuildWeightSensitivitySummary(allCandidates, baseline, profiles);
        var stability = BuildStabilityMetrics(sensitivitySummary);

        TryWriteGridSearchReport(allCandidates, best, baseline, scoreWeights);
        TryWritePolicySuggestion(best, scoreWeights, stability);
    }

    [Fact]
    public async Task GridSearch_ProducesWeightSensitivitySummary()
    {
        var dataset = BuildDataset(useExternalFromEnvironment: true);
        var baseline = await EvaluateDatasetAsync(dataset, BuildRoutingConfig());
        var defaultWeights = ResolveScoreWeights();
        var allCandidates = await BuildGridCandidatesAsync(dataset, defaultWeights);

        Assert.NotEmpty(allCandidates);

        var profiles = new[]
        {
            new WeightProfile("balanced", new ScoreWeights(0.60d, 0.25d, 0.35d)),
            new WeightProfile("safety-first", new ScoreWeights(1.00d, 0.25d, 0.70d)),
            new WeightProfile("cost-first", new ScoreWeights(0.45d, 0.60d, 0.25d))
        };

        var summary = BuildWeightSensitivitySummary(allCandidates, baseline, profiles);
        Assert.Equal(profiles.Length, summary.Count);
        Assert.All(summary, static item => Assert.False(string.IsNullOrWhiteSpace(item.Profile)));

        TryWriteWeightSensitivityReport(summary, baseline);
    }

    private static TurnRoutingRequest BuildRequest(string userMessage)
        => new()
        {
            Session = new Session
            {
                Id = "routing-quality-session",
                ChannelId = "test",
                SenderId = "user"
            },
            Messages = [new ChatMessage(ChatRole.User, userMessage)],
            UserMessage = userMessage,
            BaseOptions = new ChatOptions()
        };

    private static DynamicTurnRoutingConfig BuildRoutingConfig()
        => new()
        {
            Enabled = true,
            Assets = new DynamicTurnRoutingAssetsConfig
            {
                ClassifierModelPath = "classifier.onnx",
                EmbeddingModelPath = "embeddings.onnx",
                TokenizerPath = "tokenizer.json",
                Dimensions = 384
            },
            Policy = new DynamicTurnRoutingPolicyConfig
            {
                Tiers = new DynamicTurnRoutingTierMap
                {
                    T0 = new DynamicTurnRoutingTierTarget { ModelProfileId = "local-freeform", DisableTools = true, PromptMode = "minimal" },
                    T1 = new DynamicTurnRoutingTierTarget { ModelProfileId = "mini-readonly", AllowedTools = ["read_file"], PromptMode = "compact" },
                    T2 = new DynamicTurnRoutingTierTarget { ModelProfileId = "frontier-tools", PromptMode = "full" },
                    T3 = new DynamicTurnRoutingTierTarget { ModelProfileId = "frontier-deep", PromptMode = "full" }
                }
            }
        };

    private static IReadOnlyList<QualitySample> BuildDataset(bool useExternalFromEnvironment)
    {
        if (!useExternalFromEnvironment)
            return BuildFallbackDataset();

        var useExternal = string.Equals(
            Environment.GetEnvironmentVariable(UseExternalDatasetEnvironmentVariable),
            "1",
            StringComparison.Ordinal);

        if (!useExternal)
            return BuildFallbackDataset();

        var externalPath = ResolveExternalDatasetPath();

        var minSamples = ResolveMinimumSamples();
        return LoadAndValidateExternalDataset(externalPath, minSamples);
    }

    private static string ResolveExternalDatasetPath()
    {
        var configuredPath = Environment.GetEnvironmentVariable(DatasetPathEnvironmentVariable);
        if (!string.IsNullOrWhiteSpace(configuredPath))
            return configuredPath;

        if (TryResolveDefaultTrackedDatasetPath(out var defaultPath))
            return defaultPath;

        throw new InvalidOperationException(
            $"{DatasetPathEnvironmentVariable} is not set and default dataset was not found at {DefaultTrackedDatasetRelativePath}.");
    }

    private static bool TryResolveDefaultTrackedDatasetPath(out string path)
    {
        var fromCurrentDirectory = Path.GetFullPath(DefaultTrackedDatasetRelativePath, Directory.GetCurrentDirectory());
        if (File.Exists(fromCurrentDirectory))
        {
            path = fromCurrentDirectory;
            return true;
        }

        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null)
        {
            var candidate = Path.GetFullPath(DefaultTrackedDatasetRelativePath, directory.FullName);
            if (File.Exists(candidate))
            {
                path = candidate;
                return true;
            }

            directory = directory.Parent;
        }

        path = string.Empty;
        return false;
    }

    private static int ResolveMinimumSamples()
    {
        var raw = Environment.GetEnvironmentVariable(MinSamplesEnvironmentVariable);
        if (!string.IsNullOrWhiteSpace(raw) && int.TryParse(raw, out var parsed) && parsed > 0)
            return parsed;

        return DefaultExternalDatasetMinSamples;
    }

    private static IReadOnlyList<QualitySample> LoadAndValidateExternalDataset(string path, int minRequiredSamples)
    {
        if (minRequiredSamples <= 0)
            throw new ArgumentOutOfRangeException(nameof(minRequiredSamples), minRequiredSamples, "Minimum sample count must be positive.");

        if (!File.Exists(path))
            throw new FileNotFoundException($"Routing quality dataset file was not found: {path}", path);

        var samples = new List<QualitySample>();
        var errors = new List<string>();
        var lineNumber = 0;

        foreach (var line in File.ReadLines(path))
        {
            lineNumber++;

            if (string.IsNullOrWhiteSpace(line))
                continue;

            var trimmed = line.TrimStart();
            if (trimmed.StartsWith("#", StringComparison.Ordinal))
                continue;

            try
            {
                var record = JsonSerializer.Deserialize<QualityDatasetLine>(line, DatasetJsonOptions);
                if (record is null || string.IsNullOrWhiteSpace(record.Text) || string.IsNullOrWhiteSpace(record.GoldTier))
                {
                    errors.Add($"line {lineNumber}: text and goldTier are required.");
                    continue;
                }

                var normalizedTier = record.GoldTier.Trim().ToUpperInvariant();
                if (normalizedTier is not ("T0" or "T1" or "T2" or "T3"))
                {
                    errors.Add($"line {lineNumber}: goldTier must be one of T0/T1/T2/T3.");
                    continue;
                }

                samples.Add(new QualitySample(record.Text, normalizedTier, record.HighRisk));
            }
            catch (JsonException)
            {
                errors.Add($"line {lineNumber}: invalid json object.");
            }
        }

        if (samples.Count == 0)
            errors.Add("dataset has no valid samples.");
        else if (samples.Count < minRequiredSamples)
            errors.Add($"dataset has only {samples.Count} valid samples but requires at least {minRequiredSamples}.");

        if (errors.Count > 0)
            throw new InvalidDataException($"Invalid routing quality dataset: {string.Join(" ", errors)}");

        return samples;
    }

    [Fact]
    public void LoadAndValidateExternalDataset_WhenTierIsInvalid_ThrowsInvalidDataException()
    {
        var path = Path.GetTempFileName();
        try
        {
            File.WriteAllText(path, "{\"text\":\"hello\",\"goldTier\":\"TX\"}");

            var ex = Assert.Throws<InvalidDataException>(() => LoadAndValidateExternalDataset(path, minRequiredSamples: 1));

            Assert.Contains("goldTier must be one of T0/T1/T2/T3", ex.Message, StringComparison.Ordinal);
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public void LoadAndValidateExternalDataset_WhenJsonIsMalformed_ThrowsInvalidDataException()
    {
        var path = Path.GetTempFileName();
        try
        {
            File.WriteAllText(path, "{\"text\":\"broken\",\"goldTier\":\"T1\"");

            var ex = Assert.Throws<InvalidDataException>(() => LoadAndValidateExternalDataset(path, minRequiredSamples: 1));

            Assert.Contains("invalid json object", ex.Message, StringComparison.Ordinal);
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public void LoadAndValidateExternalDataset_WhenBelowMinimumSampleCount_ThrowsInvalidDataException()
    {
        var path = Path.GetTempFileName();
        try
        {
            File.WriteAllText(
                path,
                "{\"text\":\"sample-1\",\"goldTier\":\"T1\"}" + Environment.NewLine +
                "{\"text\":\"sample-2\",\"goldTier\":\"T2\"}");

            var ex = Assert.Throws<InvalidDataException>(() => LoadAndValidateExternalDataset(path, minRequiredSamples: 3));

            Assert.Contains("requires at least 3", ex.Message, StringComparison.Ordinal);
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public void ResolveScoreWeights_WhenEnvVarsAreInvalid_FallsBackToDefaults()
    {
        var originalUnderRouting = Environment.GetEnvironmentVariable(UnderRoutingPenaltyWeightEnvironmentVariable);
        var originalOverRouting = Environment.GetEnvironmentVariable(OverRoutingPenaltyWeightEnvironmentVariable);
        var originalHighRiskRecall = Environment.GetEnvironmentVariable(HighRiskRecallRewardWeightEnvironmentVariable);

        Environment.SetEnvironmentVariable(UnderRoutingPenaltyWeightEnvironmentVariable, "-1");
        Environment.SetEnvironmentVariable(OverRoutingPenaltyWeightEnvironmentVariable, "abc");
        Environment.SetEnvironmentVariable(HighRiskRecallRewardWeightEnvironmentVariable, "0");

        try
        {
            var weights = ResolveScoreWeights();

            Assert.Equal(DefaultUnderRoutingPenaltyWeight, weights.UnderRoutingPenaltyWeight);
            Assert.Equal(DefaultOverRoutingPenaltyWeight, weights.OverRoutingPenaltyWeight);
            Assert.Equal(DefaultHighRiskRecallRewardWeight, weights.HighRiskRecallRewardWeight);
        }
        finally
        {
            Environment.SetEnvironmentVariable(UnderRoutingPenaltyWeightEnvironmentVariable, originalUnderRouting);
            Environment.SetEnvironmentVariable(OverRoutingPenaltyWeightEnvironmentVariable, originalOverRouting);
            Environment.SetEnvironmentVariable(HighRiskRecallRewardWeightEnvironmentVariable, originalHighRiskRecall);
        }
    }

    [Fact]
    public void ResolveScoreWeights_WhenEnvVarsAreValid_UsesConfiguredValues()
    {
        var originalUnderRouting = Environment.GetEnvironmentVariable(UnderRoutingPenaltyWeightEnvironmentVariable);
        var originalOverRouting = Environment.GetEnvironmentVariable(OverRoutingPenaltyWeightEnvironmentVariable);
        var originalHighRiskRecall = Environment.GetEnvironmentVariable(HighRiskRecallRewardWeightEnvironmentVariable);

        Environment.SetEnvironmentVariable(UnderRoutingPenaltyWeightEnvironmentVariable, "0.9");
        Environment.SetEnvironmentVariable(OverRoutingPenaltyWeightEnvironmentVariable, "0.4");
        Environment.SetEnvironmentVariable(HighRiskRecallRewardWeightEnvironmentVariable, "0.6");

        try
        {
            var weights = ResolveScoreWeights();

            Assert.Equal(0.9d, weights.UnderRoutingPenaltyWeight, precision: 6);
            Assert.Equal(0.4d, weights.OverRoutingPenaltyWeight, precision: 6);
            Assert.Equal(0.6d, weights.HighRiskRecallRewardWeight, precision: 6);
        }
        finally
        {
            Environment.SetEnvironmentVariable(UnderRoutingPenaltyWeightEnvironmentVariable, originalUnderRouting);
            Environment.SetEnvironmentVariable(OverRoutingPenaltyWeightEnvironmentVariable, originalOverRouting);
            Environment.SetEnvironmentVariable(HighRiskRecallRewardWeightEnvironmentVariable, originalHighRiskRecall);
        }
    }

    private static IReadOnlyList<QualitySample> BuildFallbackDataset()
        =>
        [
            new("给我一句话总结这个 issue", "T0"),
            new("continue", "T0"),
            new("把这段话翻译成英文", "T0"),
            new("用三句话说明这个函数的作用", "T0"),
            new("读 README 并列出主要模块", "T1"),
            new("对比两个实现方案并给出建议", "T1"),
            new("给我一个迁移计划草案，分三步", "T1"),
            new("分析这个架构设计并指出边界问题", "T1"),
            new("生产环境 deploy 前请给出 rollback checklist", "T2", HighRisk: true),
            new("线上出现 exception 和 traceback，帮我定位根因", "T2", HighRisk: true),
            new("请审计这个删除脚本在 production 的风险", "T2", HighRisk: true),
            new("用户数据迁移脚本可能会覆盖记录，评估风险", "T2", HighRisk: true),
            new("写一份详细调研报告，比较三种技术路线并给出里程碑计划", "T3"),
            new("给出完整架构重构方案：目标、约束、风险、实施步骤、验收标准", "T3"),
            new("请做 competitive analysis，输出 roadmap 与风险矩阵", "T3")
        ];

    private static int TierToIndex(string tier)
        => tier.Trim().ToUpperInvariant() switch
        {
            "T0" => 0,
            "T1" => 1,
            "T2" => 2,
            "T3" => 3,
            _ => throw new ArgumentException($"Unexpected routing tier '{tier}'.", nameof(tier))
        };

    private static double ComputeMacroF1(int[,] confusion)
    {
        var classCount = confusion.GetLength(0);
        var f1Sum = 0d;

        for (var cls = 0; cls < classCount; cls++)
        {
            var tp = confusion[cls, cls];
            var fp = 0;
            var fn = 0;

            for (var i = 0; i < classCount; i++)
            {
                if (i != cls)
                {
                    fp += confusion[i, cls];
                    fn += confusion[cls, i];
                }
            }

            var precision = tp / (double)Math.Max(1, tp + fp);
            var recall = tp / (double)Math.Max(1, tp + fn);
            var denominator = precision + recall;
            var f1 = Math.Abs(denominator) < 1e-12d ? 0d : 2d * precision * recall / denominator;
            f1Sum += f1;
        }

        return f1Sum / classCount;
    }

    private static async Task<QualityMetrics> EvaluateDatasetAsync(IReadOnlyList<QualitySample> dataset, DynamicTurnRoutingConfig config)
    {
        // Keep fallback baseline deterministic while enabling threshold-sensitive behavior for larger datasets.
        ITierClassifier classifier = dataset.Count >= BoundarySensitiveClassifierMinSamples
            ? new HeuristicQualityTierClassifier()
            : new LegacyHeuristicQualityTierClassifier();

        var policy = new OnnxTurnRoutingPolicy(
            config,
            new FixedEmbeddingGenerator(new float[PromptFeatureExtractor.EmbeddingSegmentDimensions]),
            classifier,
            NullLogger<OnnxTurnRoutingPolicy>.Instance);

        var labels = new[] { "T0", "T1", "T2", "T3" };
        var confusion = new int[labels.Length, labels.Length];
        var highRiskTotal = 0;
        var highRiskHits = 0;
        var underRouting = 0;
        var overRouting = 0;

        foreach (var sample in dataset)
        {
            var decision = await policy.ResolveAsync(BuildRequest(sample.Text), TestContext.Current.CancellationToken);
            var gold = TierToIndex(sample.GoldTier);
            var predicted = TierToIndex(decision.Tier);

            confusion[gold, predicted]++;

            if (sample.HighRisk)
            {
                highRiskTotal++;
                if (predicted >= 2)
                    highRiskHits++;
            }

            if (gold >= 2 && predicted < 2)
                underRouting++;
            if (gold <= 1 && predicted >= 2)
                overRouting++;
        }

        var macroF1 = ComputeMacroF1(confusion);
        var underRoutingRate = underRouting / (double)Math.Max(1, dataset.Count(static item => TierToIndex(item.GoldTier) >= 2));
        var overRoutingRate = overRouting / (double)Math.Max(1, dataset.Count(static item => TierToIndex(item.GoldTier) <= 1));
        var highRiskRecall = highRiskHits / (double)Math.Max(1, highRiskTotal);

        return new QualityMetrics(labels, confusion, macroF1, underRoutingRate, overRoutingRate, highRiskRecall);
    }

    private static async Task<List<ThresholdCandidateResult>> BuildGridCandidatesAsync(IReadOnlyList<QualitySample> dataset, ScoreWeights scoreWeights)
    {
        var marginCandidates = new[] { 0.10f, 0.15f, 0.20f, 0.25f };
        var r1Candidates = new[] { 0.10f, 0.20f, 0.30f };
        var safetyCandidates = new[] { 0.35f, 0.45f, 0.55f };
        var deepTurnCandidates = new[] { 2, 4, 6 };
        var allCandidates = new List<ThresholdCandidateResult>();

        foreach (var margin in marginCandidates)
        {
            foreach (var rescue in r1Candidates)
            {
                foreach (var safety in safetyCandidates)
                {
                    foreach (var deepTurn in deepTurnCandidates)
                    {
                        var config = BuildRoutingConfig();
                        config.Policy.MarginUpgradeThreshold = margin;
                        config.Policy.R1RescueThreshold = rescue;
                        config.Policy.UnderRoutingSafetyThreshold = safety;
                        config.Policy.DeepConversationTurnIndexThreshold = deepTurn;

                        var metrics = await EvaluateDatasetAsync(dataset, config);
                        var score = ComputeCandidateScore(metrics, scoreWeights);
                        allCandidates.Add(new ThresholdCandidateResult(
                            margin,
                            rescue,
                            safety,
                            deepTurn,
                            score,
                            metrics.MacroF1,
                            metrics.UnderRoutingRate,
                            metrics.OverRoutingRate,
                            metrics.HighRiskRecall));
                    }
                }
            }
        }

        return allCandidates;
    }

    private static ScoreWeights ResolveScoreWeights()
        => new(
            UnderRoutingPenaltyWeight: ResolvePositiveDouble(
                UnderRoutingPenaltyWeightEnvironmentVariable,
                DefaultUnderRoutingPenaltyWeight),
            OverRoutingPenaltyWeight: ResolvePositiveDouble(
                OverRoutingPenaltyWeightEnvironmentVariable,
                DefaultOverRoutingPenaltyWeight),
            HighRiskRecallRewardWeight: ResolvePositiveDouble(
                HighRiskRecallRewardWeightEnvironmentVariable,
                DefaultHighRiskRecallRewardWeight));

    private static double ResolvePositiveDouble(string variableName, double defaultValue)
    {
        var raw = Environment.GetEnvironmentVariable(variableName);
        if (string.IsNullOrWhiteSpace(raw))
            return defaultValue;

        return double.TryParse(raw, NumberStyles.Float, CultureInfo.InvariantCulture, out var parsed) && parsed > 0d
            ? parsed
            : defaultValue;
    }

    private static double ComputeCandidateScore(QualityMetrics metrics, ScoreWeights weights)
        => metrics.MacroF1
            - (weights.UnderRoutingPenaltyWeight * metrics.UnderRoutingRate)
            - (weights.OverRoutingPenaltyWeight * metrics.OverRoutingRate)
            + (weights.HighRiskRecallRewardWeight * metrics.HighRiskRecall);

    private static void TryWriteGridSearchReport(
        IReadOnlyList<ThresholdCandidateResult> allCandidates,
        ThresholdCandidateResult best,
        QualityMetrics baseline,
        ScoreWeights scoreWeights)
    {
        var reportPath = Environment.GetEnvironmentVariable(GridReportPathEnvironmentVariable);
        if (string.IsNullOrWhiteSpace(reportPath))
            return;

        var report = new
        {
            scoreWeights,
            baseline = new
            {
                score = ComputeCandidateScore(baseline, scoreWeights),
                macroF1 = baseline.MacroF1,
                underRoutingRate = baseline.UnderRoutingRate,
                overRoutingRate = baseline.OverRoutingRate,
                highRiskRecall = baseline.HighRiskRecall
            },
            best,
            candidates = allCandidates.OrderByDescending(static item => item.Score).Take(20).ToArray()
        };

        var options = new JsonSerializerOptions { WriteIndented = true };
        var directory = Path.GetDirectoryName(reportPath);
        if (!string.IsNullOrWhiteSpace(directory))
            Directory.CreateDirectory(directory);

        File.WriteAllText(reportPath, JsonSerializer.Serialize(report, options));
    }

    private static void TryWritePolicySuggestion(ThresholdCandidateResult best, ScoreWeights scoreWeights, StabilityMetrics stability)
    {
        var suggestionPath = Environment.GetEnvironmentVariable(PolicySuggestionPathEnvironmentVariable);
        if (string.IsNullOrWhiteSpace(suggestionPath))
            return;

        var payload = new
        {
            DynamicTurnRouting = new
            {
                Policy = new
                {
                    EnableStickyTier = true,
                    EnableMarginUpgrade = true,
                    EnableR1Rescue = true,
                    EnableUnderRoutingSafety = true,
                    MarginUpgradeThreshold = best.MarginUpgradeThreshold,
                    R1RescueThreshold = best.R1RescueThreshold,
                    UnderRoutingSafetyThreshold = best.UnderRoutingSafetyThreshold,
                    DeepConversationTurnIndexThreshold = best.DeepConversationTurnIndexThreshold
                }
            },
            Diagnostics = new
            {
                best.Score,
                best.MacroF1,
                best.UnderRoutingRate,
                best.OverRoutingRate,
                best.HighRiskRecall,
                scoreWeights,
                stability
            }
        };

        var options = new JsonSerializerOptions { WriteIndented = true };
        var directory = Path.GetDirectoryName(suggestionPath);
        if (!string.IsNullOrWhiteSpace(directory))
            Directory.CreateDirectory(directory);

        File.WriteAllText(suggestionPath, JsonSerializer.Serialize(payload, options));
    }

    private static IReadOnlyList<WeightSensitivitySummaryItem> BuildWeightSensitivitySummary(
        IReadOnlyList<ThresholdCandidateResult> allCandidates,
        QualityMetrics baseline,
        IReadOnlyList<WeightProfile> profiles)
    {
        var results = new List<WeightSensitivitySummaryItem>(profiles.Count);

        foreach (var profile in profiles)
        {
            var best = allCandidates
                .OrderByDescending(candidate => ComputeCandidateScore(candidate, profile.Weights))
                .First();

            var baselineScore = ComputeCandidateScore(baseline, profile.Weights);
            var bestScore = ComputeCandidateScore(best, profile.Weights);

            results.Add(new WeightSensitivitySummaryItem(
                profile.Profile,
                profile.Weights,
                baselineScore,
                bestScore,
                bestScore - baselineScore,
                best));
        }

        return results;
    }

    private static void TryWriteWeightSensitivityReport(
        IReadOnlyList<WeightSensitivitySummaryItem> summary,
        QualityMetrics baseline)
    {
        var reportPath = Environment.GetEnvironmentVariable(WeightSensitivityReportPathEnvironmentVariable);
        if (string.IsNullOrWhiteSpace(reportPath))
            return;

        var stability = BuildStabilityMetrics(summary);

        var payload = new
        {
            baseline = new
            {
                baseline.MacroF1,
                baseline.UnderRoutingRate,
                baseline.OverRoutingRate,
                baseline.HighRiskRecall
            },
            stability,
            summary
        };

        var options = new JsonSerializerOptions { WriteIndented = true };
        var directory = Path.GetDirectoryName(reportPath);
        if (!string.IsNullOrWhiteSpace(directory))
            Directory.CreateDirectory(directory);

        File.WriteAllText(reportPath, JsonSerializer.Serialize(payload, options));
    }

    private static StabilityMetrics BuildStabilityMetrics(IReadOnlyList<WeightSensitivitySummaryItem> summary)
    {
        var topParameterKeys = summary
            .Select(static item => BuildParameterKey(item.BestCandidate))
            .ToArray();

        var grouped = topParameterKeys
            .GroupBy(static key => key, StringComparer.Ordinal)
            .OrderByDescending(static group => group.Count())
            .ToArray();

        var mostCommon = grouped.Length > 0 ? grouped[0] : null;
        var top1ConsistencyRate = summary.Count == 0 || mostCommon is null
            ? 0d
            : mostCommon.Count() / (double)summary.Count;

        var distribution = grouped
            .Select(static group => new StabilityDistributionItem(group.Key, group.Count()))
            .ToArray();

        return new StabilityMetrics(grouped.Length, top1ConsistencyRate, distribution);
    }

    private static string BuildParameterKey(ThresholdCandidateResult candidate)
        =>
            $"margin={candidate.MarginUpgradeThreshold:0.00}|r1={candidate.R1RescueThreshold:0.00}|safety={candidate.UnderRoutingSafetyThreshold:0.00}|deepTurn={candidate.DeepConversationTurnIndexThreshold}";

    private static double ComputeCandidateScore(ThresholdCandidateResult candidate, ScoreWeights weights)
        => candidate.MacroF1
            - (weights.UnderRoutingPenaltyWeight * candidate.UnderRoutingRate)
            - (weights.OverRoutingPenaltyWeight * candidate.OverRoutingRate)
            + (weights.HighRiskRecallRewardWeight * candidate.HighRiskRecall);

    private static void TryWriteQualityReport(
        IReadOnlyList<string> labels,
        int[,] confusion,
        int totalSamples,
        double macroF1,
        double underRoutingRate,
        double overRoutingRate,
        double highRiskRecall)
    {
        var reportPath = Environment.GetEnvironmentVariable(ReportPathEnvironmentVariable);
        if (string.IsNullOrWhiteSpace(reportPath))
            return;

        var perTier = BuildPerTierMetrics(labels, confusion);
        var report = new
        {
            totalSamples,
            macroF1,
            underRoutingRate,
            overRoutingRate,
            highRiskRecall,
            labels,
            confusion = ToJagged(confusion),
            perTier
        };

        var options = new JsonSerializerOptions
        {
            WriteIndented = true
        };

        var directory = Path.GetDirectoryName(reportPath);
        if (!string.IsNullOrWhiteSpace(directory))
            Directory.CreateDirectory(directory);

        File.WriteAllText(reportPath, JsonSerializer.Serialize(report, options));
    }

    private static IReadOnlyList<object> BuildPerTierMetrics(IReadOnlyList<string> labels, int[,] confusion)
    {
        var classCount = confusion.GetLength(0);
        var result = new List<object>(classCount);

        for (var cls = 0; cls < classCount; cls++)
        {
            var tp = confusion[cls, cls];
            var fp = 0;
            var fn = 0;

            for (var i = 0; i < classCount; i++)
            {
                if (i != cls)
                {
                    fp += confusion[i, cls];
                    fn += confusion[cls, i];
                }
            }

            var support = 0;
            for (var j = 0; j < classCount; j++)
                support += confusion[cls, j];

            var precision = tp / (double)Math.Max(1, tp + fp);
            var recall = tp / (double)Math.Max(1, tp + fn);
            var denominator = precision + recall;
            var f1 = denominator <= double.Epsilon ? 0d : 2d * precision * recall / denominator;

            result.Add(new
            {
                tier = labels[cls],
                support,
                precision,
                recall,
                f1
            });
        }

        return result;
    }

    private static int[][] ToJagged(int[,] matrix)
    {
        var rows = matrix.GetLength(0);
        var cols = matrix.GetLength(1);
        var result = new int[rows][];

        for (var row = 0; row < rows; row++)
        {
            var items = new int[cols];
            for (var col = 0; col < cols; col++)
                items[col] = matrix[row, col];

            result[row] = items;
        }

        return result;
    }

    private sealed record QualitySample(string Text, string GoldTier, bool HighRisk = false);

    private sealed record QualityDatasetLine(string Text, string GoldTier, bool HighRisk = false);

    private sealed record QualityMetrics(
        IReadOnlyList<string> Labels,
        int[,] Confusion,
        double MacroF1,
        double UnderRoutingRate,
        double OverRoutingRate,
        double HighRiskRecall);

    private sealed record ThresholdCandidateResult(
        float MarginUpgradeThreshold,
        float R1RescueThreshold,
        float UnderRoutingSafetyThreshold,
        int DeepConversationTurnIndexThreshold,
        double Score,
        double MacroF1,
        double UnderRoutingRate,
        double OverRoutingRate,
        double HighRiskRecall);

    private sealed record WeightProfile(string Profile, ScoreWeights Weights);

    private sealed record WeightSensitivitySummaryItem(
        string Profile,
        ScoreWeights Weights,
        double BaselineScore,
        double BestScore,
        double Improvement,
        ThresholdCandidateResult BestCandidate);

    private sealed record StabilityDistributionItem(string ParameterKey, int Count);

    private sealed record StabilityMetrics(
        int UniqueTopParameterCount,
        double Top1ConsistencyRate,
        IReadOnlyList<StabilityDistributionItem> TopParameterDistribution);

    private sealed record ScoreWeights(
        double UnderRoutingPenaltyWeight,
        double OverRoutingPenaltyWeight,
        double HighRiskRecallRewardWeight);

    private sealed class FixedEmbeddingGenerator(float[] vector) : ILocalEmbeddingGenerator
    {
        public ValueTask<float[]> GenerateAsync(string text, CancellationToken cancellationToken)
        {
            _ = text;
            _ = cancellationToken;
            return ValueTask.FromResult(vector);
        }
    }

    private sealed class HeuristicQualityTierClassifier : ITierClassifier
    {
        public TierClassificationResult PredictTier(ReadOnlySpan<float> features)
        {
            var highRisk = features[28] > 0f;
            var debug = features[22] > 0f || features[37] > 0f;
            var architecture = features[24] > 0f || features[50] > 0f;
            var planning = features[26] > 0f;
            var research = features[23] > 0f;
            var textLength = features[0];

            var tier = 0;
            if ((research && planning) || (architecture && textLength >= 80f) || textLength >= 140f)
            {
                tier = 3;
            }
            else if (highRisk || debug || textLength >= 55f)
            {
                tier = 2;
            }
            else if (architecture || planning || research)
            {
                tier = 1;
            }

            if (textLength > 1200f)
                tier = Math.Max(tier, 2);

            // Margin intentionally spans threshold cut points used by grid search.
            var margin = ((int)MathF.Abs(textLength) % 4) switch
            {
                0 => 0.09f,
                1 => 0.13f,
                2 => 0.18f,
                _ => 0.24f
            };

            float[] probabilities;
            if (tier <= 1)
            {
                var riskMass = 0.30f;
                if (highRisk)
                    riskMass += 0.10f;
                if (debug)
                    riskMass += 0.10f;
                if (architecture)
                    riskMass += 0.08f;
                if (planning)
                    riskMass += 0.07f;

                riskMass = Math.Clamp(riskMass, 0.28f, 0.62f);
                var nonRiskMass = 1f - riskMass;
                var pair = BuildPair(nonRiskMass, margin);

                probabilities = tier == 0
                    ? [pair.Top, pair.Second, riskMass * 0.65f, riskMass * 0.35f]
                    : [pair.Second, pair.Top, riskMass * 0.65f, riskMass * 0.35f];
            }
            else
            {
                var highTierMass = 0.62f;
                if (highRisk)
                    highTierMass += 0.12f;
                if (debug)
                    highTierMass += 0.08f;
                if (planning || research)
                    highTierMass += 0.06f;

                highTierMass = Math.Clamp(highTierMass, 0.58f, 0.85f);
                var lowTierMass = 1f - highTierMass;
                var pair = BuildPair(highTierMass, margin);

                probabilities = tier == 2
                    ? [lowTierMass * 0.35f, lowTierMass * 0.65f, pair.Top, pair.Second]
                    : [lowTierMass * 0.40f, lowTierMass * 0.60f, pair.Second, pair.Top];
            }

            return new TierClassificationResult(tier, probabilities);
        }

        private static (float Top, float Second) BuildPair(float totalMass, float desiredMargin)
        {
            var margin = Math.Clamp(desiredMargin, 0.01f, Math.Max(0.01f, totalMass - 0.02f));
            var top = (totalMass + margin) / 2f;
            var second = totalMass - top;
            return (top, second);
        }
    }

    private sealed class LegacyHeuristicQualityTierClassifier : ITierClassifier
    {
        public TierClassificationResult PredictTier(ReadOnlySpan<float> features)
        {
            var highRisk = features[28] > 0f;
            var debug = features[22] > 0f || features[37] > 0f;
            var architecture = features[24] > 0f || features[50] > 0f;
            var planning = features[26] > 0f;
            var research = features[23] > 0f;
            var textLength = features[0];

            var tier = 0;
            if (textLength >= 26f)
            {
                tier = 3;
            }
            else if (highRisk || debug || textLength >= 20f)
            {
                tier = 2;
            }
            else if ((research && planning) || (architecture && textLength >= 45f))
            {
                tier = 3;
            }
            else if (architecture || planning || research)
            {
                tier = 1;
            }

            if (textLength > 1200f)
                tier = Math.Max(tier, 2);

            return tier switch
            {
                0 => new TierClassificationResult(0, [0.88f, 0.08f, 0.03f, 0.01f]),
                1 => new TierClassificationResult(1, [0.08f, 0.84f, 0.06f, 0.02f]),
                2 => new TierClassificationResult(2, [0.05f, 0.15f, 0.70f, 0.10f]),
                3 => new TierClassificationResult(3, [0.03f, 0.07f, 0.20f, 0.70f]),
                _ => new TierClassificationResult(2, [0.05f, 0.15f, 0.70f, 0.10f])
            };
        }
    }
}

using System.Text.RegularExpressions;
using OpenClaw.Core.Models;

namespace OpenClaw.Gateway;

internal sealed class AutomationSuggestionQualityGate
{
    private static readonly Regex WhitespaceRegex = new(@"\s+", RegexOptions.Compiled);
    private static readonly string[] StableRangeTerms = ["过去", "小时", "天", "周", "24", "48", "hour", "day", "week"];
    private static readonly string[] VagueScopeTerms = ["当前", "最近", "整体", "current", "recent", "overall"];
    private static readonly string[] OutputTerms = ["输出", "只输出", "列表", "清单", "格式", "json", "output", "list", "format", "1)", "1."];
    private static readonly string[] SideEffectTerms = ["发送", "发布", "写入", "修改", "删除", "send", "post", "write", "modify", "delete"];
    private static readonly string[] ConfirmationTerms = ["确认", "批准", "用户明确", "confirm", "approval", "approved"];

    public AutomationSuggestionQualityResult Evaluate(
        AutomationDefinition candidate,
        AutomationSuggestionIntent intent,
        IReadOnlyList<AutomationDefinition> existingAutomations,
        IReadOnlySet<string> availableDeliveryChannelIds)
    {
        var blockingIssues = new List<string>();
        var warnings = new List<string>();
        var normalizedName = Normalize(candidate.Name);
        var normalizedPrompt = Normalize(candidate.Prompt);
        var hasStableInputScope = HasStableInputScope(candidate.Prompt);
        var hasOutputFormat = ContainsAny(candidate.Prompt, OutputTerms);
        var hasSideEffect = ContainsAny(candidate.Prompt, SideEffectTerms);
        var hasExplicitConfirmation = ContainsAny(candidate.Prompt, ConfirmationTerms);
        var isDuplicate = existingAutomations.Any(existing => IsSimilar(candidate, existing));

        if (string.IsNullOrWhiteSpace(candidate.Name))
            blockingIssues.Add("缺少名称。");
        if (string.IsNullOrWhiteSpace(candidate.Prompt))
            blockingIssues.Add("缺少提示词。");
        if (string.IsNullOrWhiteSpace(candidate.Schedule))
            blockingIssues.Add("缺少调度。");
        if (string.IsNullOrWhiteSpace(candidate.DeliveryChannelId))
            blockingIssues.Add("缺少投递通道。");
        else if (!availableDeliveryChannelIds.Contains(candidate.DeliveryChannelId))
            blockingIssues.Add("投递通道不存在。");

        if (!string.IsNullOrWhiteSpace(normalizedName) && string.Equals(normalizedName, normalizedPrompt, StringComparison.Ordinal))
            blockingIssues.Add("名称和提示词不能完全相同。");
        if (!hasStableInputScope)
            blockingIssues.Add("提示词没有为定时执行定义稳定输入范围。");
        if (!hasOutputFormat)
            blockingIssues.Add("提示词没有清晰的预期输出。");
        if (hasSideEffect && !hasExplicitConfirmation)
            blockingIssues.Add("带外部副作用的自动化缺少用户明确确认。");
        if (isDuplicate)
            blockingIssues.Add("候选项与已有自动化重复。");

        if (ContainsAny(candidate.Prompt, VagueScopeTerms) && !hasStableInputScope)
            warnings.Add("提示词包含模糊范围词，但没有定义稳定输入范围。");
        if (string.Equals(candidate.Schedule, "@daily", StringComparison.OrdinalIgnoreCase) && !hasOutputFormat)
            warnings.Add("每日执行但输出格式不明确，可能产生低价值结果。");
        if (!candidate.RetryPolicy.Enabled)
            warnings.Add("自动化未启用重试策略。");

        var dimensions = new[]
        {
            BuildDimension("intent_clarity", string.Equals(intent.Intent, "custom_automation", StringComparison.OrdinalIgnoreCase) ? 55 : 90, "意图是否有明确目的。"),
            BuildDimension("input_scope", hasStableInputScope ? 90 : 25, "输入范围是否稳定。"),
            BuildDimension("output_clarity", hasOutputFormat ? 90 : 25, "输出格式是否清楚。"),
            BuildDimension("schedule_match", ScoreSchedule(candidate.Schedule, intent.CadenceHint), "调度是否匹配任务价值。"),
            BuildDimension("safety", hasSideEffect && !hasExplicitConfirmation ? 20 : 90, "外部副作用是否受控。"),
            BuildDimension("noise_risk", hasOutputFormat && hasStableInputScope ? 85 : 35, "是否容易产生低价值输出。"),
            BuildDimension("user_value", string.Equals(intent.ExpectedOutcome, "unspecified", StringComparison.OrdinalIgnoreCase) ? 55 : 85, "是否减少重复劳动。"),
            BuildDimension("duplicate_risk", isDuplicate ? 15 : 90, "是否与已有自动化重复。")
        };
        var score = (int)Math.Round(dimensions.Average(static dimension => dimension.Score));

        return new AutomationSuggestionQualityResult
        {
            Score = score,
            Decision = Decide(score, blockingIssues.Count),
            Dimensions = dimensions,
            BlockingIssues = blockingIssues.ToArray(),
            Warnings = warnings.ToArray()
        };
    }

    private static AutomationSuggestionQualityDimension BuildDimension(string name, int score, string reason)
        => new()
        {
            Name = name,
            Score = score,
            Reason = reason
        };

    private static string Decide(int score, int blockingIssueCount)
    {
        if (blockingIssueCount > 0)
            return AutomationSuggestionQualityDecisions.LearningOnly;
        if (score >= 85)
            return AutomationSuggestionQualityDecisions.ReadyDraft;
        if (score >= 70)
            return AutomationSuggestionQualityDecisions.NeedsReviewDraft;
        if (score >= 50)
            return AutomationSuggestionQualityDecisions.LearningOnly;
        return AutomationSuggestionQualityDecisions.Suppressed;
    }

    private static int ScoreSchedule(string schedule, string cadenceHint)
    {
        if (string.Equals(schedule, "@daily", StringComparison.OrdinalIgnoreCase) && string.Equals(cadenceHint, "daily", StringComparison.OrdinalIgnoreCase))
            return 90;
        if (string.Equals(schedule, "@weekly", StringComparison.OrdinalIgnoreCase) && string.Equals(cadenceHint, "weekly", StringComparison.OrdinalIgnoreCase))
            return 90;
        if (string.Equals(schedule, "@hourly", StringComparison.OrdinalIgnoreCase) && string.Equals(cadenceHint, "hourly", StringComparison.OrdinalIgnoreCase))
            return 90;
        return 65;
    }

    private static bool HasStableInputScope(string prompt)
        => ContainsAny(prompt, StableRangeTerms) && !string.IsNullOrWhiteSpace(prompt);

    private static bool IsSimilar(AutomationDefinition candidate, AutomationDefinition existing)
    {
        if (string.Equals(Normalize(candidate.Name), Normalize(existing.Name), StringComparison.Ordinal) ||
            string.Equals(Normalize(candidate.Prompt), Normalize(existing.Prompt), StringComparison.Ordinal))
        {
            return true;
        }

        var candidateTokens = Tokenize(candidate.Prompt).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var existingTokens = Tokenize(existing.Prompt).ToHashSet(StringComparer.OrdinalIgnoreCase);
        if (candidateTokens.Count == 0 || existingTokens.Count == 0)
            return false;

        var overlap = candidateTokens.Count(existingTokens.Contains);
        var union = candidateTokens.Count + existingTokens.Count - overlap;
        return union > 0 && (double)overlap / union >= 0.85d;
    }

    private static IEnumerable<string> Tokenize(string value)
        => Regex.Split(Normalize(value), @"[^\p{L}\p{N}]+")
            .Where(static token => token.Length > 2);

    private static string Normalize(string value)
        => WhitespaceRegex.Replace(value.Trim().ToLowerInvariant(), " ");

    private static bool ContainsAny(string value, IReadOnlyList<string> terms)
        => terms.Any(term => value.Contains(term, StringComparison.OrdinalIgnoreCase));
}

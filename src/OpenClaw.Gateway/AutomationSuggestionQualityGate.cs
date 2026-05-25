using System.Text.RegularExpressions;
using OpenClaw.Core.Models;

namespace OpenClaw.Gateway;

internal sealed class AutomationSuggestionQualityGate
{
    private static readonly Regex WhitespaceRegex = new(@"\s+", RegexOptions.Compiled);
    private static readonly string[] StableRangeTerms = ["过去", "小时", "天", "周", "24", "48", "past", "last", "hour", "hours", "day", "days", "week", "weeks"];
    private static readonly string[] VagueScopeTerms = ["当前", "最近", "整体", "current", "recent", "latest", "overall"];
    private static readonly string[] OutputTerms = ["输出", "只输出", "列表", "清单", "格式", "json", "output", "only output", "list", "checklist", "format", "sections", "1)", "1."];
    private static readonly string[] SideEffectTerms = ["发送", "发布", "写入", "修改", "删除", "send", "post", "publish", "write", "modify", "delete"];
    private static readonly string[] ConfirmationTerms = ["确认", "批准", "用户明确", "confirm", "confirmation", "approval", "approved", "explicitly asked"];

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
            blockingIssues.Add("Missing name.");
        if (string.IsNullOrWhiteSpace(candidate.Prompt))
            blockingIssues.Add("Missing prompt.");
        if (string.IsNullOrWhiteSpace(candidate.Schedule))
            blockingIssues.Add("Missing schedule.");
        if (string.IsNullOrWhiteSpace(candidate.DeliveryChannelId))
            blockingIssues.Add("Missing delivery channel.");
        else if (!availableDeliveryChannelIds.Contains(candidate.DeliveryChannelId))
            blockingIssues.Add("Delivery channel does not exist.");

        if (!string.IsNullOrWhiteSpace(normalizedName) && string.Equals(normalizedName, normalizedPrompt, StringComparison.Ordinal))
            blockingIssues.Add("Name and prompt must not be identical.");
        if (!hasStableInputScope)
            blockingIssues.Add("Prompt does not define a stable input range for scheduled execution.");
        if (!hasOutputFormat)
            blockingIssues.Add("Prompt does not define a clear expected output.");
        if (hasSideEffect && !hasExplicitConfirmation)
            blockingIssues.Add("Automation with external side effects lacks explicit user confirmation.");
        if (isDuplicate)
            blockingIssues.Add("Candidate duplicates an existing automation.");

        if (ContainsAny(candidate.Prompt, VagueScopeTerms) && !hasStableInputScope)
            warnings.Add("Prompt contains vague scope terms without defining a stable input range.");
        if (string.Equals(candidate.Schedule, "@daily", StringComparison.OrdinalIgnoreCase) && !hasOutputFormat)
            warnings.Add("Daily execution without a clear output format can produce low-value results.");
        if (!candidate.RetryPolicy.Enabled)
            warnings.Add("Automation retry policy is disabled.");

        var dimensions = new[]
        {
            BuildDimension("intent_clarity", string.Equals(intent.Intent, "custom_automation", StringComparison.OrdinalIgnoreCase) ? 55 : 90, "Whether the intent has a clear purpose."),
            BuildDimension("input_scope", hasStableInputScope ? 90 : 25, "Whether the input scope is stable."),
            BuildDimension("output_clarity", hasOutputFormat ? 90 : 25, "Whether the output format is clear."),
            BuildDimension("schedule_match", ScoreSchedule(candidate.Schedule, intent.CadenceHint), "Whether the schedule matches the task value."),
            BuildDimension("safety", hasSideEffect && !hasExplicitConfirmation ? 20 : 90, "Whether external side effects are controlled."),
            BuildDimension("noise_risk", hasOutputFormat && hasStableInputScope ? 85 : 35, "Whether the automation is likely to produce low-value output."),
            BuildDimension("user_value", string.Equals(intent.ExpectedOutcome, "unspecified", StringComparison.OrdinalIgnoreCase) ? 55 : 85, "Whether the automation reduces repeated work."),
            BuildDimension("duplicate_risk", isDuplicate ? 15 : 90, "Whether the candidate duplicates an existing automation.")
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

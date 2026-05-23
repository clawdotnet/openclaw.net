using OpenClaw.Core.Models;

namespace OpenClaw.Gateway;

internal sealed class AutomationSuggestionPreviewBuilder
{
    public LearningAutomationSuggestionPreview Build(string originalPrompt, AutomationDefinition candidate, AutomationSuggestionIntent intent, AutomationSuggestionQualityResult quality)
        => new()
        {
            WhySuggested = BuildWhySuggested(intent),
            OriginalPrompt = originalPrompt,
            RefinedPrompt = candidate.Prompt,
            QualityScore = quality.Score,
            QualityDecision = quality.Decision,
            Warnings = BuildWarnings(originalPrompt, candidate.Prompt, quality).ToArray(),
            ExpectedOutputSections = BuildExpectedOutputSections(intent).ToArray()
        };

    private static string BuildWhySuggested(AutomationSuggestionIntent intent)
    {
        if (intent.TriggerEvidence.Count > 0)
            return "检测到用户多次提出相似请求。";
        return "检测到可复用的重复请求意图。";
    }

    private static IEnumerable<string> BuildWarnings(string originalPrompt, string refinedPrompt, AutomationSuggestionQualityResult quality)
    {
        foreach (var warning in quality.Warnings)
            yield return warning;

        if (originalPrompt.Contains("当前", StringComparison.OrdinalIgnoreCase) &&
            refinedPrompt.Contains("过去 24 小时", StringComparison.OrdinalIgnoreCase))
        {
            yield return "原始提示中的“当前”已替换为“过去 24 小时”，这样定时任务才有稳定输入范围。";
        }
    }

    private static IEnumerable<string> BuildExpectedOutputSections(AutomationSuggestionIntent intent)
    {
        if (!string.Equals(intent.Intent, "daily_conversation_review", StringComparison.OrdinalIgnoreCase))
            yield break;

        yield return "unfinishedItems";
        yield return "rememberedPreferences";
        yield return "risks";
        yield return "nextActions";
    }
}

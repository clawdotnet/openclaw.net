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
            return "Similar requests were observed multiple times.";
        return "A reusable repeated request intent was detected.";
    }

    private static IEnumerable<string> BuildWarnings(string originalPrompt, string refinedPrompt, AutomationSuggestionQualityResult quality)
    {
        foreach (var warning in quality.Warnings)
            yield return warning;

        if ((originalPrompt.Contains("当前", StringComparison.OrdinalIgnoreCase) ||
                originalPrompt.Contains("current", StringComparison.OrdinalIgnoreCase)) &&
            refinedPrompt.Contains("past 24 hours", StringComparison.OrdinalIgnoreCase))
        {
            yield return "The original prompt's current scope was replaced with the past 24 hours so the scheduled task has a stable input range.";
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

using OpenClaw.Core.Models;

namespace OpenClaw.Gateway;

internal sealed class AutomationSuggestionIntentExtractor
{
    private static readonly string[] ConversationTerms = ["会话", "对话", "conversation", "chat"];
    private static readonly string[] ReviewTerms = ["回顾", "总结", "评估", "比较", "review", "summarize", "summary", "evaluate", "compare"];
    private static readonly string[] CurrentTerms = ["当前", "最近", "current", "recent"];
    private static readonly string[] RangeTerms = ["过去", "小时", "天", "周", "24", "48", "hour", "day", "week"];
    private static readonly string[] OutputTerms = ["输出", "列表", "清单", "格式", "只输出", "output", "list", "format", "json"];

    public AutomationSuggestionIntent Extract(string prompt, IReadOnlyList<SessionSearchHit> triggerEvidence)
    {
        var normalizedPrompt = prompt.Trim();
        var lowerPrompt = normalizedPrompt.ToLowerInvariant();
        var ambiguities = new List<string>();

        if (ContainsAny(lowerPrompt, CurrentTerms) && !ContainsAny(lowerPrompt, RangeTerms))
            ambiguities.Add("The scheduled automation input range is unstable.");

        if (ContainsAny(lowerPrompt, ["比较", "compare"]) && !ContainsAny(lowerPrompt, ["与", "相比", "against", "versus", "baseline"]))
            ambiguities.Add("The comparison baseline is not specified.");

        if (ContainsAny(lowerPrompt, ReviewTerms) && !ContainsAny(lowerPrompt, OutputTerms))
            ambiguities.Add("The output format is not specified.");

        var isConversationReview = ContainsAny(lowerPrompt, ConversationTerms) && ContainsAny(lowerPrompt, ReviewTerms);
        return new AutomationSuggestionIntent
        {
            Intent = isConversationReview ? "daily_conversation_review" : "custom_automation",
            TargetObject = isConversationReview ? "recent_conversations" : "unspecified",
            ExpectedOutcome = isConversationReview ? "actionable_followup_list" : "unspecified",
            CadenceHint = ExtractCadenceHint(lowerPrompt),
            TriggerEvidence = BuildTriggerEvidence(normalizedPrompt, triggerEvidence),
            Ambiguities = ambiguities.ToArray()
        };
    }

    private static string ExtractCadenceHint(string prompt)
    {
        if (ContainsAny(prompt, ["每周", "weekly", "week"]))
            return "weekly";
        if (ContainsAny(prompt, ["每小时", "hourly", "hour"]))
            return "hourly";
        return "daily";
    }

    private static string[] BuildTriggerEvidence(string prompt, IReadOnlyList<SessionSearchHit> triggerEvidence)
        => triggerEvidence
            .Select(static item => item.Snippet.Trim())
            .Where(static snippet => !string.IsNullOrWhiteSpace(snippet))
            .Append(prompt)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .Take(5)
            .ToArray();

    private static bool ContainsAny(string value, IReadOnlyList<string> terms)
        => terms.Any(term => value.Contains(term, StringComparison.OrdinalIgnoreCase));
}

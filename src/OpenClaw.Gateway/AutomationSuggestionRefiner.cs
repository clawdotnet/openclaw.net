using OpenClaw.Core.Models;

namespace OpenClaw.Gateway;

internal sealed class AutomationSuggestionRefiner
{
    public AutomationDefinition Refine(string originalPrompt, AutomationSuggestionIntent intent)
    {
        if (string.Equals(intent.Intent, "daily_conversation_review", StringComparison.OrdinalIgnoreCase))
        {
            return new AutomationDefinition
            {
                Id = $"suggested:{Guid.NewGuid():N}"[..20],
                Name = "Daily conversation follow-up review",
                Enabled = false,
                Schedule = "@daily",
                Prompt = "Every day, review conversations from the past 24 hours. Output only: 1) unfinished items; 2) preferences the user explicitly asked to remember; 3) risks that need follow-up; 4) recommended next actions. Do not provide a generic summary, do not evaluate the user, and do not repeat completed items. If there is nothing worth following up on, output that there are no follow-up items today.",
                DeliveryChannelId = "cron",
                Tags = ["suggested", "learning", "conversation-review"],
                IsDraft = true,
                Source = "learning",
                TemplateKey = "custom"
            };
        }

        return new AutomationDefinition
        {
            Id = $"suggested:{Guid.NewGuid():N}"[..20],
            Name = originalPrompt.Length > 60 ? originalPrompt[..60] : originalPrompt,
            Enabled = false,
            Schedule = "@daily",
            Prompt = originalPrompt,
            DeliveryChannelId = "cron",
            Tags = ["suggested", "learning"],
            IsDraft = true,
            Source = "learning",
            TemplateKey = "custom"
        };
    }
}

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
                Name = "每日回顾会话中的待办和风险",
                Enabled = false,
                Schedule = "@daily",
                Prompt = "每天回顾过去 24 小时内的会话内容。只输出：1) 未完成事项；2) 用户明确要求记住的偏好；3) 需要跟进的风险；4) 建议的下一步动作。不要泛泛总结，不要评价用户，不要重复已经完成的事项。如果没有值得跟进的内容，输出“今天没有需要跟进的事项”。",
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

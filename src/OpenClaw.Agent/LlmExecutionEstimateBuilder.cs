using Microsoft.Extensions.AI;
using OpenClaw.Core.Models;

namespace OpenClaw.Agent;

internal static class LlmExecutionEstimateBuilder
{
    public static int EstimateInputTokens(IReadOnlyList<ChatMessage> messages, int additionalSystemPromptChars = 0)
    {
        var charCount = Math.Max(0, additionalSystemPromptChars);
        foreach (var message in messages)
        {
            foreach (var content in message.Contents)
            {
                var value = content.ToString();
                if (!string.IsNullOrEmpty(value))
                    charCount += value.Length;
            }
        }

        return EstimateTokenCount(charCount);
    }

    public static int EstimateTokenCount(int charCount)
    {
        if (charCount <= 0)
            return 0;

        // Approximation used only when providers omit usage details.
        return Math.Max(1, (charCount + 3) / 4);
    }

    public static LlmExecutionEstimate Create(
        IReadOnlyList<ChatMessage> messages,
        int skillPromptLength,
        int additionalSystemPromptChars = 0)
    {
        var estimatedInputTokens = EstimateInputTokens(messages, additionalSystemPromptChars);
        return new LlmExecutionEstimate
        {
            EstimatedInputTokens = estimatedInputTokens,
            EstimatedInputTokensByComponent = BuildInputTokenEstimate(
                messages,
                estimatedInputTokens,
                skillPromptLength,
                additionalSystemPromptChars)
        };
    }

    public static InputTokenComponentEstimate BuildInputTokenEstimate(
        IReadOnlyList<ChatMessage> messages,
        long totalInputTokens,
        int skillPromptLength,
        int additionalSystemPromptChars = 0)
    {
        long systemChars = Math.Max(0, additionalSystemPromptChars);
        long historyChars = 0;
        long toolChars = 0;
        long userChars = 0;

        for (var i = 0; i < messages.Count; i++)
        {
            var message = messages[i];
            long chars = 0;
            foreach (var content in message.Contents)
            {
                var value = content.ToString();
                if (!string.IsNullOrEmpty(value))
                    chars += value.Length;
            }

            if (i == 0 && message.Role == ChatRole.System)
            {
                systemChars += chars;
                continue;
            }

            if (message.Role == ChatRole.Tool)
            {
                toolChars += chars;
                continue;
            }

            if (message.Role == ChatRole.User && i == messages.Count - 1)
            {
                userChars += chars;
                continue;
            }

            historyChars += chars;
        }

        var skillChars = Math.Min(systemChars, skillPromptLength);
        var systemPromptChars = Math.Max(0, systemChars - skillChars);

        return DistributeEstimatedTokens(
            totalInputTokens,
            systemPromptChars,
            skillChars,
            historyChars,
            toolChars,
            userChars);
    }

    private static InputTokenComponentEstimate DistributeEstimatedTokens(
        long totalTokens,
        long systemPromptChars,
        long skillChars,
        long historyChars,
        long toolChars,
        long userChars)
    {
        var totalChars = systemPromptChars + skillChars + historyChars + toolChars + userChars;
        if (totalTokens <= 0 || totalChars <= 0)
            return new InputTokenComponentEstimate();

        var systemTokens = (long)Math.Round(totalTokens * (double)systemPromptChars / totalChars);
        var skillTokens = (long)Math.Round(totalTokens * (double)skillChars / totalChars);
        var historyTokens = (long)Math.Round(totalTokens * (double)historyChars / totalChars);
        var toolTokens = (long)Math.Round(totalTokens * (double)toolChars / totalChars);
        var userTokens = Math.Max(0, totalTokens - systemTokens - skillTokens - historyTokens - toolTokens);

        return new InputTokenComponentEstimate
        {
            SystemPrompt = systemTokens,
            Skills = skillTokens,
            History = historyTokens,
            ToolOutputs = toolTokens,
            UserInput = userTokens
        };
    }
}

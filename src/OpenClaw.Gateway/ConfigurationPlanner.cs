using System.Text.Json;
using Microsoft.Extensions.AI;
using OpenClaw.Core.Models;
using OpenClaw.Core.Security;

namespace OpenClaw.Gateway;

internal static class ConfigurationPlanner
{
    public static async Task<Dictionary<string, JsonElement>> ProposeAsync(
        IChatClient client, string modelId, string instruction, ConfigurationState state, CancellationToken ct)
    {
        var catalog = string.Join("\n", state.Values.Select(p => $"{p.Key}: {p.Value.ValueKind}; choices: {string.Join(", ", ConfigurationSettingChoices.For(p.Key))}"));
        var response = await client.GetResponseAsync(
            [new ChatMessage(ChatRole.System, "Translate the user's configuration request into a JSON object with a changes object. Use only the following exact setting names and JSON types. Boolean types True/False mean boolean. Never invent values or modify unrelated settings. Return an empty changes object if clarification is needed. No markdown, tools, or explanations.\n" + catalog),
             new ChatMessage(ChatRole.User, new BaselineSecretRedactor().Redact(instruction))],
            new ChatOptions { ModelId = modelId, Temperature = 0, MaxOutputTokens = 2048 }, ct);
        var interpreted = JsonSerializer.Deserialize(response.Text, ConfigurationJsonContext.Default.ConfigurationRequest);
        return interpreted?.Changes ?? [];
    }
}

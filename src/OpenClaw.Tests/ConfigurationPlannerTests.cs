using Microsoft.Extensions.AI;
using NSubstitute;
using OpenClaw.Core.Models;
using OpenClaw.Gateway;
using System.Text.Json;
using Xunit;

namespace OpenClaw.Tests;

public sealed class ConfigurationPlannerTests
{
    [Fact]
    public async Task Planner_UsesNoToolsOrExistingValues_AndProducesAValidatedDraftOnly()
    {
        var client = Substitute.For<IChatClient>();
        List<ChatMessage>? captured = null;
        ChatOptions? options = null;
        client.GetResponseAsync(Arg.Any<IEnumerable<ChatMessage>>(), Arg.Any<ChatOptions>(), Arg.Any<CancellationToken>())
            .Returns(call =>
            {
                captured = call.ArgAt<IEnumerable<ChatMessage>>(0).ToList();
                options = call.ArgAt<ChatOptions>(1);
                return new ChatResponse(new ChatMessage(ChatRole.Assistant, "{\"changes\":{\"sessionTimeoutMinutes\":45}}"));
            });
        using var value = JsonDocument.Parse("1234567");
        var state = new ConfigurationState { Values = new() { ["sessionTimeoutMinutes"] = value.RootElement.Clone() } };
        var draft = await ConfigurationPlanner.ProposeAsync(client, "test-model", "Set session timeout to 45 minutes. api_key: sk-abcdefghijklmnop", state, CancellationToken.None);
        Assert.Equal(45, draft["sessionTimeoutMinutes"].GetInt32());
        Assert.NotNull(captured);
        Assert.DoesNotContain("1234567", captured[0].Text);
        Assert.DoesNotContain("sk-abcdefghijklmnop", captured[1].Text);
        Assert.Null(options!.Tools);
    }
}

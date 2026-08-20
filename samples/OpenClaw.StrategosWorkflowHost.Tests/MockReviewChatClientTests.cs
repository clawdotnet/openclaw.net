using System.Text.Json;
using Microsoft.Extensions.AI;
using OpenClaw.StrategosWorkflowHost.Configuration;
using OpenClaw.StrategosWorkflowHost.Workflows.Models;
using Xunit;

namespace OpenClaw.StrategosWorkflowHost.Tests;

public class MockReviewChatClientTests
{
    [Theory]
    [InlineData("security")]
    [InlineData("architecture")]
    [InlineData("cost")]
    public async Task GetResponseAsync_ReturnsRoleSpecificVerdictJson(string role)
    {
        var client = new MockReviewChatClient();
        var messages = new[] { new ChatMessage(ChatRole.User, role) };

        var response = await client.GetResponseAsync(messages);
        var text = response.Messages[0].Text;

        var verdict = JsonSerializer.Deserialize<ReviewVerdict>(text);
        Assert.NotNull(verdict);
        Assert.Equal(role, verdict!.Role);
        Assert.Equal("review-required", verdict.Verdict);
        Assert.Equal(0.8, verdict.Confidence);
    }

    [Fact]
    public async Task GetResponseAsync_UnknownRoleDefaultsToSecurity()
    {
        var client = new MockReviewChatClient();
        var messages = new[] { new ChatMessage(ChatRole.User, "something-else") };

        var response = await client.GetResponseAsync(messages);
        var verdict = JsonSerializer.Deserialize<ReviewVerdict>(response.Messages[0].Text);

        Assert.Equal("security", verdict!.Role);
    }
}

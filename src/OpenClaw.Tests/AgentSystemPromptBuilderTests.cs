using OpenClaw.Agent;
using OpenClaw.Core.Models;
using Xunit;

namespace OpenClaw.Tests;

public sealed class AgentSystemPromptBuilderTests
{
    [Fact]
    public void ApplyResponseMode_AddsOperationalInstructions_ForConciseOps()
    {
        var prompt = AgentSystemPromptBuilder.ApplyResponseMode("Base prompt", SessionResponseModes.ConciseOps);

        Assert.Contains("Operational Response Mode", prompt, StringComparison.Ordinal);
        Assert.Contains("state the action taken", prompt, StringComparison.Ordinal);
    }

    [Fact]
    public void ApplyResponseMode_LeavesPromptUnchanged_ForDefaultAndFull()
    {
        Assert.Equal("Base prompt", AgentSystemPromptBuilder.ApplyResponseMode("Base prompt", SessionResponseModes.Default));
        Assert.Equal("Base prompt", AgentSystemPromptBuilder.ApplyResponseMode("Base prompt", SessionResponseModes.Full));
    }
}

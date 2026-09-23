using Microsoft.Extensions.AI;
using NSubstitute;
using OpenClaw.Agent;
using OpenClaw.Core.Abstractions;
using OpenClaw.Core.Models;
using OpenClaw.Core.Observability;
using Xunit;
namespace OpenClaw.Tests;
public sealed class AudiencePolicyTests
{
    private static Session Session() => new() { Id = "room-1", ChannelId = "slack", SenderId = "user" };
    private static GatewayConfig Config() { var c = new GatewayConfig(); c.Tooling.Audiences.Enabled = true; return c; }
    private static OpenClawToolExecutor Executor(GatewayConfig config, params ITool[] tools) => new(tools, 5, false, [], [], config: config);

    [Fact]
    public async Task AudienceDeniesDirectExecutionEvenWhenRouteAllowsTool()
    {
        var tool = Substitute.For<ITool>(); tool.Name.Returns("shell"); tool.Description.Returns("shell"); tool.ParameterSchema.Returns("{\"type\":\"object\"}");
        var executor = Executor(Config(), tool);
        var session = Session(); session.RouteAllowedTools = ["shell"];
        Assert.Empty(executor.GetToolDeclarations(session));
        var result = await executor.ExecuteAsync("shell", "{}", null, session, new TurnContext { SessionId = session.Id, ChannelId = session.ChannelId }, false, null, TestContext.Current.CancellationToken);
        Assert.Equal(ToolResultStatuses.Blocked, result.ResultStatus);
        await tool.DidNotReceiveWithAnyArgs().ExecuteAsync(default!, default);
    }
    [Fact]
    public void UnknownProfileAndUnmappedChannelFailClosed()
    {
        var config = Config();
        Assert.False(AudiencePolicy.AllowsTool(AudiencePolicy.Resolve(config.Tooling.Audiences, Session()), "shell"));
        config.Tooling.Audiences.ChannelBindings["slack"] = "personal";
        config.Tooling.Audiences.SessionBindings["room-1"] = "typo";
        Assert.False(AudiencePolicy.AllowsTool(AudiencePolicy.Resolve(config.Tooling.Audiences, Session()), "shell"));
    }
    [Fact]
    public void AudienceDowngradeRejectsExistingPrivateHistory()
    {
        var config = Config(); config.Tooling.Audiences.ChannelBindings["slack"] = "personal";
        var executor = Executor(config); var session = Session(); Assert.Null(executor.PrepareAudienceTurn(session, "hello"));
        session.History.Add(new() { Role = "assistant", Content = "private" });
        config.Tooling.Audiences.ChannelBindings["slack"] = "public";
        Assert.Contains("narrowed", executor.PrepareAudienceTurn(session, "hello"), StringComparison.OrdinalIgnoreCase);
        Assert.Single(session.History); // rejection must not delete user history
    }
    [Fact]
    public void LegacyPrivateHistoryCannotEnterPublicAudience()
    {
        var session = Session();
        session.History.Add(new() { Role = "assistant", Content = "legacy private context" });
        Assert.Contains("narrowed", Executor(Config()).PrepareAudienceTurn(session, "hello"), StringComparison.OrdinalIgnoreCase);
        Assert.Null(session.AudienceContextKey);
        Assert.Single(session.History);
        Assert.Null(Executor(new GatewayConfig()).PrepareAudienceTurn(session, "hello"));
    }

    [Fact]
    public void PublicAttachmentsAreRejected()
    {
        Assert.Contains("disabled", Executor(Config()).PrepareAudienceTurn(Session(), "[IMAGE_URL:https://example.com/a.png]"), StringComparison.OrdinalIgnoreCase);
    }
    [Fact]
    public async Task PublicRuntimeSkipsRecallButKeepsRoutePrompt()
    {
        var client = Substitute.For<IChatClient>();
        IList<ChatMessage>? captured = null;
        client.GetResponseAsync(Arg.Do<IList<ChatMessage>>(m => captured = m), Arg.Any<ChatOptions>(), Arg.Any<CancellationToken>())
            .Returns(new ChatResponse(new ChatMessage(ChatRole.Assistant, "ok")));
        var memory = Substitute.For<IMemoryStore, IMemoryNoteSearch>();
        var config = Config();
        var runtime = new AgentRuntime(client, [], memory, new LlmProviderConfig { Provider = "openai", Model = "test" }, 10,
            gatewayConfig: config, recall: new MemoryRecallConfig { Enabled = true });
        var session = Session(); session.SystemPromptOverride = "private-route-sentinel";
        await runtime.RunAsync(session, "hello", TestContext.Current.CancellationToken);
        Assert.NotNull(captured);
        Assert.Contains(captured!, m => m.Text.Contains("private-route-sentinel"));
        await ((IMemoryNoteSearch)memory).DidNotReceiveWithAnyArgs().SearchNotesAsync(default!, default, default, default);
    }

    [Fact]
    public void ToolOnlyPolicyChangesDoNotInvalidateExistingHistory()
    {
        var config = Config();
        var executor = Executor(config); var session = Session();
        Assert.Null(executor.PrepareAudienceTurn(session, "hello"));
        session.History.Add(new() { Role = "assistant", Content = "public" });
        config.Tooling.Audiences.Profiles["public"].AllowedTools = ["Web_Search"];
        Assert.Null(executor.PrepareAudienceTurn(session, "hello"));
        Assert.True(AudiencePolicy.AllowsTool(config.Tooling.Audiences.Profiles["public"], "web_search"));
    }

    [Fact]
    public void AudienceBindingsAndProfilesAreCaseInsensitive()
    {
        var config = Config();
        config.Tooling.Audiences.ChannelBindings["SLACK"] = "TEAM";
        Assert.Same(config.Tooling.Audiences.Profiles["team"], AudiencePolicy.Resolve(config.Tooling.Audiences, Session()));
    }
}

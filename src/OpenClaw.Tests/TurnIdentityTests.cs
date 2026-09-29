using OpenClaw.Core.Models;
using OpenClaw.Gateway.Extensions;
using Xunit;

namespace OpenClaw.Tests;

public sealed class TurnIdentityTests
{
    [Fact]
    public void Apply_WhenMessageCarriesAccount_ShouldRunAsThatAccount()
    {
        var session = NewSession(previousAccount: "acct-a");

        TurnIdentity.Apply(session, Message(authenticatedUserId: "acct-b"));

        Assert.Equal("acct-b", session.AuthenticatedUserId);
    }

    [Fact]
    public void Apply_WhenExternalSenderHasNoAccount_ShouldNotInheritPreviousAccount()
    {
        // An operator wrote into this Telegram session earlier; the Telegram user's own turn must not run as that operator.
        var session = NewSession(previousAccount: "acct-operator");

        TurnIdentity.Apply(session, Message(authenticatedUserId: null));

        Assert.Null(session.AuthenticatedUserId);
    }

    [Theory]
    [InlineData("system")]
    [InlineData("cron")]
    [InlineData("automation")]
    [InlineData("background")]
    public void Apply_WhenTurnActsOnSessionsBehalf_ShouldKeepSessionIdentity(string kind)
    {
        var session = NewSession(previousAccount: "acct-owner");
        var message = kind switch
        {
            "system" => Message(authenticatedUserId: null) with { IsSystem = true },
            "cron" => Message(authenticatedUserId: null) with { CronJobName = "nightly" },
            "automation" => Message(authenticatedUserId: null) with { AutomationRunId = "run-1" },
            _ => Message(authenticatedUserId: null) with { BackgroundRunId = "bg-1" }
        };

        TurnIdentity.Apply(session, message);

        Assert.Equal("acct-owner", session.AuthenticatedUserId);
    }

    private static Session NewSession(string? previousAccount)
        => new()
        {
            Id = "telegram:user-1",
            ChannelId = "telegram",
            SenderId = "user-1",
            AuthenticatedUserId = previousAccount
        };

    private static InboundMessage Message(string? authenticatedUserId)
        => new()
        {
            ChannelId = "telegram",
            SenderId = "user-1",
            Text = "hi",
            AuthenticatedUserId = authenticatedUserId
        };
}

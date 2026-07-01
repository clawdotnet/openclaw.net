using System.Text.Json;
using OpenClaw.Core.Models;
using Xunit;

namespace OpenClaw.Tests;

public sealed class BackgroundSessionModelTests
{
    [Fact]
    public void Session_Defaults_ToIdleBackgroundState()
    {
        var session = new Session
        {
            Id = "websocket:user-1",
            ChannelId = "websocket",
            SenderId = "user-1"
        };

        Assert.Equal(SessionRunState.Idle, session.RunState);
        Assert.Null(session.BackgroundRun);
    }

    [Fact]
    public void Session_BackgroundRun_RoundTripsThroughSourceGeneratedJson()
    {
        var session = new Session
        {
            Id = "telegram:42",
            ChannelId = "telegram",
            SenderId = "42",
            RunState = SessionRunState.Continuing,
            BackgroundRun = new BackgroundRunMetadata
            {
                RunId = "run_abc",
                Objective = "Fix failing tests",
                StartedAtUtc = DateTimeOffset.Parse("2026-07-02T10:00:00Z"),
                LastContinuedAtUtc = DateTimeOffset.Parse("2026-07-02T10:05:00Z"),
                ContinuationCount = 3,
                ContinuationSequence = 7,
                TokenBudget = 128_000,
                MaxContinuationTurns = 200,
                LastCheckpointId = "ckpt_1",
                LastStopReason = "batch_limit"
            }
        };

        var json = JsonSerializer.Serialize(session, CoreJsonContext.Default.Session);
        var loaded = JsonSerializer.Deserialize(json, CoreJsonContext.Default.Session);

        Assert.NotNull(loaded);
        Assert.Equal(SessionRunState.Continuing, loaded!.RunState);
        Assert.NotNull(loaded.BackgroundRun);
        Assert.Equal("run_abc", loaded.BackgroundRun!.RunId);
        Assert.Equal(7, loaded.BackgroundRun.ContinuationSequence);
    }

    [Fact]
    public void LegacySessionJson_DeserializesAsIdle()
    {
        const string json = """
        {
          "id": "websocket:user-1",
          "channelId": "websocket",
          "senderId": "user-1",
          "createdAt": "2026-07-02T10:00:00Z",
          "lastActiveAt": "2026-07-02T10:00:00Z",
          "history": [],
          "state": 0
        }
        """;

        var loaded = JsonSerializer.Deserialize(json, CoreJsonContext.Default.Session);

        Assert.NotNull(loaded);
        Assert.Equal(SessionRunState.Idle, loaded!.RunState);
        Assert.Null(loaded.BackgroundRun);
    }

    [Fact]
    public void GatewayConfig_BackgroundExecution_DefaultsToEnabledAndAutoResume()
    {
        var config = new GatewayConfig();

        Assert.True(config.BackgroundExecution.Enabled);
        Assert.True(config.BackgroundExecution.AutoResumeOnStartup);
        Assert.Equal(3, config.BackgroundExecution.MaxConcurrentBackgroundTurns);
        Assert.Equal(10, config.BackgroundExecution.MaxIterationsPerBatch);
        Assert.Equal(128_000, config.BackgroundExecution.DefaultTokenBudget);
    }
}

using Microsoft.Extensions.Logging;
using NSubstitute;
using OpenClaw.Agent.Tools;
using OpenClaw.Core.Abstractions;
using OpenClaw.Core.Loops;
using OpenClaw.Core.Models;
using OpenClaw.Core.Observability;
using Xunit;

namespace OpenClaw.Tests;

public sealed class LoopControlToolTests
{
    private readonly ILoopControlService _mockControl = Substitute.For<ILoopControlService>();
    private readonly ILogger<LoopTerminationDetector> _mockLogger = Substitute.For<ILogger<LoopTerminationDetector>>();

    [Fact]
    public void Tool_HasExpectedMetadata()
    {
        var detector = new LoopTerminationDetector(_mockControl, _mockLogger);
        var tool = new LoopControlTool(detector);

        Assert.Equal("loop_control", tool.Name);
        Assert.Contains("complete", tool.Description);
        Assert.Contains("status", tool.ParameterSchema);
    }

    [Fact]
    public async Task Execute_Complete_CallsDetector()
    {
        var detector = new LoopTerminationDetector(_mockControl, _mockLogger);
        var tool = new LoopControlTool(detector);

        var context = new ToolExecutionContext
        {
            Session = new Session { Id = "s1", ChannelId = "cli", SenderId = "test" },
            TurnContext = new TurnContext { SessionId = "s1", ChannelId = "cli" }
        };

        var result = await tool.ExecuteAsync("""{"status":"complete"}""", context, TestContext.Current.CancellationToken);

        Assert.Contains("stopped", result);
        await _mockControl.Received(1).SignalCompleteAsync("s1", Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task Execute_InvalidStatus_ReturnsError()
    {
        var detector = new LoopTerminationDetector(_mockControl, _mockLogger);
        var tool = new LoopControlTool(detector);

        var context = new ToolExecutionContext
        {
            Session = new Session { Id = "s1", ChannelId = "cli", SenderId = "test" },
            TurnContext = new TurnContext { SessionId = "s1", ChannelId = "cli" }
        };

        var result = await tool.ExecuteAsync("""{"status":"paused"}""", context, TestContext.Current.CancellationToken);
        Assert.Contains("Error", result);
        await _mockControl.DidNotReceive().SignalCompleteAsync(Arg.Any<string>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task Execute_NoContext_ReturnsError()
    {
        var detector = new LoopTerminationDetector(_mockControl, _mockLogger);
        var tool = new LoopControlTool(detector);

        var result = await tool.ExecuteAsync("{}", TestContext.Current.CancellationToken);
        Assert.Contains("Error", result);
    }
}

public sealed class LoopTerminationDetectorTests
{
    private readonly ILoopControlService _mockControl = Substitute.For<ILoopControlService>();
    private readonly ILogger<LoopTerminationDetector> _mockLogger = Substitute.For<ILogger<LoopTerminationDetector>>();

    [Theory]
    [InlineData("LOOP_TERMINATE")]
    [InlineData("DONE")]
    [InlineData("WORK_COMPLETE")]
    public async Task ScanText_KeywordMatch_SignalsComplete(string text)
    {
        var detector = new LoopTerminationDetector(_mockControl, _mockLogger);

        var result = await detector.ScanTextAsync("s1", text, TestContext.Current.CancellationToken);

        Assert.True(result);
        await _mockControl.Received(1).SignalCompleteAsync("s1", Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task ScanText_NoKeyword_DoesNotSignal()
    {
        var detector = new LoopTerminationDetector(_mockControl, _mockLogger);

        var result = await detector.ScanTextAsync("s1", "just a normal response", TestContext.Current.CancellationToken);

        Assert.False(result);
        await _mockControl.DidNotReceive().SignalCompleteAsync(Arg.Any<string>(), Arg.Any<CancellationToken>());
    }

    [Theory]
    [InlineData("ABANDONED")]
    [InlineData("UNDONE")]
    [InlineData("WORK_COMPLETED")]
    [InlineData("PRELOOP_TERMINATE")]
    public async Task ScanText_KeywordSubstring_DoesNotSignal(string text)
    {
        var detector = new LoopTerminationDetector(_mockControl, _mockLogger);

        var result = await detector.ScanTextAsync("s1", text, TestContext.Current.CancellationToken);

        Assert.False(result);
        await _mockControl.DidNotReceive().SignalCompleteAsync(Arg.Any<string>(), Arg.Any<CancellationToken>());
    }

    [Theory]
    [InlineData("DONE.")]
    [InlineData("Status: WORK_COMPLETE")]
    [InlineData("(LOOP_TERMINATE)")]
    public async Task ScanText_WholeKeyword_SignalsComplete(string text)
    {
        var detector = new LoopTerminationDetector(_mockControl, _mockLogger);

        var result = await detector.ScanTextAsync("s1", text, TestContext.Current.CancellationToken);

        Assert.True(result);
        await _mockControl.Received(1).SignalCompleteAsync("s1", Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task ScanText_EmptyOrNull_ReturnsFalse()
    {
        var detector = new LoopTerminationDetector(_mockControl, _mockLogger);

        Assert.False(await detector.ScanTextAsync("s1", "", TestContext.Current.CancellationToken));
        Assert.False(await detector.ScanTextAsync("s1", null!, TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task OnToolComplete_SignalsComplete()
    {
        var detector = new LoopTerminationDetector(_mockControl, _mockLogger);

        await detector.OnToolCompleteAsync("s1", TestContext.Current.CancellationToken);

        await _mockControl.Received(1).SignalCompleteAsync("s1", Arg.Any<CancellationToken>());
    }
}

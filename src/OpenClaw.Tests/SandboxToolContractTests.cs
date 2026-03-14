using OpenClaw.Agent.Tools;
using OpenClaw.Core.Abstractions;
using OpenClaw.Core.Models;
using OpenClaw.Core.Plugins;
using Xunit;

namespace OpenClaw.Tests;

public sealed class SandboxToolContractTests
{
    [Fact]
    public void ShellTool_CreateSandboxRequest_WrapsCommandInShell()
    {
        var tool = new ShellTool(new ToolingConfig
        {
            AllowShell = true
        });

        var request = ((ISandboxCapableTool)tool).CreateSandboxRequest("""{"command":"echo hello","timeout_seconds":15}""");

        Assert.Equal("/bin/sh", request.Command);
        Assert.Equal(["-lc", "if command -v timeout >/dev/null 2>&1; then exec timeout 15s '/bin/sh' '-lc' 'echo hello'; else exec '/bin/sh' '-lc' 'echo hello'; fi"], request.Arguments);
    }

    [Fact]
    public void ShellTool_FormatSandboxResult_MatchesExitPrefix()
    {
        var tool = new ShellTool(new ToolingConfig
        {
            AllowShell = true
        });

        var result = ((ISandboxCapableTool)tool).FormatSandboxResult(
            """{"command":"echo hello"}""",
            new SandboxResult
            {
                ExitCode = 0,
                Stdout = "hello",
                Stderr = string.Empty
            });

        Assert.Equal("[exit: 0]\nhello", result);
    }

    [Theory]
    [InlineData("python", "print(1)", "python3", "-c")]
    [InlineData("javascript", "console.log(1)", "node", "-e")]
    [InlineData("bash", "echo 1", "bash", "-lc")]
    public void CodeExecTool_CreateSandboxRequest_UsesInterpreterInlineExecution(
        string language,
        string code,
        string interpreter,
        string expectedFlag)
    {
        var tool = new CodeExecTool(new CodeExecConfig
        {
            Enabled = true
        });

        var request = ((ISandboxCapableTool)tool).CreateSandboxRequest(
            $$"""{"language":"{{language}}","code":"{{code}}","timeout_seconds":12}""");

        Assert.Equal("/bin/sh", request.Command);
        Assert.Equal("-lc", request.Arguments[0]);
        Assert.Contains($"'{interpreter}' '{expectedFlag}'", request.Arguments[1], StringComparison.Ordinal);
    }

    [Fact]
    public void BrowserTool_CreateSandboxRequest_GeneratesNodeRunnerForGoto()
    {
        var tool = new BrowserTool(new ToolingConfig
        {
            EnableBrowserTool = true,
            BrowserHeadless = true,
            BrowserTimeoutSeconds = 20
        });

        var request = ((ISandboxCapableTool)tool).CreateSandboxRequest(
            """{"action":"goto","url":"https://example.com"}""");

        Assert.Equal("node", request.Command);
        Assert.Equal("-e", request.Arguments[0]);
        Assert.Contains("playwright", request.Arguments[1], StringComparison.Ordinal);
        Assert.Contains("\"action\":\"goto\"", request.Arguments[2], StringComparison.Ordinal);
        Assert.Contains("\"url\":\"https://example.com\"", request.Arguments[2], StringComparison.Ordinal);
    }

    [Fact]
    public void BrowserTool_CreateSandboxRequest_EvaluateHonorsConfig()
    {
        var tool = new BrowserTool(new ToolingConfig
        {
            EnableBrowserTool = true,
            AllowBrowserEvaluate = false
        });

        var ex = Assert.Throws<ToolSandboxException>(() =>
            ((ISandboxCapableTool)tool).CreateSandboxRequest("""{"action":"evaluate","script":"1+1"}"""));

        Assert.Contains("Browser evaluate is disabled", ex.Message, StringComparison.Ordinal);
    }
}

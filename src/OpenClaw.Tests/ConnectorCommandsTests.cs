using System.Text.Json;
using OpenClaw.Cli;
using OpenClaw.Core.Models;
using Xunit;

namespace OpenClaw.Tests;

public sealed class ConnectorCommandsTests
{
    [Fact]
    public async Task RunAsync_Help_ReturnsZeroAndPrintsHelp()
    {
        using var output = new StringWriter();
        using var error = new StringWriter();

        var exit = await ConnectorCommands.RunAsync(["--help"], output, error);

        Assert.Equal(0, exit);
        var text = output.ToString();
        Assert.Contains("connector execute", text, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task RunAsync_MissingProposalFile_ReturnsTwoAndPrintsError()
    {
        using var output = new StringWriter();
        using var error = new StringWriter();

        var exit = await ConnectorCommands.RunAsync(["execute"], output, error);

        Assert.Equal(2, exit);
        Assert.Contains("--proposal-file", error.ToString(), StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task RunAsync_Execute_WithUnknownConnector_PrintsPolicyDeniedAndReturns1()
    {
        var proposalPath = Path.GetTempFileName();
        try
        {
            await File.WriteAllTextAsync(proposalPath, ValidProposalJson());

            using var output = new StringWriter();
            using var error = new StringWriter();

            var exit = await ConnectorCommands.RunAsync(
                ["execute", "--proposal-file", proposalPath, "--json"],
                output,
                error,
                executeAction: (_, _) => Task.FromResult(new IntegrationConnectorActionExecuteResponse
                {
                    Status = "failed",
                    FailureCode = "policy_denied",
                    Message = "Connector not found or access denied."
                }));

            Assert.Equal(1, exit);
            var text = output.ToString();
            Assert.Contains("policy_denied", text, StringComparison.OrdinalIgnoreCase);
        }
        finally
        {
            File.Delete(proposalPath);
        }
    }

    [Fact]
    public async Task RunAsync_Execute_Success_ReturnsZero()
    {
        var proposalPath = Path.GetTempFileName();
        try
        {
            await File.WriteAllTextAsync(proposalPath, ValidProposalJson());

            using var output = new StringWriter();
            using var error = new StringWriter();

            var exit = await ConnectorCommands.RunAsync(
                ["execute", "--proposal-file", proposalPath],
                output,
                error,
                executeAction: (_, _) => Task.FromResult(new IntegrationConnectorActionExecuteResponse
                {
                    Status = "success",
                    Decision = "proceed_execute"
                }));

            Assert.Equal(0, exit);
            var text = output.ToString();
            Assert.Contains("success", text, StringComparison.OrdinalIgnoreCase);
        }
        finally
        {
            File.Delete(proposalPath);
        }
    }

    [Fact]
    public async Task RunAsync_Execute_JsonOutput_SerializesFullResponse()
    {
        var proposalPath = Path.GetTempFileName();
        try
        {
            await File.WriteAllTextAsync(proposalPath, ValidProposalJson());

            using var output = new StringWriter();
            using var error = new StringWriter();

            var exit = await ConnectorCommands.RunAsync(
                ["execute", "--proposal-file", proposalPath, "--json"],
                output,
                error,
                executeAction: (_, _) => Task.FromResult(new IntegrationConnectorActionExecuteResponse
                {
                    Status = "success",
                    Decision = "proceed_execute",
                    Message = "Action executed successfully.",
                    FailureCode = null,
                    ReasonCodes = ["ok"],
                    RequiredApprovals = [],
                    Constraints = []
                }));

            Assert.Equal(0, exit);
            var text = output.ToString();
            Assert.Contains("\"status\":\"success\"", text, StringComparison.OrdinalIgnoreCase);
            Assert.Contains("\"decision\":\"proceed_execute\"", text, StringComparison.OrdinalIgnoreCase);
            Assert.Contains("\"reasonCodes\":[\"ok\"]", text, StringComparison.Ordinal);
        }
        finally
        {
            File.Delete(proposalPath);
        }
    }

    [Fact]
    public async Task RunAsync_Execute_WithDecisionOverride_PassesDecisionToRequest()
    {
        var proposalPath = Path.GetTempFileName();
        string? capturedDecision = null;
        try
        {
            await File.WriteAllTextAsync(proposalPath, ValidProposalJson());

            using var output = new StringWriter();
            using var error = new StringWriter();

            var exit = await ConnectorCommands.RunAsync(
                ["execute", "--proposal-file", proposalPath, "--decision", "require_approval"],
                output,
                error,
                executeAction: (request, _) =>
                {
                    capturedDecision = request.Decision;
                    return Task.FromResult(new IntegrationConnectorActionExecuteResponse
                    {
                        Status = "success",
                        Decision = request.Decision
                    });
                });

            Assert.Equal(0, exit);
            Assert.Equal("require_approval", capturedDecision);
        }
        finally
        {
            File.Delete(proposalPath);
        }
    }

    [Fact]
    public async Task RunAsync_Execute_NonExistentProposalFile_ReturnsOne()
    {
        using var output = new StringWriter();
        using var error = new StringWriter();

        var exit = await ConnectorCommands.RunAsync(
            ["execute", "--proposal-file", "nonexistent-file.json"],
            output,
            error);

        Assert.Equal(1, exit);
        Assert.Contains("Failed to read proposal file", error.ToString(), StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task RunAsync_UnknownCommand_ReturnsTwoAndPrintsHelp()
    {
        using var output = new StringWriter();
        using var error = new StringWriter();

        var exit = await ConnectorCommands.RunAsync(["unknown"], output, error);

        Assert.Equal(2, exit);
    }

    private static string ValidProposalJson()
        => JsonSerializer.Serialize(
            new ActionProposal
            {
                ActionName = "test-action",
                Source = new ActionProposalSource
                {
                    MetaSkill = "test",
                    RunId = "run-1",
                    StepId = "step-1"
                },
                Trigger = new ActionProposalTrigger
                {
                    Condition = "always"
                },
                Target = new ActionProposalTarget
                {
                    System = "test-system",
                    Operation = "test-operation"
                },
                Execution = [new ActionCall { Call = "test-call" }],
                IdempotencyKey = "idem-1"
            },
            CoreJsonContext.Default.ActionProposal);
}

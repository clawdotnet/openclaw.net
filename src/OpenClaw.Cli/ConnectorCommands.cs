using System.Text.Json;
using OpenClaw.Core.Models;

namespace OpenClaw.Cli;

internal static class ConnectorCommands
{
    private const string DefaultBaseUrl = "http://127.0.0.1:18789";
    private const string EnvBaseUrl = "OPENCLAW_BASE_URL";
    private const string EnvAuthToken = "OPENCLAW_AUTH_TOKEN";

    public static async Task<int> RunAsync(string[] args)
        => await RunAsync(args, Console.Out, Console.Error, executeAction: null);

    internal static async Task<int> RunAsync(
        string[] args,
        TextWriter? output = null,
        TextWriter? error = null,
        Func<ConnectorActionExecuteRequest, CancellationToken, Task<IntegrationConnectorActionExecuteResponse>>? executeAction = null)
    {
        output ??= Console.Out;
        error ??= Console.Error;

        if (args.Length == 0 || args[0] is "-h" or "--help" or "help")
        {
            PrintHelp(output);
            return 0;
        }

        var parsed = CliArgs.Parse(args);
        if (parsed.ShowHelp)
        {
            PrintHelp(output);
            return 0;
        }

        var command = args[0].ToLowerInvariant();
        if (command == "execute")
            return await ExecuteCommandAsync(parsed, output, error, executeAction);

        PrintHelp(output);
        return 2;
    }

    private static async Task<int> ExecuteCommandAsync(
        CliArgs parsed,
        TextWriter output,
        TextWriter error,
        Func<ConnectorActionExecuteRequest, CancellationToken, Task<IntegrationConnectorActionExecuteResponse>>? executeAction)
    {
        var proposalFile = parsed.GetOption("--proposal-file");
        if (string.IsNullOrWhiteSpace(proposalFile))
        {
            await error.WriteLineAsync("--proposal-file is required.");
            return 2;
        }

        var decision = parsed.GetOption("--decision") ?? "proceed_execute";
        var jsonOutput = parsed.HasFlag("--json");

        string proposalJson;
        try
        {
            proposalJson = await File.ReadAllTextAsync(proposalFile);
        }
        catch (Exception ex)
        {
            await error.WriteLineAsync($"Failed to read proposal file: {ex.Message}");
            return 1;
        }

        ActionProposal proposal;
        try
        {
            proposal = JsonSerializer.Deserialize(proposalJson, CoreJsonContext.Default.ActionProposal)
                       ?? throw new InvalidOperationException("Deserialized proposal is null.");
        }
        catch (Exception ex)
        {
            await error.WriteLineAsync($"Failed to parse proposal file: {ex.Message}");
            return 1;
        }

        var request = new ConnectorActionExecuteRequest
        {
            Proposal = proposal,
            Decision = decision
        };

        var ct = CancellationToken.None;
        IntegrationConnectorActionExecuteResponse response;

        try
        {
            if (executeAction is not null)
                response = await executeAction(request, ct);
            else
            {
                using var client = CreateClient(parsed);
                response = await client.ExecuteConnectorActionAsync(request, ct);
            }
        }
        catch (Exception ex)
        {
            await error.WriteLineAsync($"Failed to execute connector action: {ex.Message}");
            return 1;
        }

        if (jsonOutput)
        {
            await output.WriteLineAsync(JsonSerializer.Serialize(response, CoreJsonContext.Default.IntegrationConnectorActionExecuteResponse));
        }
        else
        {
            await output.WriteLineAsync($"Status: {response.Status}");
            if (!string.IsNullOrWhiteSpace(response.FailureCode))
                await output.WriteLineAsync($"Failure code: {response.FailureCode}");
            if (!string.IsNullOrWhiteSpace(response.Message))
                await output.WriteLineAsync($"Message: {response.Message}");
            if (!string.IsNullOrWhiteSpace(response.Decision))
                await output.WriteLineAsync($"Decision: {response.Decision}");
        }

        var failed = string.Equals(response.Status, "failed", StringComparison.OrdinalIgnoreCase);
        return failed ? 1 : 0;
    }

    private static OpenClawHttpClient CreateClient(CliArgs parsed)
    {
        var baseUrl = parsed.GetOption("--url") ?? Environment.GetEnvironmentVariable(EnvBaseUrl) ?? DefaultBaseUrl;
        var token = parsed.GetOption("--token") ?? Environment.GetEnvironmentVariable(EnvAuthToken);
        return new OpenClawHttpClient(baseUrl, token);
    }

    private static void PrintHelp(TextWriter output)
    {
        output.WriteLine(
            """
            openclaw connector execute --proposal-file <path> [--decision <value>] [--json]

            Executes a connector action via the gateway integration API.

            Options:
              --proposal-file <path>  Path to a JSON file containing an ActionProposal (required)
              --decision <value>      Decision override (default: proceed_execute)
              --json                  Output raw JSON response
              --url <url>             Base URL (default: OPENCLAW_BASE_URL or http://127.0.0.1:18789)
              --token <token>         Auth token

            Exit codes:
              0  Success (status is not "failed")
              1  Connector action failed (status is "failed")
              2  Argument or usage error
            """);
    }
}
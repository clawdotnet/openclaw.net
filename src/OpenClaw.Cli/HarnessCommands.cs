using System.Text.Json;
using OpenClaw.Core.Models;
using OpenClaw.Testing;

namespace OpenClaw.Cli;

internal static class HarnessCommands
{
    private const string DefaultBaseUrl = "http://127.0.0.1:18789";
    private const string EnvBaseUrl = "OPENCLAW_BASE_URL";
    private const string EnvAuthToken = "OPENCLAW_AUTH_TOKEN";

    public static Task<int> RunAsync(string[] args)
        => RunAsync(args, Console.Out, Console.Error);

    internal static async Task<int> RunAsync(string[] args, TextWriter output, TextWriter error)
    {
        if (args.Length == 0 || args[0] is "-h" or "--help" or "help")
        {
            PrintHelp(output);
            return 0;
        }

        var subcommand = args[0].Trim().ToLowerInvariant();
        if (subcommand is "state")
            return await RunStateAsync(args.Skip(1).ToArray(), output, error);

        if (subcommand is not ("test" or "regression"))
        {
            error.WriteLine($"Unknown harness subcommand: {subcommand}");
            PrintHelp(output);
            return 2;
        }

        var parsed = CliArgs.Parse(args.Skip(1).ToArray());
        if (parsed.ShowHelp)
        {
            PrintHelp(output);
            return 0;
        }

        return await RunRegressionAsync(parsed, output);
    }

    internal static async Task<int> RunRegressionAliasAsync(string[] args, TextWriter output, TextWriter error)
    {
        if (args.Length == 0 || args[0] is "-h" or "--help" or "help")
        {
            PrintAliasHelp(output);
            return 0;
        }

        if (!string.Equals(args[0], "test", StringComparison.OrdinalIgnoreCase))
        {
            error.WriteLine($"Unknown regression subcommand: {args[0]}");
            PrintAliasHelp(output);
            return 2;
        }

        var forwarded = new[] { "test" }.Concat(args.Skip(1)).ToArray();
        return await RunAsync(forwarded, output, error);
    }

    private static async Task<int> RunRegressionAsync(CliArgs parsed, TextWriter output)
    {
        var options = new HarnessRegressionOptions
        {
            ConfigPath = parsed.GetOption("--config"),
            Category = parsed.GetOption("--category"),
            Offline = true,
            Strict = parsed.HasFlag("--strict"),
            ProposalId = parsed.GetOption("--proposal"),
            OutputPath = parsed.GetOption("--output")
        };

        var report = await new HarnessRegressionRunner().RunAsync(options, CancellationToken.None);
        var renderJson = parsed.HasFlag("--json");
        var rendered = renderJson
            ? HarnessRegressionReportFormatter.ToJson(report)
            : HarnessRegressionReportFormatter.ToText(report);

        var outputPath = parsed.GetOption("--output");
        if (!string.IsNullOrWhiteSpace(outputPath))
        {
            var fullPath = Path.GetFullPath(outputPath);
            var directory = Path.GetDirectoryName(fullPath);
            if (!string.IsNullOrWhiteSpace(directory))
                Directory.CreateDirectory(directory);
            await File.WriteAllTextAsync(fullPath, rendered);
        }

        output.Write(rendered);
        if (!renderJson && !string.IsNullOrWhiteSpace(outputPath))
            output.WriteLine($"Report written: {Path.GetFullPath(outputPath)}");

        return HarnessRegressionRunner.GetExitCode(report);
    }

    private static async Task<int> RunStateAsync(string[] args, TextWriter output, TextWriter error)
    {
        var parsed = CliArgs.Parse(args);
        if (parsed.ShowHelp || parsed.Positionals.Count == 0)
        {
            PrintStateHelp(output);
            return parsed.ShowHelp ? 0 : 2;
        }

        var json = parsed.HasFlag("--json");
        using var client = CreateClient(parsed);
        var ct = CancellationToken.None;

        switch (parsed.Positionals[0].ToLowerInvariant())
        {
            case "list":
            {
                var response = await client.ListSharedHarnessStateAsync(new SharedHarnessStateListQuery
                {
                    SessionId = parsed.GetOption("--session"),
                    ParentSessionId = parsed.GetOption("--parent-session"),
                    HarnessContractId = parsed.GetOption("--contract"),
                    Status = parsed.GetOption("--status"),
                    Tag = parsed.GetOption("--tag"),
                    Limit = GetIntOption(parsed, "--limit", 100)
                }, ct);
                Write(output, response, CoreJsonContext.Default.SharedHarnessStateListResponse, json, TextStateList);
                return 0;
            }
            case "show":
            {
                if (!TryGetPosition(parsed, 1, "id", error, output, out var id))
                    return 2;
                try
                {
                    var response = await client.GetSharedHarnessStateAsync(id, ct);
                    Write(output, response, CoreJsonContext.Default.SharedHarnessStateDetailResponse, json, TextStateDetail);
                    return response.State is null ? 1 : 0;
                }
                catch (HttpRequestException ex) when (IsNotFound(ex))
                {
                    Write(output, new SharedHarnessStateDetailResponse(), CoreJsonContext.Default.SharedHarnessStateDetailResponse, json, TextStateDetail);
                    return 1;
                }
                catch (HttpRequestException ex)
                {
                    WriteHttpError(error, ex);
                    return 1;
                }
            }
            case "session":
            {
                if (!TryGetPosition(parsed, 1, "session-id", error, output, out var sessionId))
                    return 2;
                try
                {
                    var response = await client.GetSharedHarnessStateForSessionAsync(sessionId, ct);
                    Write(output, response, CoreJsonContext.Default.SharedHarnessStateDetailResponse, json, TextStateDetail);
                    return response.State is null ? 1 : 0;
                }
                catch (HttpRequestException ex) when (IsNotFound(ex))
                {
                    Write(output, new SharedHarnessStateDetailResponse(), CoreJsonContext.Default.SharedHarnessStateDetailResponse, json, TextStateDetail);
                    return 1;
                }
                catch (HttpRequestException ex)
                {
                    WriteHttpError(error, ex);
                    return 1;
                }
            }
            case "conflicts":
            {
                if (!TryGetPosition(parsed, 1, "id", error, output, out var id))
                    return 2;
                try
                {
                    var response = await client.DetectSharedHarnessStateConflictsAsync(id, ct);
                    Write(output, response, CoreJsonContext.Default.SharedHarnessStateMutationResponse, json, TextStateMutation);
                    return response.Success ? 0 : 1;
                }
                catch (HttpRequestException ex) when (IsNotFound(ex))
                {
                    Write(
                        output,
                        new SharedHarnessStateMutationResponse { Success = false, Error = "Shared harness state not found." },
                        CoreJsonContext.Default.SharedHarnessStateMutationResponse,
                        json,
                        TextStateMutation);
                    return 1;
                }
                catch (HttpRequestException ex)
                {
                    WriteHttpError(error, ex);
                    return 1;
                }
            }
            default:
                error.WriteLine($"Unknown harness state subcommand: {parsed.Positionals[0]}");
                PrintStateHelp(output);
                return 2;
        }
    }

    private static OpenClawHttpClient CreateClient(CliArgs parsed)
    {
        var baseUrl = parsed.GetOption("--url") ?? Environment.GetEnvironmentVariable(EnvBaseUrl) ?? DefaultBaseUrl;
        var token = parsed.GetOption("--token") ?? Environment.GetEnvironmentVariable(EnvAuthToken);
        return new OpenClawHttpClient(baseUrl, token);
    }

    private static int GetIntOption(CliArgs parsed, string name, int fallback)
        => int.TryParse(parsed.GetOption(name), out var value) ? value : fallback;

    private static bool IsNotFound(HttpRequestException ex)
        => ex.Message.StartsWith("HTTP 404", StringComparison.Ordinal);

    private static void WriteHttpError(TextWriter error, HttpRequestException ex)
        => error.WriteLine(ex.Message);

    private static bool TryGetPosition(CliArgs parsed, int index, string name, TextWriter error, TextWriter output, out string value)
    {
        if (parsed.Positionals.Count > index && !string.IsNullOrWhiteSpace(parsed.Positionals[index]))
        {
            value = parsed.Positionals[index];
            return true;
        }

        value = "";
        error.WriteLine($"{name} is required.");
        PrintStateHelp(output);
        return false;
    }

    private static void Write<T>(TextWriter output, T value, System.Text.Json.Serialization.Metadata.JsonTypeInfo<T> typeInfo, bool json, Action<TextWriter, T> text)
    {
        if (json)
        {
            output.WriteLine(JsonSerializer.Serialize(value, typeInfo));
            return;
        }

        text(output, value);
    }

    private static void TextStateList(TextWriter output, SharedHarnessStateListResponse response)
    {
        output.WriteLine("Shared Harness State");
        output.WriteLine($"Items: {response.Items.Count}");
        foreach (var item in response.Items)
            output.WriteLine($"- {item.Id} session={item.SessionId ?? "-"} status={item.Status} participants={item.Participants.Count} actions={item.Actions.Count} conflicts={item.Conflicts.Count}");
    }

    private static void TextStateDetail(TextWriter output, SharedHarnessStateDetailResponse response)
    {
        if (response.State is null)
        {
            output.WriteLine("Shared harness state not found.");
            return;
        }

        var state = response.State;
        output.WriteLine($"Shared Harness State: {state.Id}");
        output.WriteLine($"Session: {state.SessionId ?? "-"}");
        output.WriteLine($"Status: {state.Status}");
        output.WriteLine($"Goal: {state.Goal}");
        output.WriteLine($"Participants: {state.Participants.Count}");
        foreach (var participant in state.Participants)
            output.WriteLine($"- {participant.Id} role={participant.Role} session={participant.SessionId ?? "-"} status={participant.Status}");
        output.WriteLine($"Actions: {state.Actions.Count}");
        foreach (var action in state.Actions)
            output.WriteLine($"- {action.Id} participant={action.ParticipantId ?? "-"} status={action.Status} reads={action.ReadSet.Count} writes={action.WriteSet.Count}");
        output.WriteLine($"Conflicts: {state.Conflicts.Count}");
        foreach (var conflict in state.Conflicts)
            output.WriteLine($"- {conflict.Type} {conflict.Severity}: {conflict.Summary}");
    }

    private static void TextStateMutation(TextWriter output, SharedHarnessStateMutationResponse response)
    {
        if (!response.Success)
        {
            output.WriteLine($"Error: {response.Error}");
            return;
        }

        output.WriteLine(response.Message);
        if (response.State is not null)
            TextStateDetail(output, new SharedHarnessStateDetailResponse { State = response.State });
    }

    private static void PrintHelp(TextWriter output)
    {
        output.WriteLine("""
            openclaw harness

            Usage:
              openclaw harness test [--config <path>] [--category <name>] [--json] [--offline] [--strict] [--proposal <id>] [--output <path>]
              openclaw harness regression [--config <path>] [--category <name>] [--json] [--offline] [--strict] [--proposal <id>] [--output <path>]
              openclaw harness state <list|show|session|conflicts> [options]

            Categories:
              onboarding, security, approvals, memory, providers, tools, mcp,
              openai_compat, sessions, harness, deployment, docs

            Notes:
              - Runs offline by default and does not require provider keys.
              - Provider/network checks are structural only unless a future online mode is added.
              - Strict mode treats required warnings and skips as failures.
            """);
    }

    private static void PrintStateHelp(TextWriter output)
    {
        output.WriteLine("""
            openclaw harness state

            Usage:
              openclaw harness state list [--session <id>] [--parent-session <id>] [--contract <id>] [--status <name>] [--tag <tag>] [--limit <n>] [--json]
              openclaw harness state show <id> [--json]
              openclaw harness state session <session-id> [--json]
              openclaw harness state conflicts <id> [--json]

            Common options:
              --url <url>
              --token <token>
            """);
    }

    private static void PrintAliasHelp(TextWriter output)
    {
        output.WriteLine("""
            openclaw regression

            Usage:
              openclaw regression test [--config <path>] [--category <name>] [--json] [--offline] [--strict] [--proposal <id>] [--output <path>]
            """);
    }
}

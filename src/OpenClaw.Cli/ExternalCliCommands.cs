using System.Text.Json;
using System.Text;
using OpenClaw.Core.Models;

namespace OpenClaw.Cli;

internal static class ExternalCliCommands
{
    private const string DefaultBaseUrl = "http://127.0.0.1:18789";
    private const string EnvBaseUrl = "OPENCLAW_BASE_URL";
    private const string EnvAuthToken = "OPENCLAW_AUTH_TOKEN";

    public static async Task<int> RunAsync(string[] args)
    {
        if (args.Length == 0 || args[0] is "-h" or "--help" or "help")
        {
            PrintHelp();
            return 0;
        }

        var parsed = CliArgs.Parse(args);
        if (parsed.ShowHelp || parsed.Positionals.Count == 0)
        {
            PrintHelp();
            return 0;
        }

        using var client = CreateClient(parsed);
        var command = parsed.Positionals[0];
        var json = parsed.HasFlag("--json");
        var ct = CancellationToken.None;

        switch (command)
        {
            case "list":
            {
                var response = await client.ListExternalCliConnectorsAsync(ct);
                Write(response, CoreJsonContext.Default.ExternalCliConnectorListResponse, json, TextConnectors);
                return 0;
            }

            case "status":
            {
                var connector = RequiredPosition(parsed, 1, "connector");
                var response = await client.GetExternalCliConnectorStatusAsync(connector, ct);
                Write(response, CoreJsonContext.Default.ExternalCliConnectorStatus, json, TextStatus);
                return 0;
            }

            case "commands":
            {
                var connector = RequiredPosition(parsed, 1, "connector");
                var response = await client.ListExternalCliCommandsAsync(connector, ct);
                Write(response, CoreJsonContext.Default.ExternalCliCommandListResponse, json, TextCommands);
                return 0;
            }

            case "preview":
            {
                var request = BuildPreviewRequest(parsed);
                var response = await client.PreviewExternalCliAsync(request, ct);
                Write(response, CoreJsonContext.Default.ExternalCliPreviewResponse, json, TextPreview);
                return 0;
            }

            case "execute":
            {
                var requestedPreview = BuildPreviewRequest(parsed);
                var previewRequest = new ExternalCliPreviewRequest
                {
                    Connector = requestedPreview.Connector,
                    Command = requestedPreview.Command,
                    Parameters = requestedPreview.Parameters,
                    ExecuteDryRun = false
                };
                var preview = await client.PreviewExternalCliAsync(previewRequest, ct);
                if (preview.Preview.RequiresApproval && !parsed.HasFlag("--yes"))
                {
                    Console.Error.WriteLine("External CLI command requires approval. Re-run with --yes after reviewing preview:");
                    TextPreview(preview);
                    return 3;
                }

                var executeRequest = new ExternalCliExecuteRequest
                {
                    Connector = previewRequest.Connector,
                    Command = previewRequest.Command,
                    Parameters = previewRequest.Parameters,
                    ApprovedFingerprint = preview.Preview.RequiresApproval ? preview.Preview.Fingerprint : null,
                    ApprovalReason = parsed.GetOption("--reason")
                };
                var response = await client.ExecuteExternalCliAsync(executeRequest, ct);
                Write(response, CoreJsonContext.Default.ExternalCliExecutionResult, json, TextExecution);
                return response.Success ? 0 : 1;
            }

            default:
                PrintHelp();
                return 2;
        }
    }

    private static ExternalCliPreviewRequest BuildPreviewRequest(CliArgs parsed)
        => new()
        {
            Connector = RequiredPosition(parsed, 1, "connector"),
            Command = RequiredPosition(parsed, 2, "command"),
            Parameters = ParseParameters(parsed),
            ExecuteDryRun = parsed.HasFlag("--dry-run")
        };

    private static Dictionary<string, JsonElement> ParseParameters(CliArgs parsed)
    {
        var result = new Dictionary<string, JsonElement>(StringComparer.OrdinalIgnoreCase);
        if (!parsed.Options.TryGetValue("--param", out var values))
            return result;

        foreach (var item in values)
        {
            var index = item.IndexOf('=', StringComparison.Ordinal);
            if (index <= 0)
                throw new ArgumentException("--param must be in key=value form.");

            var key = item[..index].Trim();
            var value = item[(index + 1)..];
            if (string.IsNullOrWhiteSpace(key))
                throw new ArgumentException("--param key cannot be empty.");

            result[key] = ParseParameterValue(value);
        }

        return result;
    }

    private static JsonElement ParseParameterValue(string value)
    {
        var trimmed = value.Trim();
        var looksJson = trimmed.StartsWith('"') ||
                        trimmed.StartsWith('{') ||
                        trimmed.StartsWith('[') ||
                        trimmed.Equals("true", StringComparison.OrdinalIgnoreCase) ||
                        trimmed.Equals("false", StringComparison.OrdinalIgnoreCase) ||
                        decimal.TryParse(trimmed, out _);
        try
        {
            using var parsed = JsonDocument.Parse(looksJson ? trimmed : QuoteJsonString(value));
            return parsed.RootElement.Clone();
        }
        catch (JsonException)
        {
            using var parsed = JsonDocument.Parse(QuoteJsonString(value));
            return parsed.RootElement.Clone();
        }
    }

    private static string QuoteJsonString(string value)
    {
        using var stream = new MemoryStream();
        using (var writer = new Utf8JsonWriter(stream))
            writer.WriteStringValue(value);
        return Encoding.UTF8.GetString(stream.ToArray());
    }

    private static string RequiredPosition(CliArgs parsed, int index, string name)
        => parsed.Positionals.Count > index && !string.IsNullOrWhiteSpace(parsed.Positionals[index])
            ? parsed.Positionals[index]
            : throw new ArgumentException($"{name} is required.");

    private static OpenClawHttpClient CreateClient(CliArgs parsed)
    {
        var baseUrl = parsed.GetOption("--url") ?? Environment.GetEnvironmentVariable(EnvBaseUrl) ?? DefaultBaseUrl;
        var token = parsed.GetOption("--token") ?? Environment.GetEnvironmentVariable(EnvAuthToken);
        return new OpenClawHttpClient(baseUrl, token);
    }

    private static void Write<T>(T value, System.Text.Json.Serialization.Metadata.JsonTypeInfo<T> typeInfo, bool json, Action<T> text)
    {
        if (json)
            Console.WriteLine(JsonSerializer.Serialize(value, typeInfo));
        else
            text(value);
    }

    private static void TextConnectors(ExternalCliConnectorListResponse response)
    {
        foreach (var item in response.Items)
            Console.WriteLine($"{item.Name}\t{(item.Enabled ? "enabled" : "disabled")}\t{item.Executable}\tcommands={item.CommandCount}");
    }

    private static void TextStatus(ExternalCliConnectorStatus status)
    {
        Console.WriteLine($"{status.Connector}\t{(status.Enabled ? "enabled" : "disabled")}\texecutableFound={status.ExecutableFound}\tauth={status.AuthenticationStatus}");
        if (!string.IsNullOrWhiteSpace(status.ResolvedExecutablePath))
            Console.WriteLine($"path: {status.ResolvedExecutablePath}");
        if (!string.IsNullOrWhiteSpace(status.Version))
            Console.WriteLine($"version: {status.Version}");
        foreach (var warning in status.Warnings)
            Console.WriteLine($"warning: {warning}");
    }

    private static void TextCommands(ExternalCliCommandListResponse response)
    {
        foreach (var item in response.Items)
            Console.WriteLine($"{item.Name}\t{item.RiskLevel}\treadOnly={item.ReadOnly}\tapproval={item.RequiresApproval}\t{item.Description}");
    }

    private static void TextPreview(ExternalCliPreviewResponse response)
    {
        var preview = response.Preview;
        Console.WriteLine($"{preview.Connector}/{preview.Command}");
        Console.WriteLine($"command: {preview.RedactedCommandLine}");
        Console.WriteLine($"risk: {preview.RiskLevel} readOnly={preview.ReadOnly} approval={preview.RequiresApproval}");
        Console.WriteLine($"fingerprint: {preview.Fingerprint}");
        if (response.DryRunResult is not null)
            TextExecution(response.DryRunResult);
    }

    private static void TextExecution(ExternalCliExecutionResult result)
    {
        Console.WriteLine($"{result.Preview.Connector}/{result.Preview.Command}\texit={result.ExitCode}\ttimedOut={result.TimedOut}\tdurationMs={result.DurationMs:F0}");
        if (!string.IsNullOrWhiteSpace(result.Stdout))
            Console.WriteLine(result.Stdout.TrimEnd());
        if (!string.IsNullOrWhiteSpace(result.Stderr))
            Console.Error.WriteLine(result.Stderr.TrimEnd());
        if (result.StdoutTruncated || result.StderrTruncated)
            Console.WriteLine("[output truncated]");
        if (!string.IsNullOrWhiteSpace(result.ParseError))
            Console.WriteLine($"parseError: {result.ParseError}");
    }

    private static void PrintHelp()
    {
        Console.WriteLine(
            """
            openclaw external

            Usage:
              openclaw external list [--json]
              openclaw external status <connector> [--json]
              openclaw external commands <connector> [--json]
              openclaw external preview <connector> <command> [--param key=value]... [--dry-run] [--json]
              openclaw external execute <connector> <command> [--param key=value]... [--yes] [--json]

            Notes:
              - Commands talk to the gateway admin API.
              - Mutating or high-risk commands require --yes after preview review.
              - External CLI connectors are disabled unless OpenClaw:ExternalCli:Enabled=true.
            """);
    }
}

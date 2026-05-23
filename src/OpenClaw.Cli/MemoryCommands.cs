using System.Text.Json;
using System.Text.Json.Serialization.Metadata;
using OpenClaw.Core.Models;

namespace OpenClaw.Cli;

internal static class MemoryCommands
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
        if (parsed.ShowHelp || parsed.Positionals.Count < 2 ||
            !string.Equals(parsed.Positionals[0], "fractal", StringComparison.OrdinalIgnoreCase))
        {
            PrintHelp();
            return parsed.ShowHelp ? 0 : 2;
        }

        var json = parsed.HasFlag("--json");
        using var client = CreateClient(parsed);
        var ct = CancellationToken.None;

        switch (parsed.Positionals[1].ToLowerInvariant())
        {
            case "status":
            {
                var response = await client.GetFractalMemoryStatusAsync(ct);
                Write(response, CoreJsonContext.Default.StructuredMemoryStatusResponse, json, TextStatus);
                return response.Enabled && !response.Available ? 1 : 0;
            }
            case "search":
            {
                if (!TryGetPosition(parsed, 2, "query", out var query))
                    return 2;
                var response = await client.SearchFractalMemoryAsync(
                    query,
                    GetIntOption(parsed, "--limit", 10),
                    parsed.GetOption("--scope"),
                    ct);
                Write(response, CoreJsonContext.Default.StructuredMemorySearchResult, json, TextSearch);
                return response.Success ? 0 : 1;
            }
            case "open":
            {
                if (!TryGetPosition(parsed, 2, "path", out var path))
                    return 2;
                var response = await client.OpenFractalMemoryAsync(
                    path,
                    GetNullableIntOption(parsed, "--depth"),
                    parsed.GetOption("--view"),
                    ct);
                Write(response, CoreJsonContext.Default.StructuredMemoryOpenResult, json, TextOpen);
                return response.Success ? 0 : 1;
            }
            case "export":
            {
                if (!TryGetPosition(parsed, 2, "path", out var path))
                    return 2;
                var response = await client.ExportFractalMemoryAsync(path, parsed.GetOption("--mode"), ct);
                Write(response, CoreJsonContext.Default.StructuredMemoryExportResult, json, TextExport);
                return response.Success ? 0 : 1;
            }
            case "recent":
            {
                var response = await client.GetRecentFractalMemoryAsync(
                    GetIntOption(parsed, "--days", 30),
                    GetIntOption(parsed, "--limit", 10),
                    parsed.GetOption("--scope"),
                    ct);
                Write(response, CoreJsonContext.Default.StructuredMemoryRecentResult, json, TextRecent);
                return response.Success ? 0 : 1;
            }
            case "validate":
            {
                var response = await client.ValidateFractalMemoryAsync(ct);
                Write(response, CoreJsonContext.Default.StructuredMemoryValidationResult, json, TextValidation);
                return response.Success && !response.HasErrors ? 0 : 1;
            }
            case "index" when parsed.Positionals.Count > 2 && string.Equals(parsed.Positionals[2], "refresh", StringComparison.OrdinalIgnoreCase):
            {
                var response = await client.RefreshFractalMemoryIndexAsync(ct);
                Write(response, CoreJsonContext.Default.StructuredMemoryValidationResult, json, TextValidation);
                return response.Success && !response.HasErrors ? 0 : 1;
            }
            case "handoff" when parsed.Positionals.Count > 2 && string.Equals(parsed.Positionals[2], "create", StringComparison.OrdinalIgnoreCase):
            {
                if (!TryGetPosition(parsed, 3, "path", out var path))
                    return 2;
                var response = await client.CreateFractalMemoryHandoffAsync(path, ct);
                Write(response, CoreJsonContext.Default.StructuredMemoryHandoffResult, json, TextHandoff);
                return response.Success ? 0 : 1;
            }
            default:
                PrintHelp();
                return 2;
        }
    }

    private static OpenClawHttpClient CreateClient(CliArgs parsed)
    {
        var baseUrl = parsed.GetOption("--url") ?? Environment.GetEnvironmentVariable(EnvBaseUrl) ?? DefaultBaseUrl;
        var token = parsed.GetOption("--token") ?? Environment.GetEnvironmentVariable(EnvAuthToken);
        return new OpenClawHttpClient(baseUrl, token);
    }

    private static bool TryGetPosition(CliArgs parsed, int index, string name, out string value)
    {
        if (parsed.Positionals.Count > index && !string.IsNullOrWhiteSpace(parsed.Positionals[index]))
        {
            value = parsed.Positionals[index];
            return true;
        }

        value = "";
        Console.Error.WriteLine($"{name} is required.");
        PrintHelp();
        return false;
    }

    private static int GetIntOption(CliArgs parsed, string name, int fallback)
        => int.TryParse(parsed.GetOption(name), out var value) ? value : fallback;

    private static int? GetNullableIntOption(CliArgs parsed, string name)
        => int.TryParse(parsed.GetOption(name), out var value) ? value : null;

    private static void Write<T>(T value, JsonTypeInfo<T> typeInfo, bool json, Action<T> text)
    {
        if (json)
        {
            Console.WriteLine(JsonSerializer.Serialize(value, typeInfo));
            return;
        }

        text(value);
    }

    private static void TextStatus(StructuredMemoryStatusResponse response)
    {
        Console.WriteLine("Fractal Memory");
        Console.WriteLine($"Status: {response.Status}");
        Console.WriteLine($"Enabled: {response.Enabled}");
        Console.WriteLine($"Mode: {response.Mode}");
        Console.WriteLine($"Repository: {response.ResolvedRepositoryRoot}");
        Console.WriteLine($"MCP: {response.McpCommand}");
        Console.WriteLine($"Auto context: {response.AutoContextMode}");
        Console.WriteLine($"Writes: {(response.WriteToolsAvailable ? "enabled" : "disabled")}");
        foreach (var warning in response.Warnings)
            Console.WriteLine($"Warning: {warning}");
        if (!string.IsNullOrWhiteSpace(response.Error))
            Console.WriteLine($"Error: {response.Error}");
    }

    private static void TextSearch(StructuredMemorySearchResult response)
    {
        if (!response.Success)
        {
            Console.WriteLine($"Error: {response.Error}");
            return;
        }

        Console.WriteLine($"Matches: {response.Items.Count}");
        foreach (var item in response.Items)
            Console.WriteLine($"- {item.Path} {item.Title}".TrimEnd());
    }

    private static void TextRecent(StructuredMemoryRecentResult response)
    {
        if (!response.Success)
        {
            Console.WriteLine($"Error: {response.Error}");
            return;
        }

        Console.WriteLine($"Recent nodes: {response.Items.Count}");
        foreach (var item in response.Items)
            Console.WriteLine($"- {item.Path} {item.Title}".TrimEnd());
    }

    private static void TextOpen(StructuredMemoryOpenResult response)
    {
        if (!response.Success)
        {
            Console.WriteLine($"Error: {response.Error}");
            return;
        }

        Console.WriteLine($"{response.Path} {response.Title}".TrimEnd());
        if (!string.IsNullOrWhiteSpace(response.Content))
            Console.WriteLine(response.Content);
    }

    private static void TextExport(StructuredMemoryExportResult response)
    {
        if (!response.Success)
        {
            Console.WriteLine($"Error: {response.Error}");
            return;
        }

        Console.WriteLine($"Source: {response.Path}");
        Console.WriteLine($"Mode: {response.Mode}");
        if (!string.IsNullOrWhiteSpace(response.Content))
            Console.WriteLine(response.Content);
    }

    private static void TextHandoff(StructuredMemoryHandoffResult response)
    {
        if (!response.Success)
        {
            Console.WriteLine($"Error: {response.Error}");
            return;
        }

        Console.WriteLine($"Handoff: {response.HandoffFilePath ?? response.Path}");
        if (!string.IsNullOrWhiteSpace(response.Content))
            Console.WriteLine(response.Content);
    }

    private static void TextValidation(StructuredMemoryValidationResult response)
    {
        if (!response.Success)
        {
            Console.WriteLine($"Error: {response.Error}");
            return;
        }

        Console.WriteLine(response.Summary ?? (response.HasErrors ? "Validation reported errors." : "Validation passed."));
        foreach (var issue in response.Issues)
            Console.WriteLine($"- {issue.Severity}: {issue.Path} {issue.Message}".TrimEnd());
    }

    private static void PrintHelp()
    {
        Console.WriteLine(
            """
            openclaw memory fractal <command>

            Commands:
              status
              search <query> [--limit <n>] [--scope <path>] [--json]
              open <path> [--depth <0-3>] [--view index|state|timeline|decisions|children] [--json]
              export <path> [--mode compact|standard|verbose] [--json]
              recent [--days <n>] [--limit <n>] [--scope <path>] [--json]
              handoff create <path> [--json]
              validate [--json]
              index refresh [--json]

            Common options:
              --url <url>
              --token <token>
            """);
    }
}

using System.Globalization;
using OpenClaw.Core.Models;

namespace OpenClaw.Cli;

internal static class Program
{
    private const string DefaultBaseUrl = "http://127.0.0.1:18789";
    private const string EnvBaseUrl = "OPENCLAW_BASE_URL";
    private const string EnvAuthToken = "OPENCLAW_AUTH_TOKEN";

    public static async Task<int> Main(string[] args)
    {
        if (args.Length == 0 || args[0] is "-h" or "--help" or "help")
        {
            PrintHelp();
            return 0;
        }

        var command = args[0];
        var rest = args.Skip(1).ToArray();

        try
        {
            return command switch
            {
                "run" => await RunAsync(rest),
                "chat" => await ChatAsync(rest),
                "version" or "--version" or "-v" => PrintVersion(),
                _ => UnknownCommand(command)
            };
        }
        catch (OperationCanceledException)
        {
            return 130;
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine(ex.Message);
            return 1;
        }
    }

    private static int PrintVersion()
    {
        var version = typeof(Program).Assembly.GetName().Version?.ToString() ?? "0.0.0";
        Console.WriteLine($"openclaw {version}");
        return 0;
    }

    private static int UnknownCommand(string command)
    {
        Console.Error.WriteLine($"Unknown command: {command}");
        Console.Error.WriteLine("Run: openclaw --help");
        return 2;
    }

    private static void PrintHelp()
    {
        Console.WriteLine(
            """
            openclaw — OpenClaw.NET CLI

            Usage:
              openclaw run [options] <prompt>
              openclaw chat [options]

            Common options:
              --url <url>        Base URL (default: OPENCLAW_BASE_URL or http://127.0.0.1:18789)
              --token <token>    Auth token (default: OPENCLAW_AUTH_TOKEN)
              --model <model>    Model override (optional)
              --system <text>    System prompt (optional)

            run options:
              --file <path>      Attach file contents (repeatable)
              --no-stream        Disable SSE streaming
              --temperature <n>  Temperature (optional)
              --max-tokens <n>   Max tokens (optional)

            chat commands:
              /help, /exit, /reset
              /system <text>
              /model <model>

            Examples:
              openclaw run "summarize this README" --file ./README.md
              cat error.log | openclaw run "what went wrong?"
              openclaw chat --system "Be concise."
            """);
    }

    private static async Task<int> RunAsync(string[] args)
    {
        var parsed = CliArgs.Parse(args);
        if (parsed.ShowHelp)
        {
            PrintHelp();
            return 0;
        }

        var baseUrl = parsed.GetOption("--url") ?? Environment.GetEnvironmentVariable(EnvBaseUrl) ?? DefaultBaseUrl;
        var token = parsed.GetOption("--token") ?? Environment.GetEnvironmentVariable(EnvAuthToken);
        var model = parsed.GetOption("--model");
        var system = parsed.GetOption("--system");

        var stream = !parsed.HasFlag("--no-stream");
        var temperature = ParseFloat(parsed.GetOption("--temperature"));
        var maxTokens = ParseInt(parsed.GetOption("--max-tokens"));

        var prompt = parsed.Positionals.Count > 0 ? string.Join(' ', parsed.Positionals) : null;
        var stdin = await ReadAllStdinAsync();

        if (string.IsNullOrWhiteSpace(prompt))
            prompt = stdin;
        else if (!string.IsNullOrWhiteSpace(stdin))
            prompt = $"{prompt}\n\n--- stdin ---\n{stdin}";

        if (string.IsNullOrWhiteSpace(prompt))
        {
            Console.Error.WriteLine("Missing prompt. Provide <prompt> or pipe stdin.");
            return 2;
        }

        var userContent = BuildUserContent(prompt, parsed.Files);
        var messages = BuildMessages(system, userContent, priorConversation: null);

        using var client = new OpenClawHttpClient(baseUrl, token);
        var request = new OpenAiChatCompletionRequest
        {
            Model = model,
            Stream = stream,
            Temperature = temperature,
            MaxTokens = maxTokens,
            Messages = messages
        };

        if (stream)
        {
            var full = await client.StreamChatCompletionAsync(request, s => Console.Write(s), CancellationToken.None);
            if (!string.IsNullOrEmpty(full) && !full.EndsWith('\n'))
                Console.WriteLine();
            return 0;
        }

        var response = await client.ChatCompletionAsync(request, CancellationToken.None);
        var text = response.Choices[0].Message.Content;
        Console.WriteLine(text);
        return 0;
    }

    private static async Task<int> ChatAsync(string[] args)
    {
        var parsed = CliArgs.Parse(args);
        if (parsed.ShowHelp)
        {
            PrintHelp();
            return 0;
        }

        var baseUrl = parsed.GetOption("--url") ?? Environment.GetEnvironmentVariable(EnvBaseUrl) ?? DefaultBaseUrl;
        var token = parsed.GetOption("--token") ?? Environment.GetEnvironmentVariable(EnvAuthToken);
        var model = parsed.GetOption("--model");
        var system = parsed.GetOption("--system");

        var temperature = ParseFloat(parsed.GetOption("--temperature"));
        var maxTokens = ParseInt(parsed.GetOption("--max-tokens"));

        using var client = new OpenClawHttpClient(baseUrl, token);

        var conversation = new List<OpenAiMessage>();
        if (!string.IsNullOrWhiteSpace(system))
            conversation.Add(new OpenAiMessage { Role = "system", Content = system });

        Console.Error.WriteLine("openclaw chat — type /help for commands");

        while (true)
        {
            Console.Write("> ");
            var line = Console.ReadLine();
            if (line is null)
                break;

            line = line.Trim();
            if (line.Length == 0)
                continue;

            if (line.StartsWith('/'))
            {
                if (HandleSlashCommand(line, conversation, ref system, ref model))
                    break;
                continue;
            }

            conversation.Add(new OpenAiMessage { Role = "user", Content = line });
            var messages = BuildMessages(system, line, conversation);

            var request = new OpenAiChatCompletionRequest
            {
                Model = model,
                Stream = true,
                Temperature = temperature,
                MaxTokens = maxTokens,
                Messages = messages
            };

            var assistantText = await client.StreamChatCompletionAsync(request, s => Console.Write(s), CancellationToken.None);
            if (!assistantText.EndsWith('\n'))
                Console.WriteLine();
            conversation.Add(new OpenAiMessage { Role = "assistant", Content = assistantText });
        }

        return 0;
    }

    private static bool HandleSlashCommand(
        string command,
        List<OpenAiMessage> conversation,
        ref string? system,
        ref string? model)
    {
        if (command is "/exit" or "/quit")
            return true;

        if (command is "/help")
        {
            Console.Error.WriteLine(
                """
                Commands:
                  /help
                  /exit
                  /reset
                  /system <text>
                  /model <model>
                """);
            return false;
        }

        if (command is "/reset")
        {
            conversation.Clear();
            if (!string.IsNullOrWhiteSpace(system))
                conversation.Add(new OpenAiMessage { Role = "system", Content = system });
            Console.Error.WriteLine("Reset conversation.");
            return false;
        }

        if (command.StartsWith("/system ", StringComparison.Ordinal))
        {
            system = command["/system ".Length..].Trim();
            conversation.RemoveAll(m => string.Equals(m.Role, "system", StringComparison.OrdinalIgnoreCase));
            if (!string.IsNullOrWhiteSpace(system))
                conversation.Insert(0, new OpenAiMessage { Role = "system", Content = system });
            Console.Error.WriteLine("Set system prompt.");
            return false;
        }

        if (command.StartsWith("/model ", StringComparison.Ordinal))
        {
            model = command["/model ".Length..].Trim();
            if (model.Length == 0)
                model = null;
            Console.Error.WriteLine(model is null ? "Cleared model override." : $"Set model: {model}");
            return false;
        }

        Console.Error.WriteLine($"Unknown command: {command}");
        return false;
    }

    private static List<OpenAiMessage> BuildMessages(string? system, string userContent, List<OpenAiMessage>? priorConversation)
    {
        if (priorConversation is null || priorConversation.Count == 0)
        {
            var initial = new List<OpenAiMessage>();
            if (!string.IsNullOrWhiteSpace(system))
                initial.Add(new OpenAiMessage { Role = "system", Content = system });
            initial.Add(new OpenAiMessage { Role = "user", Content = userContent });
            return initial;
        }

        // Re-send the full conversation so the server can reconstruct context.
        // (The gateway creates an ephemeral session per HTTP request.)
        var messages = new List<OpenAiMessage>(priorConversation.Count);
        messages.AddRange(priorConversation);
        return messages;
    }

    private static string BuildUserContent(string prompt, IReadOnlyList<string> files)
    {
        if (files.Count == 0)
            return prompt;

        var parts = new List<string> { prompt };
        foreach (var path in files)
        {
            var fullPath = Path.GetFullPath(path);
            var content = File.ReadAllText(fullPath);
            parts.Add(
                $"""

                --- file: {fullPath} ---
                ```
                {content}
                ```
                """);
        }
        return string.Join('\n', parts);
    }

    private static async Task<string?> ReadAllStdinAsync()
    {
        if (!Console.IsInputRedirected)
            return null;

        using var reader = new StreamReader(Console.OpenStandardInput());
        var text = await reader.ReadToEndAsync();
        return string.IsNullOrWhiteSpace(text) ? null : text.TrimEnd();
    }

    private static float? ParseFloat(string? raw)
    {
        if (string.IsNullOrWhiteSpace(raw))
            return null;
        if (!float.TryParse(raw, NumberStyles.Float, CultureInfo.InvariantCulture, out var value))
            throw new ArgumentException($"Invalid float: {raw}");
        return value;
    }

    private static int? ParseInt(string? raw)
    {
        if (string.IsNullOrWhiteSpace(raw))
            return null;
        if (!int.TryParse(raw, NumberStyles.Integer, CultureInfo.InvariantCulture, out var value))
            throw new ArgumentException($"Invalid int: {raw}");
        return value;
    }
}


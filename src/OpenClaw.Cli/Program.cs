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
                "heartbeat" => await HeartbeatAsync(rest),
                "admin" => await AdminAsync(rest),
                "clawhub" => await ClawHubCommand.RunAsync(rest),
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
              openclaw heartbeat <wizard|preview|status> [options]
              openclaw admin <posture|incident export|approvals simulate> [options]
              openclaw clawhub [wrapper options] [--] <clawhub args...>

            Common options:
              --url <url>        Base URL (default: OPENCLAW_BASE_URL or http://127.0.0.1:18789)
              --token <token>    Auth token (deprecated: prefer OPENCLAW_AUTH_TOKEN)
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
              OPENCLAW_AUTH_TOKEN=... openclaw run "summarize this README" --file ./README.md
              cat error.log | openclaw run "what went wrong?"
              openclaw chat --system "Be concise."
              openclaw heartbeat status
              openclaw heartbeat wizard
              openclaw admin posture
              openclaw admin approvals simulate --tool shell --args "{\"command\":\"pwd\"}"
              openclaw admin incident export

            ClawHub wrapper:
              # Forward --help to ClawHub itself:
              openclaw clawhub -- --help
              # Install skills into $OPENCLAW_WORKSPACE/skills (default):
              openclaw clawhub install <skill-slug>
              # Install into ~/.openclaw/skills:
              openclaw clawhub --managed install <skill-slug>
            """);
    }

    private static void PrintHeartbeatHelp()
    {
        Console.WriteLine(
            """
            openclaw heartbeat

            Usage:
              openclaw heartbeat status [--url <url>] [--token <token>]
              openclaw heartbeat preview [--url <url>] [--token <token>]
              openclaw heartbeat wizard [--url <url>] [--token <token>]

            Notes:
              - The heartbeat commands talk to the gateway admin API.
              - Prefer OPENCLAW_BASE_URL / OPENCLAW_AUTH_TOKEN over command-line tokens.
            """);
    }

    private static void PrintAdminHelp()
    {
        Console.WriteLine(
            """
            openclaw admin

            Usage:
              openclaw admin posture [--url <url>] [--token <token>]
              openclaw admin incident export [--approval-limit <n>] [--event-limit <n>] [--url <url>] [--token <token>]
              openclaw admin approvals simulate --tool <tool> [--args <json>] [--autonomy <mode>] [--require-approval <true|false>] [--approval-tool <tool>]... [--url <url>] [--token <token>]
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
        var token = ResolveAuthToken(parsed, Console.Error);
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
        var token = ResolveAuthToken(parsed, Console.Error);
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

    private static async Task<int> HeartbeatAsync(string[] args)
    {
        if (args.Length == 0 || args[0] is "-h" or "--help" or "help")
        {
            PrintHeartbeatHelp();
            return 0;
        }

        var subcommand = args[0].Trim().ToLowerInvariant();
        var parsed = CliArgs.Parse(args.Skip(1).ToArray());
        if (parsed.ShowHelp)
        {
            PrintHeartbeatHelp();
            return 0;
        }

        var baseUrl = parsed.GetOption("--url") ?? Environment.GetEnvironmentVariable(EnvBaseUrl) ?? DefaultBaseUrl;
        var token = ResolveAuthToken(parsed, Console.Error);

        using var client = new OpenClawHttpClient(baseUrl, token);
        return subcommand switch
        {
            "status" => await HeartbeatStatusAsync(client),
            "preview" => await HeartbeatPreviewAsync(client),
            "wizard" => await HeartbeatWizardAsync(client),
            _ => throw new ArgumentException($"Unknown heartbeat command: {subcommand}")
        };
    }

    private static async Task<int> AdminAsync(string[] args)
    {
        if (args.Length == 0 || args[0] is "-h" or "--help" or "help")
        {
            PrintAdminHelp();
            return 0;
        }

        var group = args[0].Trim().ToLowerInvariant();
        if (group == "posture")
        {
            var parsed = CliArgs.Parse(args.Skip(1).ToArray());
            var baseUrl = parsed.GetOption("--url") ?? Environment.GetEnvironmentVariable(EnvBaseUrl) ?? DefaultBaseUrl;
            var token = ResolveAuthToken(parsed, Console.Error);
            using var client = new OpenClawHttpClient(baseUrl, token);
            var posture = await client.GetSecurityPostureAsync(CancellationToken.None);
            WritePosture(posture);
            return 0;
        }

        if (group == "incident" && args.Length > 1 && string.Equals(args[1], "export", StringComparison.OrdinalIgnoreCase))
        {
            var parsed = CliArgs.Parse(args.Skip(2).ToArray());
            var baseUrl = parsed.GetOption("--url") ?? Environment.GetEnvironmentVariable(EnvBaseUrl) ?? DefaultBaseUrl;
            var token = ResolveAuthToken(parsed, Console.Error);
            var approvalLimit = ParseInt(parsed.GetOption("--approval-limit")) ?? 100;
            var eventLimit = ParseInt(parsed.GetOption("--event-limit")) ?? 200;
            using var client = new OpenClawHttpClient(baseUrl, token);
            var bundle = await client.ExportIncidentBundleAsync(approvalLimit, eventLimit, CancellationToken.None);
            Console.WriteLine(System.Text.Json.JsonSerializer.Serialize(bundle, CoreJsonContext.Default.IncidentBundleResponse));
            return 0;
        }

        if (group == "approvals" && args.Length > 1 && string.Equals(args[1], "simulate", StringComparison.OrdinalIgnoreCase))
        {
            var parsed = CliArgs.Parse(args.Skip(2).ToArray());
            var baseUrl = parsed.GetOption("--url") ?? Environment.GetEnvironmentVariable(EnvBaseUrl) ?? DefaultBaseUrl;
            var token = ResolveAuthToken(parsed, Console.Error);
            var tool = parsed.GetOption("--tool");
            if (string.IsNullOrWhiteSpace(tool))
            {
                Console.Error.WriteLine("--tool is required.");
                return 2;
            }

            using var client = new OpenClawHttpClient(baseUrl, token);
            var response = await client.SimulateApprovalAsync(new ApprovalSimulationRequest
            {
                ToolName = tool,
                ArgumentsJson = parsed.GetOption("--args"),
                AutonomyMode = parsed.GetOption("--autonomy"),
                RequireToolApproval = ParseBool(parsed.GetOption("--require-approval")),
                ApprovalRequiredTools = parsed.Options.TryGetValue("--approval-tool", out var tools)
                    ? tools.ToArray()
                    : null
            }, CancellationToken.None);

            Console.WriteLine($"{response.Decision}: {response.Reason}");
            Console.WriteLine($"tool={response.ToolName} autonomy={response.AutonomyMode} require_approval={response.RequireToolApproval.ToString().ToLowerInvariant()}");
            if (response.ApprovalRequiredTools.Count > 0)
                Console.WriteLine($"approval_tools={string.Join(",", response.ApprovalRequiredTools)}");
            return 0;
        }

        PrintAdminHelp();
        return 2;
    }

    private static async Task<int> HeartbeatStatusAsync(OpenClawHttpClient client)
    {
        var status = await client.GetHeartbeatStatusAsync(CancellationToken.None);
        WriteHeartbeatStatus(status);
        return 0;
    }

    private static async Task<int> HeartbeatPreviewAsync(OpenClawHttpClient client)
    {
        var preview = await client.GetHeartbeatAsync(CancellationToken.None);
        WriteHeartbeatPreview(preview);
        return 0;
    }

    private static async Task<int> HeartbeatWizardAsync(OpenClawHttpClient client)
    {
        var current = await client.GetHeartbeatAsync(CancellationToken.None);
        Console.WriteLine($"Config path: {current.ConfigPath}");
        Console.WriteLine($"HEARTBEAT path: {current.HeartbeatPath}");
        Console.WriteLine($"Current status: {(current.Config.Enabled ? "enabled" : "disabled")}");
        Console.WriteLine();

        var config = new HeartbeatConfigDto
        {
            Enabled = PromptBool("Enable heartbeat", current.Config.Enabled),
            CronExpression = Prompt("Cron expression", current.Config.CronExpression ?? "@hourly"),
            Timezone = Prompt("Timezone", current.Config.Timezone ?? "UTC"),
            DeliveryChannelId = Prompt("Delivery channel", current.Config.DeliveryChannelId ?? "cron").ToLowerInvariant(),
            DeliveryRecipientId = PromptOptional("Delivery recipient", current.Config.DeliveryRecipientId),
            DeliverySubject = PromptOptional("Delivery subject", current.Config.DeliverySubject),
            ModelId = PromptOptional("Model override", current.Config.ModelId),
            Tasks = PromptTasks(current)
        };

        var preview = await client.PreviewHeartbeatAsync(config, CancellationToken.None);
        Console.WriteLine();
        Console.WriteLine("Preview");
        Console.WriteLine("-------");
        WriteHeartbeatPreview(preview);

        var hasErrors = preview.Issues.Any(issue => string.Equals(issue.Severity, "error", StringComparison.OrdinalIgnoreCase));
        if (hasErrors)
        {
            Console.WriteLine("Heartbeat config has validation errors and was not saved.");
            return 1;
        }

        if (!PromptBool("Save this heartbeat config", true))
        {
            Console.WriteLine("Heartbeat config not saved.");
            return 0;
        }

        var saved = await client.SaveHeartbeatAsync(config, CancellationToken.None);
        Console.WriteLine("Heartbeat config saved.");
        WriteHeartbeatPreview(saved);
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

    private static IReadOnlyList<HeartbeatTaskDto> PromptTasks(HeartbeatPreviewResponse current)
    {
        var templates = current.AvailableTemplates.Where(template => template.Available).ToArray();
        if (templates.Length == 0)
        {
            Console.WriteLine("No heartbeat templates are currently available from the gateway.");
            return current.Config.Tasks;
        }

        Console.WriteLine("Available templates:");
        for (var i = 0; i < templates.Length; i++)
            Console.WriteLine($"  {i + 1}. {templates[i].Label} ({templates[i].Key})");

        if (current.Suggestions.Count > 0)
        {
            Console.WriteLine();
            Console.WriteLine("Suggestions:");
            for (var i = 0; i < current.Suggestions.Count; i++)
                Console.WriteLine($"  - {current.Suggestions[i].Title} [{current.Suggestions[i].TemplateKey}] — {current.Suggestions[i].Reason}");
        }

        var selected = Prompt("Template numbers (comma separated, blank keeps current tasks)", "");
        if (string.IsNullOrWhiteSpace(selected))
            return current.Config.Tasks;

        var tasks = new List<HeartbeatTaskDto>();
        var picks = selected.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        foreach (var pick in picks)
        {
            if (!int.TryParse(pick, out var index) || index < 1 || index > templates.Length)
                continue;

            var template = templates[index - 1];
            var defaultTitle = template.Label;
            var title = Prompt("Task title", defaultTitle);
            var target = PromptOptional("Task target", null);
            var instruction = PromptOptional("Task instruction", null);
            var priority = Prompt("Priority (low|normal|high)", "normal").ToLowerInvariant();
            var conditionMode = Prompt("Condition mode (and|or)", "and").ToLowerInvariant();
            var conditions = PromptConditions();

            tasks.Add(new HeartbeatTaskDto
            {
                Id = $"task-{Guid.NewGuid():N}"[..12],
                TemplateKey = template.Key,
                Title = title,
                Target = target,
                Instruction = instruction,
                Priority = priority,
                Enabled = true,
                ConditionMode = conditionMode,
                Conditions = conditions
            });
        }

        return tasks;
    }

    private static IReadOnlyList<HeartbeatConditionDto> PromptConditions()
    {
        var conditions = new List<HeartbeatConditionDto>();
        while (PromptBool("Add a condition", false))
        {
            var field = Prompt("Condition field", "subject");
            var op = Prompt("Condition operator (contains|equals|any_of|is_true)", "contains");
            var values = string.Equals(op, "is_true", StringComparison.OrdinalIgnoreCase)
                ? Array.Empty<string>()
                : Prompt("Condition values (comma separated)", "").Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

            conditions.Add(new HeartbeatConditionDto
            {
                Field = field,
                Operator = op,
                Values = values
            });
        }

        return conditions;
    }

    private static void WriteHeartbeatPreview(HeartbeatPreviewResponse preview)
    {
        Console.WriteLine($"Config path: {preview.ConfigPath}");
        Console.WriteLine($"HEARTBEAT path: {preview.HeartbeatPath}");
        Console.WriteLine($"memory.md path: {preview.MemoryMarkdownPath}");
        Console.WriteLine($"Managed job active: {preview.ManagedJobActive}");
        Console.WriteLine($"Drift detected: {preview.DriftDetected}");
        Console.WriteLine($"Model: {preview.CostEstimate.ProviderId}:{preview.CostEstimate.ModelId}");
        Console.WriteLine($"Estimated input tokens/run: {preview.CostEstimate.EstimatedInputTokensPerRun}");
        Console.WriteLine($"Estimated monthly runs: {preview.CostEstimate.EstimatedRunsPerMonth}");
        Console.WriteLine($"Estimated OK cost/month: ${preview.CostEstimate.EstimatedOkCostUsdPerMonth:F4}");
        Console.WriteLine($"Estimated alert cost/month: ${preview.CostEstimate.EstimatedAlertCostUsdPerMonth:F4}");
        Console.WriteLine();

        if (preview.Issues.Count > 0)
        {
            Console.WriteLine("Issues:");
            foreach (var issue in preview.Issues)
                Console.WriteLine($"- {issue.Severity}: {issue.Message}");
            Console.WriteLine();
        }

        Console.WriteLine(preview.HeartbeatMarkdown);
    }

    private static void WriteHeartbeatStatus(HeartbeatStatusResponse status)
    {
        Console.WriteLine($"Config path: {status.ConfigPath}");
        Console.WriteLine($"HEARTBEAT path: {status.HeartbeatPath}");
        Console.WriteLine($"memory.md path: {status.MemoryMarkdownPath}");
        Console.WriteLine($"Config exists: {status.ConfigExists}");
        Console.WriteLine($"HEARTBEAT exists: {status.HeartbeatExists}");
        Console.WriteLine($"Enabled: {status.Config.Enabled}");
        Console.WriteLine($"Drift detected: {status.DriftDetected}");
        Console.WriteLine($"Monthly runs: {status.CostEstimate.EstimatedRunsPerMonth}");
        Console.WriteLine($"Estimated OK cost/month: ${status.CostEstimate.EstimatedOkCostUsdPerMonth:F4}");
        Console.WriteLine($"Estimated alert cost/month: ${status.CostEstimate.EstimatedAlertCostUsdPerMonth:F4}");

        if (status.LastRun is not null)
        {
            Console.WriteLine($"Last run: {status.LastRun.LastRunAtUtc:O}");
            Console.WriteLine($"Last outcome: {status.LastRun.Outcome}");
            Console.WriteLine($"Delivery suppressed: {status.LastRun.DeliverySuppressed}");
            Console.WriteLine($"Last run tokens: in {status.LastRun.InputTokens} / out {status.LastRun.OutputTokens}");
            if (!string.IsNullOrWhiteSpace(status.LastRun.MessagePreview))
                Console.WriteLine($"Last preview: {status.LastRun.MessagePreview}");
        }

        if (status.Issues.Count > 0)
        {
            Console.WriteLine();
            Console.WriteLine("Issues:");
            foreach (var issue in status.Issues)
                Console.WriteLine($"- {issue.Severity}: {issue.Message}");
        }
    }

    private static void WritePosture(SecurityPostureResponse posture)
    {
        Console.WriteLine($"public_bind: {ToBoolWord(posture.PublicBind)}");
        Console.WriteLine($"auth_token_configured: {ToBoolWord(posture.AuthTokenConfigured)}");
        Console.WriteLine($"autonomy_mode: {posture.AutonomyMode}");
        Console.WriteLine($"tool_approval_required: {ToBoolWord(posture.ToolApprovalRequired)}");
        Console.WriteLine($"requester_match_http_tool_approval: {ToBoolWord(posture.RequireRequesterMatchForHttpToolApproval)}");
        Console.WriteLine($"browser_session_cookie_secure_effective: {ToBoolWord(posture.BrowserSessionCookieSecureEffective)}");
        Console.WriteLine($"trust_forwarded_headers: {ToBoolWord(posture.TrustForwardedHeaders)}");
        Console.WriteLine($"plugin_bridge: enabled={ToBoolWord(posture.PluginBridgeEnabled)} transport={posture.PluginBridgeTransportMode} security={posture.PluginBridgeSecurityMode}");
        Console.WriteLine($"sandbox_configured: {ToBoolWord(posture.SandboxConfigured)}");

        if (posture.RiskFlags.Count > 0)
        {
            Console.WriteLine("risk_flags:");
            foreach (var risk in posture.RiskFlags)
                Console.WriteLine($"- {risk}");
        }

        if (posture.Recommendations.Count > 0)
        {
            Console.WriteLine("recommendations:");
            foreach (var recommendation in posture.Recommendations)
                Console.WriteLine($"- {recommendation}");
        }
    }

    private static string Prompt(string label, string defaultValue)
    {
        Console.Write($"{label} [{defaultValue}]: ");
        var value = Console.ReadLine();
        return string.IsNullOrWhiteSpace(value) ? defaultValue : value.Trim();
    }

    private static string? PromptOptional(string label, string? defaultValue)
    {
        var suffix = string.IsNullOrWhiteSpace(defaultValue) ? "" : $" [{defaultValue}]";
        Console.Write($"{label}{suffix}: ");
        var value = Console.ReadLine();
        if (string.IsNullOrWhiteSpace(value))
            return string.IsNullOrWhiteSpace(defaultValue) ? null : defaultValue;
        return value.Trim();
    }

    private static bool PromptBool(string label, bool defaultValue)
    {
        Console.Write($"{label} [{(defaultValue ? "Y/n" : "y/N")}]: ");
        var value = Console.ReadLine()?.Trim().ToLowerInvariant();
        if (string.IsNullOrWhiteSpace(value))
            return defaultValue;
        return value is "y" or "yes" or "true";
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

    internal static string? ResolveAuthToken(CliArgs parsed, TextWriter error)
    {
        var cliToken = parsed.GetOption("--token");
        if (!string.IsNullOrWhiteSpace(cliToken))
        {
            error.WriteLine("Warning: --token is deprecated because command-line arguments can be exposed in process listings. Prefer OPENCLAW_AUTH_TOKEN.");
            return cliToken;
        }

        return Environment.GetEnvironmentVariable(EnvAuthToken);
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

    private static bool? ParseBool(string? raw)
    {
        if (string.IsNullOrWhiteSpace(raw))
            return null;

        return raw.Trim().ToLowerInvariant() switch
        {
            "1" or "true" or "yes" or "y" => true,
            "0" or "false" or "no" or "n" => false,
            _ => throw new ArgumentException($"Invalid bool: {raw}")
        };
    }

    private static string ToBoolWord(bool value) => value ? "true" : "false";
}

using System.Text.Json;
using OpenClaw.Core.Models;

namespace OpenClaw.Cli;

internal static class ChannelSetupCommand
{
    public static async Task<int> RunAsync(
        string[] args,
        TextReader input,
        TextWriter output,
        TextWriter error,
        bool canPrompt)
    {
        if (args.Length == 0 || args[0] is "-h" or "--help" or "help")
        {
            PrintHelp(output);
            return 0;
        }

        var channelId = args[0].Trim().ToLowerInvariant();
        if (channelId is not ("telegram" or "slack" or "discord" or "teams" or "whatsapp"))
        {
            error.WriteLine($"Unsupported channel: {channelId}");
            error.WriteLine("Supported channels: telegram, slack, discord, teams, whatsapp");
            return 2;
        }

        var parsed = CliArgs.Parse(args[1..]);
        if (parsed.ShowHelp)
        {
            PrintHelp(output);
            return 0;
        }

        var configPath = Path.GetFullPath(GatewayConfigFile.ExpandPath(parsed.GetOption("--config") ?? GatewayConfigFile.DefaultConfigPath));
        GatewayConfig config;
        try
        {
            config = GatewayConfigFile.Load(configPath);
        }
        catch (Exception ex) when (ex is FileNotFoundException or InvalidOperationException or JsonException)
        {
            error.WriteLine(ex.Message);
            error.WriteLine("Run 'openclaw setup' first to create the base config, or pass --config <path>.");
            return 1;
        }

        var nonInteractive = parsed.HasFlag("--non-interactive");
        if (!nonInteractive && !canPrompt)
        {
            error.WriteLine("Interactive channel setup requires a terminal. Re-run with explicit flags and --non-interactive.");
            return 2;
        }

        try
        {
            switch (channelId)
            {
                case "telegram":
                    ConfigureTelegram(config, parsed, input, output, nonInteractive);
                    break;
                case "slack":
                    ConfigureSlack(config, parsed, input, output, nonInteractive);
                    break;
                case "discord":
                    ConfigureDiscord(config, parsed, input, output, nonInteractive);
                    break;
                case "teams":
                    ConfigureTeams(config, parsed, input, output, nonInteractive);
                    break;
                case "whatsapp":
                    ConfigureWhatsApp(config, parsed, input, output, nonInteractive);
                    break;
            }
        }
        catch (ArgumentException ex)
        {
            error.WriteLine(ex.Message);
            return 2;
        }

        var validationErrors = OpenClaw.Core.Validation.ConfigValidator.Validate(config);
        if (validationErrors.Count > 0)
        {
            error.WriteLine("Config validation failed after channel update:");
            foreach (var validationError in validationErrors)
                error.WriteLine($"- {validationError}");
            return 1;
        }

        await GatewayConfigFile.SaveAsync(config, configPath);

        output.WriteLine($"Updated channel '{channelId}' in {configPath}");
        output.WriteLine("Next steps:");
        output.WriteLine($"- Restart or launch the gateway with: dotnet run --project src/OpenClaw.Gateway -c Release -- --config {GatewayConfigFile.QuoteIfNeeded(configPath)}");
        output.WriteLine($"- Verify with: dotnet run --project src/OpenClaw.Gateway -c Release -- --config {GatewayConfigFile.QuoteIfNeeded(configPath)} --doctor");
        output.WriteLine($"- Inspect operator posture with: OPENCLAW_BASE_URL={BuildReachableBaseUrl(config.BindAddress, config.Port)} OPENCLAW_AUTH_TOKEN={config.AuthToken} dotnet run --project src/OpenClaw.Cli -c Release -- admin posture");

        foreach (var line in BuildWebhookHints(channelId, config))
            output.WriteLine($"- {line}");

        return 0;
    }

    private static void ConfigureTelegram(GatewayConfig config, CliArgs parsed, TextReader input, TextWriter output, bool nonInteractive)
    {
        var channel = config.Channels.Telegram;
        channel.Enabled = true;
        channel.DmPolicy = GetValue(parsed, input, output, "--dm-policy", "DM policy", channel.DmPolicy, nonInteractive);
        channel.BotTokenRef = GetRequiredValue(parsed, input, output, "--bot-token-ref", "Telegram bot token ref", channel.BotTokenRef, nonInteractive);
        channel.WebhookPublicBaseUrl = GetRequiredValue(parsed, input, output, "--public-base-url", "Telegram webhook public base URL", channel.WebhookPublicBaseUrl, nonInteractive);
        channel.ValidateSignature = true;
        channel.WebhookSecretTokenRef = GetRequiredValue(parsed, input, output, "--webhook-secret-ref", "Telegram webhook secret ref", channel.WebhookSecretTokenRef, nonInteractive);
    }

    private static void ConfigureSlack(GatewayConfig config, CliArgs parsed, TextReader input, TextWriter output, bool nonInteractive)
    {
        var channel = config.Channels.Slack;
        channel.Enabled = true;
        channel.DmPolicy = GetValue(parsed, input, output, "--dm-policy", "DM policy", channel.DmPolicy, nonInteractive);
        channel.BotTokenRef = GetRequiredValue(parsed, input, output, "--bot-token-ref", "Slack bot token ref", channel.BotTokenRef, nonInteractive);
        channel.SigningSecretRef = GetRequiredValue(parsed, input, output, "--signing-secret-ref", "Slack signing secret ref", channel.SigningSecretRef, nonInteractive);
        channel.ValidateSignature = true;
    }

    private static void ConfigureDiscord(GatewayConfig config, CliArgs parsed, TextReader input, TextWriter output, bool nonInteractive)
    {
        var channel = config.Channels.Discord;
        channel.Enabled = true;
        channel.DmPolicy = GetValue(parsed, input, output, "--dm-policy", "DM policy", channel.DmPolicy, nonInteractive);
        channel.BotTokenRef = GetRequiredValue(parsed, input, output, "--bot-token-ref", "Discord bot token ref", channel.BotTokenRef, nonInteractive);
        channel.ApplicationIdRef = GetRequiredValue(parsed, input, output, "--application-id-ref", "Discord application ID ref", channel.ApplicationIdRef, nonInteractive);
        channel.PublicKeyRef = GetRequiredValue(parsed, input, output, "--public-key-ref", "Discord public key ref", channel.PublicKeyRef, nonInteractive);
        channel.ValidateSignature = true;
        channel.RegisterSlashCommands = true;
    }

    private static void ConfigureTeams(GatewayConfig config, CliArgs parsed, TextReader input, TextWriter output, bool nonInteractive)
    {
        var channel = config.Channels.Teams;
        channel.Enabled = true;
        channel.DmPolicy = GetValue(parsed, input, output, "--dm-policy", "DM policy", channel.DmPolicy, nonInteractive);
        channel.AppIdRef = GetRequiredValue(parsed, input, output, "--app-id-ref", "Teams app ID ref", channel.AppIdRef, nonInteractive);
        channel.AppPasswordRef = GetRequiredValue(parsed, input, output, "--app-password-ref", "Teams app password ref", channel.AppPasswordRef, nonInteractive);
        channel.TenantIdRef = GetRequiredValue(parsed, input, output, "--tenant-id-ref", "Teams tenant ID ref", channel.TenantIdRef, nonInteractive);
        channel.ValidateToken = true;
        channel.RequireMention = true;
    }

    private static void ConfigureWhatsApp(GatewayConfig config, CliArgs parsed, TextReader input, TextWriter output, bool nonInteractive)
    {
        var channel = config.Channels.WhatsApp;
        channel.Enabled = true;
        channel.DmPolicy = GetValue(parsed, input, output, "--dm-policy", "DM policy", channel.DmPolicy, nonInteractive);
        var mode = GetValue(parsed, input, output, "--mode", "WhatsApp mode (official|bridge)", channel.Type is "bridge" ? channel.Type : "official", nonInteractive).ToLowerInvariant();
        if (mode is not ("official" or "bridge"))
            throw new ArgumentException("WhatsApp mode must be 'official' or 'bridge'.");

        channel.Type = mode;
        if (mode == "official")
        {
            channel.CloudApiTokenRef = GetRequiredValue(parsed, input, output, "--cloud-api-token-ref", "WhatsApp Cloud API token ref", channel.CloudApiTokenRef, nonInteractive);
            channel.PhoneNumberId = GetRequiredValue(parsed, input, output, "--phone-number-id", "WhatsApp phone number ID", channel.PhoneNumberId, nonInteractive);
            channel.WebhookAppSecretRef = GetRequiredValue(parsed, input, output, "--app-secret-ref", "WhatsApp webhook app secret ref", channel.WebhookAppSecretRef, nonInteractive);
            channel.ValidateSignature = true;
        }
        else
        {
            channel.BridgeUrl = GetRequiredValue(parsed, input, output, "--bridge-url", "WhatsApp bridge URL", channel.BridgeUrl, nonInteractive);
            channel.BridgeTokenRef = GetRequiredValue(parsed, input, output, "--bridge-token-ref", "WhatsApp bridge token ref", channel.BridgeTokenRef, nonInteractive);
        }
    }

    private static string GetRequiredValue(CliArgs parsed, TextReader input, TextWriter output, string option, string label, string? currentValue, bool nonInteractive)
    {
        var value = GetValue(parsed, input, output, option, label, currentValue, nonInteractive);
        if (string.IsNullOrWhiteSpace(value))
            throw new ArgumentException($"{option} is required.");
        return value;
    }

    private static string GetValue(CliArgs parsed, TextReader input, TextWriter output, string option, string label, string? currentValue, bool nonInteractive)
    {
        var value = parsed.GetOption(option);
        if (!string.IsNullOrWhiteSpace(value))
            return value;

        if (nonInteractive)
        {
            if (!string.IsNullOrWhiteSpace(currentValue))
                return currentValue;

            throw new ArgumentException($"{option} is required when --non-interactive is set.");
        }

        return Prompt(output, input, label, currentValue ?? string.Empty);
    }

    private static string Prompt(TextWriter output, TextReader input, string label, string defaultValue)
    {
        output.Write($"{label} [{defaultValue}]: ");
        var value = input.ReadLine();
        return string.IsNullOrWhiteSpace(value) ? defaultValue : value.Trim();
    }

    private static IEnumerable<string> BuildWebhookHints(string channelId, GatewayConfig config)
    {
        return channelId switch
        {
            "telegram" => [$"Register Telegram webhook: {TrimTrailingSlash(config.Channels.Telegram.WebhookPublicBaseUrl) + config.Channels.Telegram.WebhookPath}"],
            "slack" => [
                $"Slack events URL: {BuildRouteHint(config.BindAddress, config.Port, config.Channels.Slack.WebhookPath)}",
                $"Slack slash command URL: {BuildRouteHint(config.BindAddress, config.Port, config.Channels.Slack.SlashCommandPath)}"
            ],
            "discord" => [$"Discord interactions URL: {BuildRouteHint(config.BindAddress, config.Port, config.Channels.Discord.WebhookPath)}"],
            "teams" => [$"Teams Bot Framework endpoint: {BuildRouteHint(config.BindAddress, config.Port, config.Channels.Teams.WebhookPath)}"],
            "whatsapp" when string.Equals(config.Channels.WhatsApp.Type, "bridge", StringComparison.OrdinalIgnoreCase) => [$"WhatsApp bridge inbound URL expected by the gateway: {BuildRouteHint(config.BindAddress, config.Port, config.Channels.WhatsApp.WebhookPath)}"],
            "whatsapp" => [$"WhatsApp official webhook URL: {BuildRouteHint(config.BindAddress, config.Port, config.Channels.WhatsApp.WebhookPath)}"],
            _ => []
        };
    }

    private static string BuildRouteHint(string bindAddress, int port, string path)
        => TrimTrailingSlash(BuildReachableBaseUrl(bindAddress, port)) + path;

    private static string TrimTrailingSlash(string? value)
        => string.IsNullOrWhiteSpace(value) ? string.Empty : value.TrimEnd('/');

    private static string BuildReachableBaseUrl(string bindAddress, int port)
    {
        if (string.Equals(bindAddress, "0.0.0.0", StringComparison.Ordinal) ||
            string.Equals(bindAddress, "::", StringComparison.Ordinal) ||
            string.Equals(bindAddress, "[::]", StringComparison.Ordinal))
        {
            return $"http://127.0.0.1:{port}";
        }

        if (bindAddress.Contains(':') && !bindAddress.StartsWith("[", StringComparison.Ordinal))
            return $"http://[{bindAddress}]:{port}";

        return $"http://{bindAddress}:{port}";
    }

    private static void PrintHelp(TextWriter output)
    {
        output.WriteLine(
            """
            openclaw setup channel

            Usage:
              openclaw setup channel <telegram|slack|discord|teams|whatsapp> [--config <path>] [--non-interactive] [channel-specific options]

            Common options:
              --config <path>       External gateway config to update (default: ~/.openclaw/config/openclaw.settings.json)
              --non-interactive     Require explicit values instead of prompting
              --dm-policy <mode>    open | pairing | closed

            Telegram options:
              --bot-token-ref <ref>
              --public-base-url <url>
              --webhook-secret-ref <ref>

            Slack options:
              --bot-token-ref <ref>
              --signing-secret-ref <ref>

            Discord options:
              --bot-token-ref <ref>
              --application-id-ref <ref>
              --public-key-ref <ref>

            Teams options:
              --app-id-ref <ref>
              --app-password-ref <ref>
              --tenant-id-ref <ref>

            WhatsApp options:
              --mode <official|bridge>
              --cloud-api-token-ref <ref>
              --phone-number-id <id>
              --app-secret-ref <ref>
              --bridge-url <url>
              --bridge-token-ref <ref>
            """);
    }
}

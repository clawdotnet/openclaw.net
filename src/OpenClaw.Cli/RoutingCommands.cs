using OpenClaw.Core.Models;

namespace OpenClaw.Cli;

internal static class RoutingCommands
{
    private const string RouterOption = "--router";
    private const string RouterModeRecommended = "recommended";
    private const string RouterModeOpenRouterMix = "openrouter-mix";
    private const string RouterModeDisabled = "disabled";

    public static async Task<int> RunAsync(string[] args)
        => await RunAsync(args, Console.Out, Console.Error);

    internal static async Task<int> RunAsync(string[] args, TextWriter output, TextWriter error)
    {
        if (args.Length == 0 || args[0] is "-h" or "--help" or "help")
        {
            PrintHelp(output);
            return 0;
        }

        var parsed = CliArgs.Parse(args);
        if (parsed.ShowHelp || parsed.Positionals.Count == 0)
        {
            PrintHelp(output);
            return 0;
        }

        var command = parsed.Positionals[0].ToLowerInvariant();

        return command switch
        {
            "onboard" => await RunOnboardAsync(parsed, output, error),
            "configure" => await RunConfigureAsync(parsed, output, error),
            "providers" => await RunProvidersAsync(parsed, output, error),
            "status" => await RunStatusAsync(parsed, output, error),
            "diagnostics" => await RunDiagnosticsAsync(parsed, output, error),
            _ => 2
        };
    }

    private const string ConfigOption = "--config";

    private static async Task<int> RunOnboardAsync(CliArgs parsed, TextWriter output, TextWriter error)
    {
        if (!TryGetRouterMode(parsed.GetOption(RouterOption), defaultMode: RouterModeRecommended, out var mode, out var modeError))
        {
            error.WriteLine(modeError);
            return 2;
        }

        try
        {
            var path = ResolveConfigPath(parsed);
            var config = GatewayConfigFile.Load(path);
            ApplyRouterMode(config.DynamicTurnRouting, mode!);
            await GatewayConfigFile.SaveAsync(config, path);
            output.WriteLine($"routing onboard completed with router={mode} ({GatewayConfigFile.QuoteIfNeeded(path)})");
            return 0;
        }
        catch (Exception ex)
        {
            error.WriteLine(ex.Message);
            return 1;
        }
    }

    private static async Task<int> RunConfigureAsync(CliArgs parsed, TextWriter output, TextWriter error)
    {
        if (parsed.Positionals.Count < 2)
        {
            error.WriteLine("configure target is required. Use: openclaw routing configure <router|providers> [options]");
            return 2;
        }

        var target = parsed.Positionals[1].ToLowerInvariant();
        if (target is not ("router" or "providers"))
        {
            error.WriteLine("configure target must be router or providers.");
            return 2;
        }

        return target switch
        {
            "router" => await ConfigureRouterAsync(parsed, output, error),
            "providers" => await ConfigureProvidersAsync(parsed, output, error),
            _ => 2
        };
    }

    private static Task<int> RunProvidersAsync(CliArgs parsed, TextWriter output, TextWriter error)
    {
        try
        {
            var config = GatewayConfigFile.Load(ResolveConfigPath(parsed));
            var tiers = config.DynamicTurnRouting.Policy.Tiers;
            WriteTier("T0", tiers.T0, output);
            WriteTier("T1", tiers.T1, output);
            WriteTier("T2", tiers.T2, output);
            WriteTier("T3", tiers.T3, output);
            return Task.FromResult(0);
        }
        catch (Exception ex)
        {
            error.WriteLine(ex.Message);
            return Task.FromResult(1);
        }
    }

    private static Task<int> RunStatusAsync(CliArgs parsed, TextWriter output, TextWriter error)
    {
        try
        {
            var config = GatewayConfigFile.Load(ResolveConfigPath(parsed));
            var routing = config.DynamicTurnRouting;
            var policy = routing.Policy;
            output.WriteLine($"enabled={routing.Enabled}");
            output.WriteLine($"bundlePath={routing.BundlePath}");
            output.WriteLine($"classifier={routing.Assets.ClassifierModelPath}");
            output.WriteLine($"embedding={routing.Assets.EmbeddingModelPath}");
            output.WriteLine($"tokenizer={routing.Assets.TokenizerPath}");
            output.WriteLine($"diagnostics={policy.EnableDiagnostics}");
            output.WriteLine($"marginUpgradeThreshold={policy.MarginUpgradeThreshold:0.00}");
            output.WriteLine($"r1RescueThreshold={policy.R1RescueThreshold:0.00}");
            output.WriteLine($"underRoutingSafetyThreshold={policy.UnderRoutingSafetyThreshold:0.00}");
            output.WriteLine($"deepTurnThreshold={policy.DeepConversationTurnIndexThreshold}");
            return Task.FromResult(0);
        }
        catch (Exception ex)
        {
            error.WriteLine(ex.Message);
            return Task.FromResult(1);
        }
    }

    private static async Task<int> RunDiagnosticsAsync(CliArgs parsed, TextWriter output, TextWriter error)
    {
        if (parsed.Positionals.Count < 2)
        {
            error.WriteLine("diagnostics mode is required. Use: openclaw routing diagnostics <on|off>");
            return 2;
        }

        var mode = parsed.Positionals[1].ToLowerInvariant();
        if (mode is not ("on" or "off"))
        {
            error.WriteLine("diagnostics mode must be on or off.");
            return 2;
        }

        try
        {
            var path = ResolveConfigPath(parsed);
            var config = GatewayConfigFile.Load(path);
            config.DynamicTurnRouting.Policy.EnableDiagnostics = mode == "on";
            await GatewayConfigFile.SaveAsync(config, path);
            output.WriteLine($"routing diagnostics set to {mode} ({GatewayConfigFile.QuoteIfNeeded(path)})");
            return 0;
        }
        catch (Exception ex)
        {
            error.WriteLine(ex.Message);
            return 1;
        }
    }

    private static async Task<int> ConfigureRouterAsync(CliArgs parsed, TextWriter output, TextWriter error)
    {
        try
        {
            var path = ResolveConfigPath(parsed);
            var config = GatewayConfigFile.Load(path);
            if (!TryGetRouterMode(parsed.GetOption(RouterOption), defaultMode: null, out var mode, out var modeError))
            {
                error.WriteLine(modeError);
                return 2;
            }

            if (mode is not null)
                ApplyRouterMode(config.DynamicTurnRouting, mode);

            var policy = config.DynamicTurnRouting.Policy;

            if (TryReadFloatOption(parsed, "--margin-upgrade-threshold", out var margin))
                policy.MarginUpgradeThreshold = margin;
            if (TryReadFloatOption(parsed, "--r1-rescue-threshold", out var r1))
                policy.R1RescueThreshold = r1;
            if (TryReadFloatOption(parsed, "--under-routing-safety-threshold", out var safety))
                policy.UnderRoutingSafetyThreshold = safety;
            if (TryReadIntOption(parsed, "--deep-turn-threshold", out var deepTurn))
                policy.DeepConversationTurnIndexThreshold = deepTurn;

            await GatewayConfigFile.SaveAsync(config, path);
            output.WriteLine($"routing configure router saved ({GatewayConfigFile.QuoteIfNeeded(path)})");
            return 0;
        }
        catch (Exception ex)
        {
            error.WriteLine(ex.Message);
            return 1;
        }
    }

    private static async Task<int> ConfigureProvidersAsync(CliArgs parsed, TextWriter output, TextWriter error)
    {
        var tierName = parsed.GetOption("--tier");
        if (string.IsNullOrWhiteSpace(tierName))
        {
            error.WriteLine("--tier <T0|T1|T2|T3> is required for provider configuration.");
            return 2;
        }

        try
        {
            var path = ResolveConfigPath(parsed);
            var config = GatewayConfigFile.Load(path);
            var tier = ResolveTier(config.DynamicTurnRouting.Policy.Tiers, tierName);

            if (TryReadStringOption(parsed, "--model-profile", out var modelProfile))
                tier.ModelProfileId = modelProfile;
            if (TryReadStringOption(parsed, "--fallback-profile", out var fallbackProfile))
                tier.DirectModelFallbackProfileId = fallbackProfile;
            if (TryReadStringOption(parsed, "--reasoning-level", out var reasoningLevel))
                tier.ReasoningLevel = reasoningLevel;
            if (TryReadStringOption(parsed, "--response-policy", out var responsePolicy))
                tier.ResponsePolicy = responsePolicy;
            if (TryReadStringOption(parsed, "--image-model-profile", out var imageProfile))
                tier.ImageCapableModelProfileId = imageProfile;
            if (TryReadStringOption(parsed, "--allowed-tools", out var allowedTools))
                tier.AllowedTools = SplitCsv(allowedTools);
            if (TryReadStringOption(parsed, "--preferred-tags", out var preferredTags))
                tier.PreferredTags = SplitCsv(preferredTags);
            if (TryReadStringOption(parsed, "--prompt-mode", out var promptMode))
                tier.PromptMode = promptMode;
            if (TryReadBoolOption(parsed, "--disable-tools", out var disableTools))
                tier.DisableTools = disableTools;

            await GatewayConfigFile.SaveAsync(config, path);
            output.WriteLine($"routing configure providers saved for {tierName.ToUpperInvariant()} ({GatewayConfigFile.QuoteIfNeeded(path)})");
            return 0;
        }
        catch (Exception ex)
        {
            error.WriteLine(ex.Message);
            return 1;
        }
    }

    private static string ResolveConfigPath(CliArgs parsed)
        => Path.GetFullPath(GatewayConfigFile.ExpandPath(parsed.GetOption(ConfigOption) ?? GatewayConfigFile.DefaultConfigPath));

    private static DynamicTurnRoutingTierTarget ResolveTier(DynamicTurnRoutingTierMap tiers, string tierName)
        => tierName.Trim().ToUpperInvariant() switch
        {
            "T0" => tiers.T0,
            "T1" => tiers.T1,
            "T2" => tiers.T2,
            "T3" => tiers.T3,
            _ => throw new ArgumentException("--tier must be one of T0/T1/T2/T3")
        };

    private static void WriteTier(string name, DynamicTurnRoutingTierTarget tier, TextWriter output)
    {
        var tools = tier.DisableTools ? "disabled" : JoinOrNone(tier.AllowedTools);
        output.WriteLine($"{name}\tprofile={tier.ModelProfileId}\tfallback={tier.DirectModelFallbackProfileId}\treasoning={tier.ReasoningLevel}\tresponse={tier.ResponsePolicy}\timage={tier.ImageCapableModelProfileId}\tpromptMode={tier.PromptMode}\ttools={tools}\ttags={JoinOrNone(tier.PreferredTags)}");
    }

    private static bool TryReadStringOption(CliArgs parsed, string option, out string value)
    {
        value = parsed.GetOption(option) ?? string.Empty;
        return !string.IsNullOrWhiteSpace(value);
    }

    private static bool TryReadFloatOption(CliArgs parsed, string option, out float value)
    {
        value = 0f;
        var raw = parsed.GetOption(option);
        return !string.IsNullOrWhiteSpace(raw)
            && float.TryParse(raw, out value);
    }

    private static bool TryReadIntOption(CliArgs parsed, string option, out int value)
    {
        value = 0;
        var raw = parsed.GetOption(option);
        return !string.IsNullOrWhiteSpace(raw)
            && int.TryParse(raw, out value);
    }

    private static bool TryReadBoolOption(CliArgs parsed, string option, out bool value)
    {
        value = false;
        var raw = parsed.GetOption(option);
        return !string.IsNullOrWhiteSpace(raw)
            && bool.TryParse(raw, out value);
    }

    private static string[] SplitCsv(string value)
        => value
            .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Where(static item => !string.IsNullOrWhiteSpace(item))
            .ToArray();

    private static string JoinOrNone(string[] values)
        => values.Length == 0 ? "none" : string.Join(',', values);

    private static bool TryGetRouterMode(string? rawValue, string? defaultMode, out string? mode, out string? error)
    {
        mode = defaultMode;
        error = null;

        if (string.IsNullOrWhiteSpace(rawValue))
            return true;

        var normalized = rawValue.Trim().ToLowerInvariant();
        if (normalized is RouterModeRecommended or RouterModeOpenRouterMix or RouterModeDisabled)
        {
            mode = normalized;
            return true;
        }

        error = "--router must be one of recommended, openrouter-mix, disabled.";
        return false;
    }

    private static void ApplyRouterMode(DynamicTurnRoutingConfig routing, string mode)
    {
        routing.Enabled = mode is not RouterModeDisabled;

        if (mode is RouterModeOpenRouterMix)
        {
            ApplyOpenRouterTags(routing.Policy.Tiers.T0, "cost");
            ApplyOpenRouterTags(routing.Policy.Tiers.T1, "fast");
            ApplyOpenRouterTags(routing.Policy.Tiers.T2, "tools");
            ApplyOpenRouterTags(routing.Policy.Tiers.T3, "reasoning");
        }
    }

    private static void ApplyOpenRouterTags(DynamicTurnRoutingTierTarget tier, string routeTag)
    {
        var tags = new HashSet<string>(tier.PreferredTags, StringComparer.OrdinalIgnoreCase)
        {
            "openrouter",
            routeTag
        };
        tier.PreferredTags = [.. tags.OrderBy(static item => item, StringComparer.OrdinalIgnoreCase)];
    }

    private static void PrintHelp(TextWriter output)
    {
        output.WriteLine(
            """
            openclaw routing

            Usage:
                            openclaw routing onboard [--router recommended|openrouter-mix|disabled]
              openclaw routing configure <router|providers> [options]
                            openclaw routing configure router [--router recommended|openrouter-mix|disabled] [--margin-upgrade-threshold <n>] [--r1-rescue-threshold <n>] [--under-routing-safety-threshold <n>] [--deep-turn-threshold <n>] [--config <path>]
                            openclaw routing configure providers --tier <T0|T1|T2|T3> [--model-profile <id>] [--fallback-profile <id>] [--reasoning-level <level>] [--response-policy <policy>] [--image-model-profile <id>] [--allowed-tools <csv>] [--preferred-tags <csv>] [--prompt-mode <full|minimal|compact>] [--disable-tools <true|false>] [--config <path>]
              openclaw routing providers [--json]
              openclaw routing status [--json]
              openclaw routing diagnostics <on|off> [--config <path>]

            Notes:
              - Routing remains configuration-driven through OpenClaw:DynamicTurnRouting.
                            - --router recommended enables dynamic routing with existing tier mappings.
                            - --router openrouter-mix enables routing and appends openrouter-oriented preferred tags by tier.
                            - --router disabled turns dynamic routing off.
              - This command group provides operator-oriented routing entry points.
            """);
    }
}

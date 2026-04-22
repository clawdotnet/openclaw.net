using OpenClaw.Core.Setup;

namespace OpenClaw.Gateway.Bootstrap;

internal sealed class StartupLaunchOptions
{
    private StartupLaunchOptions(
        string[] originalArgs,
        string[] effectiveArgs,
        bool isDoctorMode,
        bool isHealthCheckMode,
        bool isQuickstartRequested,
        string? configPathFromArgs,
        string? configPathFromEnvironment,
        bool canPrompt)
    {
        OriginalArgs = originalArgs;
        EffectiveArgs = effectiveArgs;
        IsDoctorMode = isDoctorMode;
        IsHealthCheckMode = isHealthCheckMode;
        IsQuickstartRequested = isQuickstartRequested;
        ConfigPathFromArgs = configPathFromArgs;
        ConfigPathFromEnvironment = configPathFromEnvironment;
        CanPrompt = canPrompt;
    }

    public string[] OriginalArgs { get; }

    public string[] EffectiveArgs { get; }

    public bool IsDoctorMode { get; }

    public bool IsHealthCheckMode { get; }

    public bool IsQuickstartRequested { get; }

    public bool CanPrompt { get; }

    public string? ConfigPathFromArgs { get; }

    public string? ConfigPathFromEnvironment { get; }

    public string? ExternalConfigPath => ConfigPathFromArgs ?? ConfigPathFromEnvironment;

    public bool HasConfigArgument => !string.IsNullOrWhiteSpace(ConfigPathFromArgs);

    public bool HasExternalConfigOverride => !string.IsNullOrWhiteSpace(ExternalConfigPath);

    public bool SuppressSavePrompt => HasExternalConfigOverride;

    public bool ShouldSuggestQuickstart => CanPrompt && !IsQuickstartRequested && !IsDoctorMode && !IsHealthCheckMode;

    public static StartupLaunchOptions Parse(string[] args)
    {
        var configArg = FindArgValue(args, "--config");
        var envConfigPath = Environment.GetEnvironmentVariable("OPENCLAW_CONFIG_PATH");
        return new StartupLaunchOptions(
            originalArgs: args,
            effectiveArgs: [.. args.Where(static arg => !string.Equals(arg, "--quickstart", StringComparison.Ordinal))],
            isDoctorMode: args.Any(static a => string.Equals(a, "--doctor", StringComparison.Ordinal)),
            isHealthCheckMode: args.Any(static a => string.Equals(a, "--health-check", StringComparison.Ordinal)),
            isQuickstartRequested: args.Any(static a => string.Equals(a, "--quickstart", StringComparison.Ordinal)),
            configPathFromArgs: string.IsNullOrWhiteSpace(configArg) ? null : System.IO.Path.GetFullPath(GatewaySetupPaths.ExpandPath(configArg)),
            configPathFromEnvironment: string.IsNullOrWhiteSpace(envConfigPath) ? null : System.IO.Path.GetFullPath(GatewaySetupPaths.ExpandPath(envConfigPath)),
            canPrompt: TerminalPrompts.IsInteractiveConsole());
    }

    public string? ValidateQuickstart()
    {
        if (!IsQuickstartRequested)
            return null;
        if (IsDoctorMode)
            return "--quickstart cannot be combined with --doctor.";
        if (IsHealthCheckMode)
            return "--quickstart cannot be combined with --health-check.";
        if (HasConfigArgument)
            return "--quickstart cannot be combined with --config.";
        if (!string.IsNullOrWhiteSpace(ConfigPathFromEnvironment))
            return "--quickstart cannot be used while OPENCLAW_CONFIG_PATH is set.";
        if (!CanPrompt)
            return "--quickstart requires an interactive terminal.";
        return null;
    }

    private static string? FindArgValue(string[] argv, string name)
    {
        for (var i = 0; i < argv.Length; i++)
        {
            var arg = argv[i];
            if (arg.Equals(name, StringComparison.Ordinal) && i + 1 < argv.Length)
                return argv[i + 1];

            var prefix = name + "=";
            if (arg.StartsWith(prefix, StringComparison.Ordinal))
                return arg[prefix.Length..];
        }

        return null;
    }
}

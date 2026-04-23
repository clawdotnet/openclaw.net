using OpenClaw.Core.Setup;

namespace OpenClaw.Cli;

internal static class StartCommand
{
    private static readonly StartCommandHandlers DefaultHandlers = new();

    public static async Task<int> RunAsync(
        string[] args,
        TextReader input,
        TextWriter output,
        TextWriter error,
        string currentDirectory,
        bool canPrompt,
        StartCommandHandlers? handlers = null)
    {
        var parsed = CliArgs.Parse(args);
        if (parsed.ShowHelp)
        {
            WriteHelp(output);
            return 0;
        }

        handlers ??= DefaultHandlers;
        var configPath = ResolveConfigPath(parsed);
        var launchArgs = EnsureConfigArgument(args, configPath);

        if (handlers.ConfigExists(configPath))
        {
            output.WriteLine($"Using config: {configPath}");
            return await handlers.RunLaunchAsync(launchArgs, output, error, currentDirectory);
        }

        output.WriteLine(canPrompt
            ? $"No config found at {configPath}. Running guided setup."
            : $"No config found at {configPath}. Running setup with the provided arguments.");

        var setupResult = await handlers.RunSetupAsync(args, input, output, error, currentDirectory, canPrompt);
        if (setupResult.ExitCode != 0)
            return setupResult.ExitCode;

        output.WriteLine();
        output.WriteLine("Setup completed. Launching gateway...");
        var launchConfigPath = string.IsNullOrWhiteSpace(setupResult.ConfigPath) ? configPath : setupResult.ConfigPath;
        return await handlers.RunLaunchAsync(EnsureConfigArgument(args, launchConfigPath), output, error, currentDirectory);
    }

    public static void WriteHelp(TextWriter output)
    {
        output.WriteLine(
            """
            openclaw start

            Usage:
              openclaw start [--config <path>] [--with-companion] [--open-browser] [--skip-verify] [--offline] [--require-provider]
                              [--profile <local|public>] [--non-interactive]
                              [--workspace <path>] [--provider <id>] [--model <id>] [--model-preset <id>] [--api-key <secret-or-envref>]
                              [--bind <address>] [--port <n>] [--auth-token <token>]
                              [--docker-image <image>] [--opensandbox-endpoint <url>] [--ssh-host <host>] [--ssh-user <user>] [--ssh-key <path>]

            Notes:
              - This is the primary supported local entrypoint.
              - If the config already exists, it launches the gateway with verification.
              - If the config is missing, it runs setup first and then launches.
              - Use --non-interactive together with explicit setup inputs for automation or CI.
            """);
    }

    private static string ResolveConfigPath(CliArgs parsed)
        => Path.GetFullPath(GatewaySetupPaths.ExpandPath(parsed.GetOption("--config") ?? GatewaySetupPaths.DefaultConfigPath));

    private static string[] EnsureConfigArgument(string[] args, string configPath)
    {
        var rewritten = new List<string>(args.Length + 2);
        var foundConfigArgument = false;
        for (var i = 0; i < args.Length; i++)
        {
            if (string.Equals(args[i], "--config", StringComparison.Ordinal))
            {
                foundConfigArgument = true;
                rewritten.Add("--config");
                rewritten.Add(configPath);
                if (i + 1 < args.Length)
                    i++;
                continue;
            }

            rewritten.Add(args[i]);
        }

        if (foundConfigArgument)
            return [.. rewritten];

        return ["--config", configPath, .. args];
    }
}

internal sealed class StartCommandHandlers
{
    public Func<string, bool> ConfigExists { get; init; } = File.Exists;

    public Func<string[], TextReader, TextWriter, TextWriter, string, bool, Task<SetupCommandResult>> RunSetupAsync { get; init; }
        = static (args, input, output, error, currentDirectory, canPrompt)
            => SetupCommand.RunWithResultAsync(args, input, output, error, currentDirectory, canPrompt);

    public Func<string[], TextWriter, TextWriter, string, Task<int>> RunLaunchAsync { get; init; }
        = static (args, output, error, currentDirectory)
            => SetupLifecycleCommand.RunLaunchAsync(args, output, error, currentDirectory);
}

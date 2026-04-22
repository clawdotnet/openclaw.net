using System.Security.Cryptography;
using Microsoft.Extensions.Logging;
using OpenClaw.Core.Setup;

namespace OpenClaw.Gateway.Bootstrap;

internal static class LocalStartupPostReadyActions
{
    public static async Task RunAsync(
        GatewayStartupContext startup,
        StartupLaunchOptions launchOptions,
        LocalStartupSession localSession,
        LocalStartupStateStore stateStore,
        ILogger logger,
        CancellationToken stoppingToken,
        TextReader? input = null,
        TextWriter? output = null,
        Action<string>? openBrowser = null,
        string? saveConfigPathOverride = null)
    {
        input ??= Console.In;
        output ??= Console.Out;
        openBrowser ??= BrowserLauncher.TryOpen;

        try
        {
            var currentState = MergeState(stateStore.Load(), localSession);
            PersistState(stateStore, currentState, logger);

            if (!currentState.BrowserPromptShown)
            {
                if (TerminalPrompts.PromptYesNo(output, input, "Open Chat UI in your browser now?", defaultValue: true))
                    openBrowser($"http://localhost:{localSession.Port}/chat");

                currentState = MergeState(currentState, localSession, browserPromptShown: true);
                PersistState(stateStore, currentState, logger);
            }

            if (!launchOptions.SuppressSavePrompt && !stoppingToken.IsCancellationRequested)
            {
                var defaultConfigPath = saveConfigPathOverride ?? GatewaySetupPaths.ResolveDefaultConfigPath();
                if (TerminalPrompts.PromptYesNo(
                    output,
                    input,
                    $"Save this local setup to {defaultConfigPath} for next time?",
                    defaultValue: true))
                {
                    var saved = await SaveLocalConfigAsync(startup, localSession, defaultConfigPath);
                    output.WriteLine($"Saved config: {saved.ConfigPath}");
                    output.WriteLine($"Saved env example: {saved.EnvExamplePath}");
                    output.Flush();

                    currentState = MergeState(currentState, localSession, browserPromptShown: true, lastSavedConfigPath: saved.ConfigPath);
                    PersistState(stateStore, currentState, logger);
                }
            }
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Post-ready startup prompts failed.");
        }
    }

    internal static LocalStartupState MergeState(
        LocalStartupState state,
        LocalStartupSession session,
        bool? browserPromptShown = null,
        string? lastSavedConfigPath = null)
        => new()
        {
            WorkspacePath = session.WorkspacePath,
            MemoryPath = session.MemoryPath,
            Port = session.Port,
            Provider = session.Provider,
            Model = session.Model,
            BrowserPromptShown = browserPromptShown ?? state.BrowserPromptShown,
            LastSavedConfigPath = lastSavedConfigPath ?? state.LastSavedConfigPath
        };

    internal static void PersistState(LocalStartupStateStore stateStore, LocalStartupState state, ILogger logger)
    {
        if (!stateStore.TrySave(state, out var error) && !string.IsNullOrWhiteSpace(error))
            logger.LogWarning("Failed to update local startup state: {Error}", error);
    }

    internal static async Task<(string ConfigPath, string EnvExamplePath)> SaveLocalConfigAsync(
        GatewayStartupContext startup,
        LocalStartupSession localSession,
        string configPath)
    {
        var authToken = string.IsNullOrWhiteSpace(startup.Config.AuthToken)
            ? $"oc_{Convert.ToHexString(RandomNumberGenerator.GetBytes(24)).ToLowerInvariant()}"
            : startup.Config.AuthToken!;
        var config = GatewaySetupProfileFactory.CreateProfileConfig(
            profile: "local",
            bindAddress: "127.0.0.1",
            port: localSession.Port,
            authToken: authToken,
            workspacePath: localSession.WorkspacePath,
            memoryPath: localSession.MemoryPath,
            provider: localSession.Provider,
            model: localSession.Model,
            apiKey: localSession.ApiKeyReference,
            warnings: null);

        if (!string.IsNullOrWhiteSpace(localSession.Endpoint))
            config.Llm.Endpoint = localSession.Endpoint;

        await GatewayConfigFile.SaveAsync(config, configPath);
        var envExamplePath = GatewaySetupArtifacts.BuildEnvExamplePath(configPath);
        await File.WriteAllTextAsync(
            envExamplePath,
            GatewaySetupArtifacts.BuildEnvExample(
                localSession.ApiKeyReference,
                authToken,
                localSession.WorkspacePath,
                GatewaySetupArtifacts.BuildReachableBaseUrl("127.0.0.1", localSession.Port)),
            CancellationToken.None);
        return (configPath, envExamplePath);
    }
}

using Microsoft.Extensions.Configuration;

namespace OpenClaw.Gateway.Bootstrap;

internal sealed class StartupConsoleCoordinator
{
    public void WritePhase(string phase, TextWriter? output = null)
    {
        var writer = output ?? Console.Out;
        writer.WriteLine(phase);
        writer.Flush();
    }

    public void WriteConfigurationSummary(
        ConfigurationManager configuration,
        string environmentName,
        LocalStartupSession? session,
        TextWriter? output = null)
    {
        var writer = output ?? Console.Out;
        writer.WriteLine($"Startup environment: {environmentName}");
        writer.WriteLine("Configuration sources:");

        foreach (var source in BuildJsonSources(configuration))
            writer.WriteLine($"- {source}");

        writer.WriteLine(session is null
            ? "- Session-only overrides: none"
            : $"- Session-only overrides: active ({session.Mode})");
        writer.Flush();
    }

    private static IReadOnlyList<string> BuildJsonSources(ConfigurationManager configuration)
    {
        var sources = new List<string>();
        foreach (var source in configuration.Sources.OfType<FileConfigurationSource>())
        {
            if (string.IsNullOrWhiteSpace(source.Path) || !source.Path.EndsWith(".json", StringComparison.OrdinalIgnoreCase))
                continue;

            sources.Add(source.Path);
        }

        return [.. sources.Distinct(StringComparer.OrdinalIgnoreCase)];
    }
}

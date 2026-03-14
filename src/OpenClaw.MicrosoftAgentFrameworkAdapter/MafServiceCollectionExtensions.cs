using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using OpenClaw.Agent;

namespace OpenClaw.MicrosoftAgentFrameworkAdapter;

public static class MafServiceCollectionExtensions
{
    public static IServiceCollection AddMicrosoftAgentFrameworkExperiment(
        this IServiceCollection services,
        IConfiguration configuration)
    {
        services.AddSingleton<IOptions<MafOptions>>(_ => Options.Create(CreateOptions(configuration)));
        services.AddSingleton<MafTelemetryAdapter>();
        services.AddSingleton<MafSessionStateStore>();
        services.AddSingleton<MafAgentFactory>();
        services.AddSingleton<IAgentRuntimeFactory, MafAgentRuntimeFactory>();
        return services;
    }

    private static MafOptions CreateOptions(IConfiguration configuration)
    {
        var section = configuration.GetSection(MafOptions.SectionName);
        var options = new MafOptions();

        var agentName = section["AgentName"];
        if (!string.IsNullOrWhiteSpace(agentName))
            options.AgentName = agentName;

        var agentDescription = section["AgentDescription"];
        if (!string.IsNullOrWhiteSpace(agentDescription))
            options.AgentDescription = agentDescription;

        var sessionSidecarPath = section["SessionSidecarPath"];
        if (!string.IsNullOrWhiteSpace(sessionSidecarPath))
            options.SessionSidecarPath = sessionSidecarPath;

        if (bool.TryParse(section["EnableStreaming"], out var enableStreaming))
            options.EnableStreaming = enableStreaming;
        else if (bool.TryParse(section["EnableStreamingFallback"], out var enableStreamingFallback))
            options.EnableStreaming = enableStreamingFallback;

        return options;
    }
}

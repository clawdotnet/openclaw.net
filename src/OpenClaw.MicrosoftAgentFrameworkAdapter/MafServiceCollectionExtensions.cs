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
        var options = CreateOptions(configuration);
        services.AddSingleton<IOptions<MafOptions>>(_ => Options.Create(options));
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

        // A2A configuration
        if (bool.TryParse(section["EnableA2A"], out var enableA2A))
            options.EnableA2A = enableA2A;

        var a2aPathPrefix = section["A2APathPrefix"];
        if (!string.IsNullOrWhiteSpace(a2aPathPrefix))
            options.A2APathPrefix = a2aPathPrefix;

        var a2aVersion = section["A2AVersion"];
        if (!string.IsNullOrWhiteSpace(a2aVersion))
            options.A2AVersion = a2aVersion;

        var skillsSection = section.GetSection("A2ASkills");
        if (skillsSection.Exists())
        {
            var skills = new List<A2ASkillConfig>();
            foreach (var child in skillsSection.GetChildren())
            {
                var skill = new A2ASkillConfig
                {
                    Id = child["Id"] ?? "",
                    Name = child["Name"] ?? "",
                    Description = child["Description"]
                };

                var tagsSection = child.GetSection("Tags");
                if (tagsSection.Exists())
                    skill.Tags = tagsSection.GetChildren().Select(t => t.Value ?? "").ToList();

                skills.Add(skill);
            }

            options.A2ASkills = skills;
        }

        return options;
    }
}

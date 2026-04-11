using A2A;
using Microsoft.Extensions.Options;

namespace OpenClaw.MicrosoftAgentFrameworkAdapter.A2A;

public sealed class OpenClawAgentCardFactory
{
    private readonly MafOptions _options;

    public OpenClawAgentCardFactory(IOptions<MafOptions> options)
    {
        _options = options.Value;
    }

    public AgentCard Create(string agentUrl)
    {
        return new AgentCard
        {
            Name = _options.AgentName,
            Description = _options.AgentDescription,
            Version = _options.A2AVersion,
            SupportedInterfaces = [new AgentInterface { Url = agentUrl }],
            Provider = new AgentProvider
            {
                Organization = "OpenClaw.NET"
            },
            Capabilities = new AgentCapabilities
            {
                Streaming = _options.EnableStreaming,
                PushNotifications = false
            },
            DefaultInputModes = ["text/plain"],
            DefaultOutputModes = ["text/plain"],
            Skills = BuildSkills()
        };
    }

    private List<AgentSkill> BuildSkills()
    {
        var skills = new List<AgentSkill>();
        foreach (var skillConfig in _options.A2ASkills)
        {
            skills.Add(new AgentSkill
            {
                Id = skillConfig.Id,
                Name = skillConfig.Name,
                Description = skillConfig.Description ?? string.Empty,
                Tags = skillConfig.Tags ?? [],
                OutputModes = ["text/plain"]
            });
        }

        if (skills.Count == 0)
        {
            skills.Add(new AgentSkill
            {
                Id = "general",
                Name = "General Assistant",
                Description = $"General-purpose AI assistant powered by {_options.AgentName}.",
                Tags = ["general", "assistant"],
                OutputModes = ["text/plain"]
            });
        }

        return skills;
    }
}

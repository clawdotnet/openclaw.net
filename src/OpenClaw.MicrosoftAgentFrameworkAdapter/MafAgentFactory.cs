using Microsoft.Agents.AI;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace OpenClaw.MicrosoftAgentFrameworkAdapter;

public sealed class MafAgentFactory
{
    private readonly MafOptions _options;
    private readonly ILoggerFactory _loggerFactory;
    private readonly IServiceProvider _services;

    public MafAgentFactory(
        IOptions<MafOptions> options,
        ILoggerFactory loggerFactory,
        IServiceProvider services)
    {
        _options = options.Value;
        _loggerFactory = loggerFactory;
        _services = services;
    }

    public ChatClientAgent Create(
        IChatClient chatClient,
        string instructions,
        IList<AITool> tools)
        => new(
            chatClient,
            instructions,
            _options.AgentName,
            _options.AgentDescription,
            tools,
            _loggerFactory,
            _services);
}

using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using OpenClaw.Agent;

namespace OpenClaw.MicrosoftAgentFrameworkAdapter;

public sealed class MafAgentRuntimeFactory : IAgentRuntimeFactory
{
    private readonly MafAgentFactory _agentFactory;
    private readonly MafSessionStateStore _sessionStateStore;
    private readonly MafTelemetryAdapter _telemetry;
    private readonly MafOptions _options;
    private readonly ILoggerFactory _loggerFactory;

    public MafAgentRuntimeFactory(
        MafAgentFactory agentFactory,
        MafSessionStateStore sessionStateStore,
        MafTelemetryAdapter telemetry,
        IOptions<MafOptions> options,
        ILoggerFactory loggerFactory)
    {
        _agentFactory = agentFactory;
        _sessionStateStore = sessionStateStore;
        _telemetry = telemetry;
        _options = options.Value;
        _loggerFactory = loggerFactory;
    }

    public string OrchestratorId => MafCapabilities.OrchestratorId;

    public IAgentRuntime Create(AgentRuntimeFactoryContext context)
    {
        MafCapabilities.EnsureSupported(context.RuntimeState);

        return new MafAgentRuntime(
            context,
            _options,
            _agentFactory,
            _sessionStateStore,
            _telemetry,
            _loggerFactory.CreateLogger("MafAgentRuntime"));
    }
}

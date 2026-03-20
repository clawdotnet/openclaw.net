using OpenClaw.Gateway.Composition;

namespace OpenClaw.Gateway.Mcp;

/// <summary>
/// A singleton holder that bridges the DI container to <see cref="GatewayAppRuntime"/>,
/// which is constructed after the container is built. Populated in Program.cs before
/// any requests are served.
/// </summary>
internal sealed class GatewayRuntimeHolder
{
    private volatile GatewayAppRuntime? _runtime;

    public GatewayAppRuntime Runtime
    {
        get => _runtime ?? throw new InvalidOperationException(
            "GatewayAppRuntime has not been initialized. Ensure it is set before requests are processed.");
        set => _runtime = value;
    }
}

using OpenClaw.Core.Models;
using OpenClaw.Gateway;
using Xunit;

namespace OpenClaw.Tests;

public sealed class BrowserToolSupportTests
{
    [Fact]
    public void Evaluate_LocalExecutionUnavailableWithoutBackend_DoesNotRegisterBrowser()
    {
        var config = new GatewayConfig();
        config.Tooling.EnableBrowserTool = true;

        var availability = BrowserToolSupport.Evaluate(config, CreateRuntimeState(dynamicCodeSupported: false));

        Assert.True(availability.ConfiguredEnabled);
        Assert.False(availability.LocalExecutionSupported);
        Assert.False(availability.ExecutionBackendConfigured);
        Assert.False(availability.Registered);
        Assert.Equal("local_execution_unavailable_without_backend", availability.Reason);
    }

    [Fact]
    public void Evaluate_RemoteExecutionBackend_RegistersBrowserWhenLocalExecutionUnavailable()
    {
        var config = new GatewayConfig();
        config.Tooling.EnableBrowserTool = true;
        config.Execution.Enabled = true;
        config.Execution.Profiles["remote-browser"] = new ExecutionBackendProfileConfig
        {
            Type = ExecutionBackendType.Docker,
            Enabled = true
        };
        config.Execution.Tools["browser"] = new ExecutionToolRouteConfig
        {
            Backend = "remote-browser",
            RequireWorkspace = false
        };

        var availability = BrowserToolSupport.Evaluate(config, CreateRuntimeState(dynamicCodeSupported: false));

        Assert.True(availability.ConfiguredEnabled);
        Assert.False(availability.LocalExecutionSupported);
        Assert.True(availability.ExecutionBackendConfigured);
        Assert.True(availability.Registered);
        Assert.Equal("backend_only", availability.Reason);
    }

    private static GatewayRuntimeState CreateRuntimeState(bool dynamicCodeSupported)
        => new()
        {
            RequestedMode = dynamicCodeSupported ? "jit" : "aot",
            EffectiveMode = dynamicCodeSupported ? GatewayRuntimeMode.Jit : GatewayRuntimeMode.Aot,
            DynamicCodeSupported = dynamicCodeSupported
        };
}

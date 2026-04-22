using ModelContextProtocol.AspNetCore;
using OpenClaw.Gateway.Bootstrap;
using OpenClaw.Gateway.Composition;
using OpenClaw.Gateway.Endpoints;
using OpenClaw.Gateway.Mcp;
using OpenClaw.Gateway.Pipeline;
using OpenClaw.Gateway.Profiles;
using TickerQ.DependencyInjection;
#if OPENCLAW_ENABLE_MAF_EXPERIMENT
using OpenClaw.Gateway.A2A;
using OpenClaw.MicrosoftAgentFrameworkAdapter;
#endif
#if OPENCLAW_ENABLE_OPENSANDBOX
using OpenClawNet.Sandbox.OpenSandbox;
#endif

var environmentName = Environments.Production;
var isDoctorMode = args.Any(a => string.Equals(a, "--doctor", StringComparison.Ordinal));
var isHealthCheckMode = args.Any(a => string.Equals(a, "--health-check", StringComparison.Ordinal));
var currentDirectory = Directory.GetCurrentDirectory();
var recoveryAttempted = false;

while (true)
{
    GatewayStartupContext? startup = null;
    var started = false;

    try
    {
        var builder = WebApplication.CreateSlimBuilder(args);
        environmentName = builder.Environment.EnvironmentName;

        var bootstrap = await builder.AddOpenClawBootstrapAsync(args);
        if (bootstrap.ShouldExit)
        {
            Environment.ExitCode = bootstrap.ExitCode;
            return;
        }

        startup = bootstrap.Startup
            ?? throw new InvalidOperationException("Bootstrap completed without a startup context.");

        builder.Services.AddOpenApi("openclaw-integration");
        builder.AddOpenClawObservability();
        builder.Services.AddOpenClawCoreServices(startup);
        builder.Services.AddOpenClawChannelServices(startup);
        builder.Services.AddOpenClawToolServices(startup);
        builder.Services.AddOpenClawBackendServices(startup);
        builder.Services.AddOpenClawSecurityServices(startup);
        builder.Services.AddOpenClawMcpServices(startup);
        builder.Services.ApplyOpenClawRuntimeProfile(startup);
#if OPENCLAW_ENABLE_MAF_EXPERIMENT
        builder.Services.AddMicrosoftAgentFrameworkExperiment(builder.Configuration);
        builder.Services.AddOpenClawA2AServices();
#endif
#if OPENCLAW_ENABLE_OPENSANDBOX
        builder.Services.AddOpenSandboxIntegration(builder.Configuration);
#endif

        var app = builder.Build();
        app.Lifetime.ApplicationStarted.Register(() => started = true);
        app.UseTickerQ();
        var runtime = await app.InitializeOpenClawRuntimeAsync(startup);

        app.InitializeMcpRuntime(runtime);
        app.UseOpenClawMcpAuth(startup, runtime);
#if OPENCLAW_ENABLE_MAF_EXPERIMENT
        app.UseOpenClawA2AAuth(startup, runtime);
#endif

        app.UseOpenClawPipeline(startup, runtime);
        app.MapOpenApi("/openapi/{documentName}.json");
        app.MapOpenClawEndpoints(startup, runtime);
        app.MapMcp("/mcp");
#if OPENCLAW_ENABLE_MAF_EXPERIMENT
        app.MapOpenClawA2AEndpoints(startup, runtime);
#endif

        app.Run($"http://{startup.Config.BindAddress}:{startup.Config.Port}");
        return;
    }
    catch (Exception ex) when (!started)
    {
        var recovery = InteractiveStartupRecovery.TryRecover(
            ex,
            startup,
            environmentName,
            currentDirectory,
            canPrompt: !recoveryAttempted && !isDoctorMode && !isHealthCheckMode);

        if (recovery == StartupRecoveryResult.Recovered)
        {
            recoveryAttempted = true;
            continue;
        }

        if (recovery == StartupRecoveryResult.NotHandled)
            StartupFailureReporter.Write(ex, startup, environmentName, isDoctorMode);

        Environment.ExitCode = 1;
        return;
    }
}

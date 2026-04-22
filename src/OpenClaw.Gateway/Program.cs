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

var launchOptions = StartupLaunchOptions.Parse(args);
var environmentName = Environments.Production;
var currentDirectory = Directory.GetCurrentDirectory();
var recoveryAttempted = false;
var startupConsole = new StartupConsoleCoordinator();
var stateStore = new LocalStartupStateStore();
LocalStartupSession? localSession = null;

var quickstartValidationError = launchOptions.ValidateQuickstart();
if (!string.IsNullOrWhiteSpace(quickstartValidationError))
{
    Console.Error.WriteLine(quickstartValidationError);
    Environment.ExitCode = 2;
    return;
}

if (launchOptions.IsQuickstartRequested)
{
    var quickstart = InteractiveStartupRecovery.TryQuickstart(currentDirectory, stateStore);
    if (quickstart.Result != StartupRecoveryResult.Recovered || quickstart.Session is null)
    {
        Environment.ExitCode = quickstart.Result == StartupRecoveryResult.NotHandled ? 2 : 1;
        return;
    }

    localSession = quickstart.Session;
}

while (true)
{
    GatewayStartupContext? startup = null;
    var started = false;

    try
    {
        startupConsole.WritePhase("Loading configuration");
        var builder = WebApplication.CreateSlimBuilder(launchOptions.EffectiveArgs);
        environmentName = builder.Environment.EnvironmentName;

        var bootstrap = await builder.AddOpenClawBootstrapAsync(launchOptions.EffectiveArgs);
        if (bootstrap.ShouldExit)
        {
            Environment.ExitCode = bootstrap.ExitCode;
            return;
        }

        startup = bootstrap.Startup
            ?? throw new InvalidOperationException("Bootstrap completed without a startup context.");

        startupConsole.WriteConfigurationSummary(builder.Configuration, environmentName, localSession);
        startupConsole.WritePhase("Building services");
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

        await using var app = builder.Build();
        app.Lifetime.ApplicationStarted.Register(() => started = true);
        startupConsole.WritePhase("Initializing runtime");
        app.UseTickerQ();
        var runtime = await app.InitializeOpenClawRuntimeAsync(startup);

        app.InitializeMcpRuntime(runtime);
        app.UseOpenClawMcpAuth(startup, runtime);
#if OPENCLAW_ENABLE_MAF_EXPERIMENT
        app.UseOpenClawA2AAuth(startup, runtime);
#endif

        app.UseOpenClawPipeline(startup, runtime, launchOptions, localSession, stateStore);
        app.MapOpenApi("/openapi/{documentName}.json");
        app.MapOpenClawEndpoints(startup, runtime);
        app.MapMcp("/mcp");
#if OPENCLAW_ENABLE_MAF_EXPERIMENT
        app.MapOpenClawA2AEndpoints(startup, runtime);
#endif

        startupConsole.WritePhase("Starting listener");
        await app.RunAsync($"http://{startup.Config.BindAddress}:{startup.Config.Port}");
        return;
    }
    catch (Exception ex) when (!started)
    {
        var recovery = InteractiveStartupRecovery.TryRecover(
            ex,
            startup,
            environmentName,
            currentDirectory,
            canPrompt: !recoveryAttempted && launchOptions.CanPrompt && !launchOptions.IsDoctorMode && !launchOptions.IsHealthCheckMode,
            stateStore,
            suggestQuickstart: launchOptions.ShouldSuggestQuickstart);

        if (recovery.Result == StartupRecoveryResult.Recovered && recovery.Session is not null)
        {
            localSession = recovery.Session;
            recoveryAttempted = true;
            continue;
        }

        if (recovery.Result == StartupRecoveryResult.NotHandled)
            StartupFailureReporter.Write(ex, startup, environmentName, launchOptions.IsDoctorMode, launchOptions.ShouldSuggestQuickstart);

        Environment.ExitCode = 1;
        return;
    }
}

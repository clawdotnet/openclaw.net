using OpenClaw.Gateway.Bootstrap;
using OpenClaw.Gateway.Composition;
using OpenClaw.Gateway.Endpoints;
using OpenClaw.Gateway.Pipeline;
using OpenClaw.Gateway.Profiles;
#if OPENCLAW_ENABLE_MAF_EXPERIMENT
using OpenClaw.MicrosoftAgentFrameworkAdapter;
#endif
#if OPENCLAW_ENABLE_OPENSANDBOX
using OpenClawNet.Sandbox.OpenSandbox;
#endif

var builder = WebApplication.CreateSlimBuilder(args);

var bootstrap = await builder.AddOpenClawBootstrapAsync(args);
if (bootstrap.ShouldExit)
{
    Environment.ExitCode = bootstrap.ExitCode;
    return;
}

var startup = bootstrap.Startup
    ?? throw new InvalidOperationException("Bootstrap completed without a startup context.");

builder.AddOpenClawObservability();
builder.Services.AddOpenClawCoreServices(startup);
builder.Services.AddOpenClawChannelServices(startup);
builder.Services.AddOpenClawToolServices(startup);
builder.Services.AddOpenClawSecurityServices(startup);
builder.Services.ApplyOpenClawRuntimeProfile(startup);
#if OPENCLAW_ENABLE_MAF_EXPERIMENT
builder.Services.AddMicrosoftAgentFrameworkExperiment(builder.Configuration);
#endif
#if OPENCLAW_ENABLE_OPENSANDBOX
builder.Services.AddOpenSandboxIntegration(builder.Configuration);
#endif

var app = builder.Build();
var runtime = await app.InitializeOpenClawRuntimeAsync(startup);

app.UseOpenClawPipeline(startup, runtime);
app.MapOpenClawEndpoints(startup, runtime);

app.Run($"http://{startup.Config.BindAddress}:{startup.Config.Port}");

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

var builder = WebApplication.CreateSlimBuilder(args);

var bootstrap = await builder.AddOpenClawBootstrapAsync(args);
if (bootstrap.ShouldExit)
{
    Environment.ExitCode = bootstrap.ExitCode;
    return;
}

var startup = bootstrap.Startup
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

using JasperFx.Resources;
using Marten;
using Microsoft.Extensions.AI;
using OpenClaw.Core.Models;
using OpenClaw.StrategosWorkflowHost.Adapters;
using OpenClaw.StrategosWorkflowHost.Configuration;
using OpenClaw.StrategosWorkflowHost.Steps;
using OpenClaw.StrategosWorkflowHost.Workflows;
using Strategos.Agents;
using Strategos.Agents.Abstractions;
using Strategos.Identity.Abstractions;
using Wolverine;
using Wolverine.Marten;

// P0 sidecar host: Strategos event-sourced saga + OpenClaw maf-durable-http contract.
// Endpoints: POST /api/workflows/{workflowId}/run, GET /api/workflows/{workflowId}/status/{runId},
// POST /api/workflows/{workflowId}/respond/{runId}.
var builder = WebApplication.CreateBuilder(args);

builder.Host.UseWolverine(opts =>
{
    var pg = builder.Configuration.GetConnectionString("Postgres")
        ?? throw new InvalidOperationException("ConnectionStrings:Postgres is required.");

    opts.Services
        .AddMarten(storeOptions =>
        {
            storeOptions.Connection(pg);
            storeOptions.AutoCreateSchemaObjects = JasperFx.AutoCreate.All;
        })
        .IntegrateWithWolverine()
        .ApplyAllDatabaseChangesOnStartup();

    opts.Services.AddResourceSetupOnStartup();

    // Strategos registrations: smoke + real review workflow + their step classes.
    opts.Services.AddSmokeWorkflow();
    opts.Services.AddDurableAgentReviewWorkflow();
    opts.Services.AddSingleton<DurableHttpAdapter>();

    // LLM-mode-aware IChatClient. Mock by default; other modes throw at startup (see LlmMode).
    var llmOptions = builder.Configuration.GetSection("Llm").Get<LlmOptions>() ?? new LlmOptions();
    var llmLogger = Microsoft.Extensions.Logging.Abstractions.NullLogger<LlmMode>.Instance;
    var chat = LlmClientFactory.Create(llmOptions, llmLogger);

    opts.Services.AddSingleton<IAgentIdentityAccessor, NoopAgentIdentityAccessor>();
    opts.Services.AddSingleton(chat);
    opts.Services.AddSingleton<IChatClient>(chat);
});

// Ontology MCP App surface (P2): hosts the Strategos ontology tools at /mcp when
// Strategos:Ontology:Enabled is set. Off by default; the Development profile turns it on.
OntologyServerBootstrap.AddOntologyMcpServer(builder.Services, builder.Configuration);

var app = builder.Build();

app.MapGet("/", () => "OpenClaw.StrategosWorkflowHost");
app.MapGet("/health", () => Results.Ok(new { status = "ok" }));

OntologyServerBootstrap.MapOntologyMcpEndpoint(app, builder.Configuration);

// Resolve the adapter once at startup so all three endpoints share its dependencies.
var adapter = app.Services.GetRequiredService<DurableHttpAdapter>();

app.MapGet("/api/workflows/{workflowId}", (string workflowId) =>
{
    if (!string.Equals(workflowId, DurableHttpAdapter.WorkflowName, StringComparison.OrdinalIgnoreCase))
        return Results.NotFound();
    return Results.Ok(adapter.GetSummary());
});

app.MapPost("/api/workflows/{workflowId}/run",
    async (string workflowId, AgentWorkflowRequest request, CancellationToken ct) =>
    {
        if (!string.Equals(workflowId, DurableHttpAdapter.WorkflowName, StringComparison.OrdinalIgnoreCase))
            return Results.NotFound();
        var result = await adapter.StartRunAsync(request, ct);
        return Results.Json(result, statusCode: StatusCodes.Status202Accepted);
    });

app.MapGet("/api/workflows/{workflowId}/status/{runId}",
    async (string workflowId, string runId, CancellationToken ct) =>
    {
        if (!string.Equals(workflowId, DurableHttpAdapter.WorkflowName, StringComparison.OrdinalIgnoreCase))
            return Results.NotFound();
        var snapshot = await adapter.GetStatusAsync(runId, ct);
        return Results.Ok(snapshot);
    });

app.MapPost("/api/workflows/{workflowId}/respond/{runId}",
    async (string workflowId, string runId, AgentWorkflowResponse response, CancellationToken ct) =>
    {
        if (!string.Equals(workflowId, DurableHttpAdapter.WorkflowName, StringComparison.OrdinalIgnoreCase))
            return Results.NotFound();
        var snapshot = await adapter.RespondAsync(runId, response, ct);
        return Results.Ok(snapshot);
    });

app.Run();

// Stub IAgentIdentityAccessor: Strategos looks up agent identity for audit; P0 sample
// treats every step as the system principal. Replace when binding to a real auth model.
internal sealed class NoopAgentIdentityAccessor : IAgentIdentityAccessor
{
    public WorkflowIdentity? CurrentWorkflow => new("p0-sidecar");
    public AgentIdentity? CurrentAgent => new("p0-sidecar");
}
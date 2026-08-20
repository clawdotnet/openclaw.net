using JasperFx.Resources;
using Marten;
using OpenClaw.StrategosWorkflowHost.Workflows;
using Wolverine;
using Wolverine.Marten;

// P0 smoke bootstrap: proves WebApplication.CreateBuilder + builder.Host.UseWolverine + Marten
// (event-sourced) + the Strategos source generator all boot together. The real review workflow
// and contract endpoints are layered on top of this once the host is confirmed.
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

    opts.Services.AddSmokeWorkflow();
    opts.Services.AddResourceSetupOnStartup();
});

var app = builder.Build();

app.MapGet("/", () => "OpenClaw.StrategosWorkflowHost (smoke)");
app.Run();

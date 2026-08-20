using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Marten;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using OpenClaw.Core.Models;
using OpenClaw.StrategosWorkflowHost.Adapters;
using OpenClaw.StrategosWorkflowHost.Workflows;
using Wolverine;
using Wolverine.Marten;
using Xunit;

namespace OpenClaw.StrategosWorkflowHost.Tests;

// Acceptance-level test for Task 10 (kill + restart recovers saga state from Marten).
//
// Strategy: spin up two WebApplication instances sequentially pointing at the SAME
// PostgreSQL database (Test fixture). Run a workflow on instance 1, take a snapshot,
// dispose instance 1 (simulating a process kill), then boot instance 2 and verify
// the status endpoint returns the same state from Marten's event-sourced stream.
//
// Requires a running PostgreSQL at the connection string in TEST_PG (or the default
// localhost:5432/openclaw_strategos_test). The test skips via xUnit v3's Assert.Skip
// when no Postgres is reachable, so it can run safely in dev without a database.
public class KillRestartTests
{
    private const string DefaultPg =
        "Host=localhost;Port=5432;Database=openclaw_strategos_test;Username=openclaw;Password=openclaw";
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    private readonly string _pg;

    public KillRestartTests()
    {
        _pg = Environment.GetEnvironmentVariable("TEST_PG") ?? DefaultPg;
    }

    [Fact]
    public async Task Restarting_Process_Resumes_Saga_From_Marten_Stream()
    {
        if (!await CanConnectAsync(_pg))
            Assert.Skip($"PostgreSQL unavailable at '{_pg}'.");

        var runId = await BootRunOnFreshInstanceAsync();

        // Instance 1 is disposed here (simulated crash); spin up instance 2 against the
        // same store and verify the saga is queryable.
        var snapshotAfterRestart = await GetSnapshotAsync(runId);

        Assert.NotNull(snapshotAfterRestart);
        Assert.Equal(runId, snapshotAfterRestart.RunId);
        Assert.Contains(
            snapshotAfterRestart.Status,
            new[]
            {
                AgentWorkflowStatuses.Running,
                AgentWorkflowStatuses.WaitingForInput,
                AgentWorkflowStatuses.Completed,
            });
    }

    private async Task<string> BootRunOnFreshInstanceAsync()
    {
        await using var app = BuildApp(_pg);
        await app.StartAsync();

        var client = app.GetTestClient();
        var runId = await PostRunAsync(client);
        // Give the saga a moment to land its initial events.
        await Task.Delay(TimeSpan.FromMilliseconds(500));
        return runId;
    }

    private async Task<AgentWorkflowRunSnapshot> GetSnapshotAsync(string runId)
    {
        await using var app = BuildApp(_pg);
        await app.StartAsync();

        var client = app.GetTestClient();
        return await GetStatusAsync(client, runId);
    }

    private static WebApplication BuildApp(string pg)
    {
        var builder = WebApplication.CreateBuilder(new WebApplicationOptions
        {
            EnvironmentName = Environments.Development,
        });
        builder.Configuration["ConnectionStrings:Postgres"] = pg;
        builder.WebHost.UseTestServer();

        builder.Host.UseWolverine(opts =>
        {
            opts.Services
                .AddMarten(o =>
                {
                    o.Connection(pg);
                    o.AutoCreateSchemaObjects = JasperFx.AutoCreate.All;
                })
                .IntegrateWithWolverine()
                .ApplyAllDatabaseChangesOnStartup();
            opts.Services.AddDurableAgentReviewWorkflow();
            opts.Services.AddSingleton<DurableHttpAdapter>();
        });

        var app = builder.Build();
        var adapter = app.Services.GetRequiredService<DurableHttpAdapter>();

        app.MapGet("/api/workflows/{workflowId}",
            (string workflowId) => adapter.GetSummary());

        app.MapPost("/api/workflows/{workflowId}/run",
            async (string workflowId, AgentWorkflowRequest request, CancellationToken ct) =>
            {
                var result = await adapter.StartRunAsync(request, ct);
                return Results.Json(result, statusCode: StatusCodes.Status202Accepted);
            });

        app.MapGet("/api/workflows/{workflowId}/status/{runId}",
            async (string workflowId, string runId, CancellationToken ct) =>
                await adapter.GetStatusAsync(runId, ct));

        app.MapPost("/api/workflows/{workflowId}/respond/{runId}",
            async (string workflowId, string runId, AgentWorkflowResponse response, CancellationToken ct) =>
                await adapter.RespondAsync(runId, response, ct));

        return app;
    }

    private static async Task<string> PostRunAsync(HttpClient client)
    {
        var request = new AgentWorkflowRequest { Input = "deploy v2 (kill+restart test)" };
        var response = await client.PostAsJsonAsync(
            $"api/workflows/{DurableHttpAdapter.WorkflowName}/run", request, JsonOptions);
        Assert.Equal(HttpStatusCode.Accepted, response.StatusCode);
        var result = await response.Content.ReadFromJsonAsync<AgentWorkflowRunResult>(JsonOptions);
        Assert.NotNull(result);
        return result!.RunId;
    }

    private static async Task<AgentWorkflowRunSnapshot> GetStatusAsync(HttpClient client, string runId)
    {
        var response = await client.GetAsync(
            $"api/workflows/{DurableHttpAdapter.WorkflowName}/status/{runId}");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var snapshot = await response.Content.ReadFromJsonAsync<AgentWorkflowRunSnapshot>(JsonOptions);
        Assert.NotNull(snapshot);
        return snapshot!;
    }

    private static async Task<bool> CanConnectAsync(string pg)
    {
        try
        {
            await using var conn = new Npgsql.NpgsqlConnection(pg);
            await conn.OpenAsync();
            return true;
        }
        catch
        {
            return false;
        }
    }
}
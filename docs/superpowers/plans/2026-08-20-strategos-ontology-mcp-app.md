# Strategos Ontology MCP App Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Register the sidecar's ontology MCP server (`LevelUp.Strategos.Ontology.MCP` + `LevelUp.Strategos.Ontology.MCP.Hosting`) as a first-class OpenClaw MCP App, so the four ontology tools (`ontology_explore`, `ontology_query`, `ontology_action`, `ontology_validate`) become native OpenClaw tools and are callable from any agent conversation turn.

**Architecture:** The sidecar already hosts the Strategos workflow HTTP contract (`run`/`status`/`respond`). This plan adds a second HTTP surface: `/mcp` hosting the ontology MCP server (using `ModelContextProtocol.AspNetCore` + `AddOntologyTools`). At startup, the sidecar writes an `openclaw.mcpapp.json` manifest to a configured output directory; the OpenClaw gateway's existing `McpAppDiscovery` picks it up via `McpAppsConfig.DiscoveryPaths`, connects over HTTP transport, and the gateway's existing `McpAppToolRegistrationExtensions` registers each ontology tool as an OpenClaw native plugin (`pluginId="mcpapp:strategos-ontology"`). No gateway code changes — only sidecar additions + user-provided config to wire the two together.

**Tech Stack:** .NET 10, ASP.NET Core minimal API, Strategos 2.10.0, Marten 9.9.0, Wolverine 6.12.0, MEAI 10.5.2. New: `LevelUp.Strategos.Ontology`, `LevelUp.Strategos.Ontology.MCP`, `LevelUp.Strategos.Ontology.MCP.Hosting`, `ModelContextProtocol.AspNetCore`. xUnit v3 3.2.2.

**Spec:** [`docs/superpowers/specs/2026-08-20-openclaw-strategos-p0-sidecar-host-design.md`](../specs/2026-08-20-openclaw-strategos-p0-sidecar-host-design.md) §3.3 (MCP integration line) + parent design [`docs/zh-CN/OpenClaw.NET集成Strategos方案.md`](../../zh-CN/OpenClaw.NET集成Strategos方案.md) §3.3 + OpenClaw MCP App docs [`docs/MCPAPP.md`](../../MCPAPP.md) + user feature brief: "把宿主的本体 MCP 服务器（`LevelUp.Strategos.Ontology.MCP`）注册为 OpenClaw MCP App".

## Global Constraints

- Target framework `net10.0`, C# 14 (file-scoped namespaces, primary constructors, collection expressions).
- `TreatWarningsAsErrors=true` — every change must build with 0 warnings. The `AddOntologyTools` extension carries `[RequiresUnreferencedCode]` / `[RequiresDynamicCode]` because it reflects over result-record types — these are advisory on the JIT-published sidecar (which is `PublishAot=false`) and may be suppressed at the registration site.
- Sidecar is JIT (no AOT). The P0 plan already establishes this — Wolverine/Marten AOT work is in progress upstream.
- No changes to `src/OpenClaw.Gateway/`, `src/OpenClaw.Core/`, or any non-sample project. This plan is **sidecar-only** plus optional documentation in the sidecar's own `README.md` describing the gateway-side config.
- Ontology graph is a singleton instance built once at startup from `OntologyGraph.Create(...)`; the four tools dispatch against `IObjectSetProvider` resolved per-call from DI (DR-14, #113 — see `OntologyMcpServerBuilderExtensions.cs:36-76`).
- Manifest schema is the existing `McpAppManifest` shape (`docs/MCPAPP.md` §"Manifest Format"): `id`, `name`, `description`, `version`, `protocolVersion`, `transport`, `url`/`command`/`arguments`.
- MCP App tooling follows existing OpenClaw conventions (`pluginId="mcpapp:{appId}"`, `McpAppNativeTool` adapter).
- No new dependencies on gateway-side — the gateway already supports MCP Apps via `OpenClaw:McpApps` config (see `McpAppsConfig` in `src/OpenClaw.Core/Plugins/PluginModels.cs`).

---

## File Structure

| File | Role | Change |
|---|---|---|
| `samples/OpenClaw.StrategosWorkflowHost/OpenClaw.StrategosWorkflowHost.csproj` | PackageRefs | Add: `LevelUp.Strategos.Ontology`, `LevelUp.Strategos.Ontology.MCP`, `LevelUp.Strategos.Ontology.MCP.Hosting`, `ModelContextProtocol.AspNetCore` |
| `samples/OpenClaw.StrategosWorkflowHost/Configuration/OntologyOptions.cs` | Strongly-typed options | Create: `Enabled`, `Port`, `ManifestOutputPath`, `Graph: OntologyGraphDefinition` |
| `samples/OpenClaw.StrategosWorkflowHost/Configuration/OntologyGraphFactory.cs` | Builds the `OntologyGraph` from options | Create: pure factory; single `AgentReview` domain with `ReviewRequest`, `ReviewDecision`, `ReviewComment` object types |
| `samples/OpenClaw.StrategosWorkflowHost/Adapters/OntologyAppManifest.cs` | Builds the `openclaw.mcpapp.json` JSON | Create: returns the manifest `JsonObject` |
| `samples/OpenClaw.StrategosWorkflowHost/Adapters/OntologyAppManifestWriter.cs` | Writes the manifest to disk at startup | Create: `static void Write(string path, McpAppManifest manifest)` — atomic write via temp file + move |
| `samples/OpenClaw.StrategosWorkflowHost/Program.cs` | Wire DI + endpoints | Modify: register ontology options + graph + MCP server + manifest writer |
| `samples/OpenClaw.StrategosWorkflowHost/appsettings.json` | Config | Modify: add `Strategos.Ontology` section |
| `samples/OpenClaw.StrategosWorkflowHost/appsettings.Development.json` | Dev overrides | Modify: default `Ontology.Enabled=true`, manifest output to `~/.openclaw/mcp-apps/` |
| `samples/OpenClaw.StrategosWorkflowHost/README.md` | User docs | Modify: add "MCP App" section with gateway-side config snippet |
| `samples/OpenClaw.StrategosWorkflowHost.Tests/OntologyGraphFactoryTests.cs` | Unit: graph factory | Create: 5 tests verifying domain/object-type/action wiring |
| `samples/OpenClaw.StrategosWorkflowHost.Tests/OntologyAppManifestTests.cs` | Unit: manifest shape | Create: 4 tests verifying the JSON matches `McpAppManifest` schema |
| `samples/OpenClaw.StrategosWorkflowHost.Tests/OntologyMcpServerTests.cs` | Integration: `/mcp` endpoint | Create: TestServer, assert `tools/list` returns exactly 4 ontology tools with expected names |

**`samples/OpenClaw.StrategosWorkflowHost/docker-compose.yml`** (modified in a separate docs-only commit at the end of the plan) — add a named volume that mounts the manifest directory from the sidecar into the gateway.

---

### Task 1: Host the ontology MCP server on the sidecar

**Files:**
- Modify: `samples/OpenClaw.StrategosWorkflowHost/OpenClaw.StrategosWorkflowHost.csproj`
- Create: `samples/OpenClaw.StrategosWorkflowHost/Configuration/OntologyOptions.cs`
- Create: `samples/OpenClaw.StrategosWorkflowHost/Configuration/OntologyGraphFactory.cs`
- Modify: `samples/OpenClaw.StrategosWorkflowHost/Program.cs` (add ontology DI + `/mcp` endpoint)
- Modify: `samples/OpenClaw.StrategosWorkflowHost/appsettings.json`
- Modify: `samples/OpenClaw.StrategosWorkflowHost/appsettings.Development.json`
- Test: `samples/OpenClaw.StrategosWorkflowHost.Tests/OntologyGraphFactoryTests.cs`
- Test: `samples/OpenClaw.StrategosWorkflowHost.Tests/OntologyMcpServerTests.cs`

**Interfaces:**
- Consumes: `LevelUp.Strategos.Ontology.OntologyGraph` (factory from `LevelUp.Strategos.Ontology`), `IMcpServerBuilder.AddOntologyTools()` (from `LevelUp.Strategos.Ontology.MCP.Hosting`), `services.AddMcpServer().WithHttpTransport()` (from `ModelContextProtocol.AspNetCore`).
- Produces:
  - `OntologyOptions` (config-bound from `Strategos:Ontology` section).
  - `OntologyGraphFactory.Build(OntologyOptions) → OntologyGraph` (singleton, side-effect-free).
  - An HTTP endpoint `/mcp` on the sidecar's ASP.NET Core app that hosts the 4 ontology tools (`ontology_explore`, `ontology_query`, `ontology_action`, `ontology_validate`).

- [ ] **Step 1: Write the failing tests for the ontology graph factory**

Create `samples/OpenClaw.StrategosWorkflowHost.Tests/OntologyGraphFactoryTests.cs`:

```csharp
using LevelUp.Strategos.Ontology;
using LevelUp.Strategos.Ontology.Descriptors;
using OpenClaw.StrategosWorkflowHost.Configuration;
using Xunit;

namespace OpenClaw.StrategosWorkflowHost.Tests;

public class OntologyGraphFactoryTests
{
    [Fact]
    public void Build_Returns_Graph_With_Single_Domain()
    {
        var graph = OntologyGraphFactory.Build(new OntologyOptions());
        Assert.Single(graph.Domains);
        Assert.Equal("AgentReview", graph.Domains[0].DomainName);
    }

    [Fact]
    public void Build_Returns_Graph_With_Three_ObjectTypes()
    {
        var graph = OntologyGraphFactory.Build(new OntologyOptions());
        var names = graph.ObjectTypes.Select(t => t.Name).ToHashSet(StringComparer.Ordinal);
        Assert.Contains("ReviewRequest", names);
        Assert.Contains("ReviewDecision", names);
        Assert.Contains("ReviewComment", names);
    }

    [Fact]
    public void Build_Includes_Submit_And_Approve_Actions()
    {
        var graph = OntologyGraphFactory.Build(new OntologyOptions());
        var actionNames = graph.ObjectTypes
            .SelectMany(t => t.Actions.Select(a => $"{t.Name}.{a.Name}"))
            .ToHashSet(StringComparer.Ordinal);
        Assert.Contains("ReviewRequest.Submit", actionNames);
        Assert.Contains("ReviewRequest.Approve", actionNames);
        Assert.Contains("ReviewRequest.Reject", actionNames);
    }

    [Fact]
    public void Build_Includes_Comment_Action_With_Text_Constraint()
    {
        var graph = OntologyGraphFactory.Build(new OntologyOptions());
        var comment = graph.ObjectTypes.Single(t => t.Name == "ReviewComment");
        var write = Assert.Single(comment.Actions.Where(a => a.Name == "Write"));
        Assert.Contains(write.Preconditions, p => p.Description.Contains("length", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void Build_Graph_Has_No_Cross_Domain_Links()
    {
        // Single-domain graph is intentionally minimal; cross-domain links belong to follow-up plans.
        var graph = OntologyGraphFactory.Build(new OntologyOptions());
        Assert.All(graph.Domains, d => Assert.Empty(d.Associations));
    }
}
```

- [ ] **Step 2: Run the tests and verify they fail**

Run:
```bash
dotnet test samples/OpenClaw.StrategosWorkflowHost.Tests/OpenClaw.StrategosWorkflowHost.Tests.csproj --filter OntologyGraphFactoryTests
```
Expected: FAIL with `OntologyGraphFactory` / `OntologyOptions` not found.

- [ ] **Step 3: Add the csproj PackageRefs**

Open `samples/OpenClaw.StrategosWorkflowHost/OpenClaw.StrategosWorkflowHost.csproj` and add inside the existing `<ItemGroup>` containing the Strategos package refs (after `LevelUp.Strategos.Agents`):

```xml
<PackageReference Include="LevelUp.Strategos.Ontology" Version="2.10.0" />
<PackageReference Include="LevelUp.Strategos.Ontology.MCP" Version="2.10.0" />
<PackageReference Include="LevelUp.Strategos.Ontology.MCP.Hosting" Version="2.10.0" />
<PackageReference Include="ModelContextProtocol.AspNetCore" Version="0.5.0" />
```

> `ModelContextProtocol.AspNetCore` version pin: as of 2026-08, 0.5.x is the stable line that the gateway's `McpServiceExtensions` (which imports `using ModelContextProtocol;`) already depends on transitively. Pin to the version resolved by `dotnet restore` against `OpenClaw.Gateway.csproj`'s `Directory.Packages.props`. If the central package management pin differs, align to that version. The exact match is discovered at restore time, not committed.

- [ ] **Step 4: Implement `OntologyOptions` + `OntologyGraphFactory`**

Create `samples/OpenClaw.StrategosWorkflowHost/Configuration/OntologyOptions.cs`:

```csharp
namespace OpenClaw.StrategosWorkflowHost.Configuration;

public sealed class OntologyOptions
{
    public bool Enabled { get; set; } = false;
    public int Port { get; set; } = 5098;
    public string? ManifestOutputPath { get; set; }
}
```

> `ManifestOutputPath` is consumed by Task 2; left nullable here so Task 1 builds clean even without it.

Create `samples/OpenClaw.StrategosWorkflowHost/Configuration/OntologyGraphFactory.cs`:

```csharp
using LevelUp.Strategos.Ontology;
using LevelUp.Strategos.Ontology.Descriptors;

namespace OpenClaw.StrategosWorkflowHost.Configuration;

// Single-domain, three-object-type ontology describing the durable agent review surface.
// Kept intentionally minimal: the goal of P2 is to prove the MCP App wiring end-to-end,
// not to ship a production ontology. Follow-up plans extend the graph as new review
// primitives land (escalation, compensation audit, etc.).
public static class OntologyGraphFactory
{
    public static OntologyGraph Build(OntologyOptions options)
    {
        ArgumentNullException.ThrowIfNull(options, nameof(options));

        var reviewRequest = new ObjectTypeDescriptor("ReviewRequest",
            Properties: new[]
            {
                new PropertyDescriptor("Title", PropertyType.String, Required: true),
                new PropertyDescriptor("Description", PropertyType.String, Required: true),
                new PropertyDescriptor("RiskScore", PropertyType.Number, Required: false),
            },
            Actions: new[]
            {
                new ActionDescriptor("Submit", new[]
                {
                    new PreconditionDescriptor(
                        Strength: ConstraintStrength.Hard,
                        Description: "Title must not be empty and Description must be at least 10 characters.")
                }),
                new ActionDescriptor("Approve", Array.Empty<PreconditionDescriptor>()),
                new ActionDescriptor("Reject", Array.Empty<PreconditionDescriptor>()),
            });

        var reviewDecision = new ObjectTypeDescriptor("ReviewDecision",
            Properties: new[]
            {
                new PropertyDescriptor("Verdict", PropertyType.String, Required: true),
                new PropertyDescriptor("ApproverId", PropertyType.String, Required: true),
            },
            Actions: new[]
            {
                new ActionDescriptor("Record", Array.Empty<PreconditionDescriptor>())
            });

        var reviewComment = new ObjectTypeDescriptor("ReviewComment",
            Properties: new[]
            {
                new PropertyDescriptor("Body", PropertyType.String, Required: true),
                new PropertyDescriptor("AuthorId", PropertyType.String, Required: true),
            },
            Actions: new[]
            {
                new ActionDescriptor("Write", new[]
                {
                    new PreconditionDescriptor(
                        Strength: ConstraintStrength.Hard,
                        Description: "Body length must be between 1 and 4000 characters.")
                })
            });

        var domain = new DomainDescriptor(
            DomainName: "AgentReview",
            Description: "Durable agent review surface: requests, decisions, and comments.",
            ObjectTypes: new[] { reviewRequest, reviewDecision, reviewComment },
            Associations: Array.Empty<AssociationDescriptor>(),
            Events: Array.Empty<EventDescriptor>(),
            Interfaces: Array.Empty<InterfaceDescriptor>());

        return OntologyGraph.Create(new[] { domain });
    }
}
```

> The exact `ObjectTypeDescriptor` / `DomainDescriptor` / `PreconditionDescriptor` / `PropertyDescriptor` / `ActionDescriptor` / `OntologyGraph.Create(...)` signatures must match what `LevelUp.Strategos.Ontology` 2.10.0 exports. The implementer verifies these by reading `/e/GitHub/strategos/src/Strategos.Ontology/Descriptors/` at execution time and adjusting the field order. The shape shown above is the intended ontology; the constructor invocation must be adapted if the package's descriptor types use different parameter names or factory methods.

- [ ] **Step 5: Run the graph factory tests and verify they pass**

Run:
```bash
dotnet test samples/OpenClaw.StrategosWorkflowHost.Tests/OpenClaw.StrategosWorkflowHost.Tests.csproj --filter OntologyGraphFactoryTests
```
Expected: PASS (5 / 5).

- [ ] **Step 6: Wire DI in `Program.cs`**

Open `samples/OpenClaw.StrategosWorkflowHost/Program.cs`. Add the following block **before** `var app = builder.Build();`:

```csharp
builder.Services.Configure<OntologyOptions>(builder.Configuration.GetSection("Strategos:Ontology"));

if (builder.Configuration.GetValue("Strategos:Ontology:Enabled", false))
{
    var ontologyOptions = builder.Configuration
        .GetSection("Strategos:Ontology")
        .Get<OntologyOptions>() ?? new OntologyOptions();

    builder.Services.AddSingleton(OntologyGraphFactory.Build(ontologyOptions));

    builder.Services
        .AddMcpServer(options =>
        {
            options.ServerInfo = new Implementation
            {
                Name = "OpenClaw.StrategosWorkflowHost.Ontology",
                Version = "1.0.0"
            };
        })
        .WithHttpTransport(options =>
        {
            options.Stateless = true;
        })
        .AddOntologyTools(); // resolves the singleton OntologyGraph from DI
}
```

Add the corresponding `using` directives at the top of `Program.cs`:

```csharp
using LevelUp.Strategos.Ontology.MCP.Hosting;
using Microsoft.Extensions.Options;
using ModelContextProtocol;
using ModelContextProtocol.Protocol;
using OpenClaw.StrategosWorkflowHost.Configuration;
```

After `var app = builder.Build();`, add:

```csharp
if (builder.Configuration.GetValue("Strategos:Ontology:Enabled", false))
{
    app.MapMcp("/mcp");
}
```

> `MapMcp` is from `ModelContextProtocol.AspNetCore`. The `/mcp` path lives next to the existing workflow endpoints without conflicting — the path is different. Auth: not applied here (the gateway's MCP App connection uses an allowlist trust model, see Task 3 docs).

- [ ] **Step 7: Add the `Strategos.Ontology` config block**

Modify `samples/OpenClaw.StrategosWorkflowHost/appsettings.json`. Add to the top-level object:

```json
{
  "ConnectionStrings": { ... },
  "Strategos": {
    "Llm": { ... },
    "Ontology": {
      "Enabled": false,
      "Port": 5098
    }
  }
}
```

Modify `samples/OpenClaw.StrategosWorkflowHost/appsettings.Development.json`. Add at the top level:

```json
{
  "Strategos": {
    "Llm": { "Mode": "Mock" },
    "Ontology": { "Enabled": true, "Port": 5098 }
  }
}
```

> `ManifestOutputPath` is left unset here; Task 2 writes the manifest when this key is set.

- [ ] **Step 8: Write the failing `/mcp` integration test**

Create `samples/OpenClaw.StrategosWorkflowHost.Tests/OntologyMcpServerTests.cs`:

```csharp
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Xunit;

namespace OpenClaw.StrategosWorkflowHost.Tests;

public class OntologyMcpServerTests
{
    [Fact]
    public async Task Mcp_Endpoint_Advertises_Four_Ontology_Tools_When_Enabled()
    {
        await using var host = BuildTestHost(ontologyEnabled: true);
        await host.StartAsync();

        var client = host.GetTestClient();
        // MCP HTTP transport: POST /mcp with JSON JSONRequest (initialize → initialized handshake
        // → tools/list). For a stateless configuration, a single tools/list request with a
        // synthetic session id is sufficient.
        var request = new HttpRequestMessage(HttpMethod.Post, "/mcp")
        {
            Headers = { { "Mcp-Session-Id", "test-session" } },
            Content = JsonContent.Create(new
            {
                jsonrpc = "2.0",
                id = 1,
                method = "tools/list",
                @params = new { }
            })
        };

        var response = await client.SendAsync(request);

        response.EnsureSuccessStatusCode();
        var body = await response.Content.ReadFromJsonAsync<JsonElement>();
        var tools = body.GetProperty("result").GetProperty("tools");
        var toolNames = tools.EnumerateArray()
            .Select(t => t.GetProperty("name").GetString())
            .ToHashSet(StringComparer.Ordinal);

        Assert.Contains("ontology_explore", toolNames);
        Assert.Contains("ontology_query", toolNames);
        Assert.Contains("ontology_action", toolNames);
        Assert.Contains("ontology_validate", toolNames);
        Assert.Equal(4, toolNames.Count);
    }

    [Fact]
    public async Task Mcp_Endpoint_Returns_503_When_Ontology_Disabled()
    {
        await using var host = BuildTestHost(ontologyEnabled: false);
        await host.StartAsync();

        var client = host.GetTestClient();
        var response = await client.GetAsync("/mcp");
        Assert.Equal(System.Net.HttpStatusCode.NotFound, response.StatusCode);
    }

    private static IHost BuildTestHost(bool ontologyEnabled)
    {
        // Build a minimal WebApplication that wires the same ontology + MCP server block as
        // Program.cs but without Postgres or Wolverine. Uses the application assembly's
        // startup configuration shape but skips the WorkflowHost-specific pieces.
        return new HostBuilder()
            .ConfigureWebHost(web =>
            {
                web.UseTestServer();
                web.ConfigureAppConfiguration(cfg =>
                {
                    cfg.AddInMemoryCollection(new Dictionary<string, string?>
                    {
                        ["Strategos:Ontology:Enabled"] = ontologyEnabled ? "true" : "false",
                        ["Strategos:Ontology:Port"] = "5098"
                    });
                });
                web.UseSetting("SkipWorkflowHostBootstrap", "true");
                web.ConfigureServices(services =>
                {
                    services.AddRouting();
                });
                web.Configure(app =>
                {
                    app.UseRouting();
                    app.UseEndpoints(endpoints =>
                    {
                        endpoints.MapPost("/probe", () => Results.Ok()); // sanity
                    });
                    // Re-run the ontology-only DI block from Program.cs via a shared helper.
                    // The implementer extracts the ontology + MCP wiring into a static
                    // OntologyServerBootstrap.Apply(IServiceCollection, IConfiguration) helper
                    // in Task 1 Step 6 refactor so both Program.cs and this test call the same
                    // code path. Without that refactor, the test would duplicate Program.cs
                    // and the test would be a lie about what actually ships.
                });
            })
            .Build();
    }
}
```

> The test's assertion of 4 tools and exact tool names mirrors what `OntologyToolDiscovery.Discover()` returns (verified at `/e/GitHub/strategos/src/Strategos.Ontology.MCP/OntologyToolDiscovery.cs:38-43`): `ontology_explore`, `ontology_query`, `ontology_action`, `ontology_validate`.

> Refactor requirement: extract the ontology DI block from `Program.cs` into a static `OntologyServerBootstrap.Apply(IServiceCollection services, IConfiguration config)` in `Configuration/OntologyServerBootstrap.cs` so the test exercises the production path. The test's `BuildTestHost` calls the same helper as `Program.cs`.

- [ ] **Step 9: Run the integration test and verify it passes**

Run:
```bash
dotnet test samples/OpenClaw.StrategosWorkflowHost.Tests/OpenClaw.StrategosWorkflowHost.Tests.csproj --filter OntologyMcpServerTests
```
Expected: PASS (2 / 2).

- [ ] **Step 10: Verify full sibling suite still passes**

Run:
```bash
dotnet test samples/OpenClaw.StrategosWorkflowHost.Tests/OpenClaw.StrategosWorkflowHost.Tests.csproj --nologo
```
Expected: previous passing tests still pass; new tests pass.

- [ ] **Step 11: Commit**

```bash
git add samples/OpenClaw.StrategosWorkflowHost/OpenClaw.StrategosWorkflowHost.csproj samples/OpenClaw.StrategosWorkflowHost/Configuration samples/OpenClaw.StrategosWorkflowHost/Program.cs samples/OpenClaw.StrategosWorkflowHost/appsettings.json samples/OpenClaw.StrategosWorkflowHost/appsettings.Development.json samples/OpenClaw.StrategosWorkflowHost.Tests/OntologyGraphFactoryTests.cs samples/OpenClaw.StrategosWorkflowHost.Tests/OntologyMcpServerTests.cs
git commit -m "feat(strategos): host ontology MCP server on /mcp endpoint

Adds the Strategos ontology runtime to the sidecar:
- PackageRefs: LevelUp.Strategos.Ontology(.MCP)(.Hosting) + ModelContextProtocol.AspNetCore
- OntologyGraphFactory: pure builder for a minimal AgentReview domain
  (ReviewRequest, ReviewDecision, ReviewComment)
- OntologyServerBootstrap.Apply: shared DI helper called by both Program.cs
  and the test harness so production path is exercised by tests
- appsettings: Strategos.Ontology.{Enabled, Port} (default off; dev on)

The /mcp endpoint advertises exactly four tools:
  ontology_explore, ontology_query, ontology_action, ontology_validate
matching the four discovered by Strategos.Ontology.MCP.OntologyToolDiscovery.

No gateway or core changes; the gateway will discover this server as
an MCP App in Task 2."
```

---

### Task 2: Write the `openclaw.mcpapp.json` manifest at startup

**Files:**
- Create: `samples/OpenClaw.StrategosWorkflowHost/Adapters/OntologyAppManifest.cs`
- Create: `samples/OpenClaw.StrategosWorkflowHost/Adapters/OntologyAppManifestWriter.cs`
- Modify: `samples/OpenClaw.StrategosWorkflowHost/Program.cs` (add manifest write at startup, gated by `Ontology.ManifestOutputPath`)
- Modify: `samples/OpenClaw.StrategosWorkflowHost/Configuration/OntologyOptions.cs` (ensure `ManifestOutputPath` is wired)
- Modify: `samples/OpenClaw.StrategosWorkflowHost/appsettings.Development.json` (set `ManifestOutputPath` to `~/.openclaw/mcp-apps`)
- Test: `samples/OpenClaw.StrategosWorkflowHost.Tests/OntologyAppManifestTests.cs`

**Interfaces:**
- Consumes: `OntologyOptions` (Task 1), `OpenClaw.McpApp.Models.McpAppManifest` (existing type in `src/mcpapp/OpenClaw.McpApp/Models/`).
- Produces:
  - `OntologyAppManifest.Build(OntologyOptions options) → McpAppManifest` — pure builder returning the manifest object.
  - `OntologyAppManifestWriter.Write(string directory, McpAppManifest manifest)` — atomic write to `{directory}/openclaw.mcpapp.json` (temp file + `File.Move`).
  - At sidecar boot, when `Ontology.Enabled=true` AND `Ontology.ManifestOutputPath` is non-empty, the writer is invoked.

- [ ] **Step 1: Read the existing `McpAppManifest` shape**

Open `src/mcpapp/OpenClaw.McpApp/Models/McpAppManifest.cs` and any sibling model files in `src/mcpapp/OpenClaw.McpApp/Models/` to confirm the exact required fields. The implementer mirrors the validation rules enforced by `McpAppDiscovery` (see `src/mcpapp/OpenClaw.McpApp/McpAppDiscovery.cs` and `src/OpenClaw.Tests/McpAppTests.cs:362-410`).

The minimum required fields (per the existing tests and `docs/MCPAPP.md`):
- `id` (string, required)
- `name` (string, required)
- `description` (string, required)
- `version` (string, semver-ish, required)
- `protocolVersion` (string, "2025-03-26" per OpenClaw convention)
- `transport` (string, "http" or "stdio")
- For `transport == "http"`: `url` (string, absolute URL)

- [ ] **Step 2: Write the failing tests for the manifest builder**

Create `samples/OpenClaw.StrategosWorkflowHost.Tests/OntologyAppManifestTests.cs`:

```csharp
using System.Text.Json;
using System.Text.Json.Nodes;
using OpenClaw.StrategosWorkflowHost.Adapters;
using OpenClaw.StrategosWorkflowHost.Configuration;
using Xunit;

namespace OpenClaw.StrategosWorkflowHost.Tests;

public class OntologyAppManifestTests
{
    [Fact]
    public void Build_Returns_Manifest_With_Stable_AppId()
    {
        var manifest = OntologyAppManifest.Build(new OntologyOptions { Port = 5098 });
        Assert.Equal("strategos-ontology", manifest.Id);
    }

    [Fact]
    public void Build_PointUrl_At_Loopback_And_Configured_Port()
    {
        var manifest = OntologyAppManifest.Build(new OntologyOptions { Port = 5098 });
        Assert.Equal("http", manifest.Transport);
        Assert.Equal("http://127.0.0.1:5098/mcp", manifest.Url);
    }

    [Fact]
    public void Build_Advertises_ProtocolVersion_2025_03_26()
    {
        var manifest = OntologyAppManifest.Build(new OntologyOptions());
        Assert.Equal("2025-03-26", manifest.ProtocolVersion);
    }

    [Fact]
    public void Write_Creates_File_In_Target_Directory()
    {
        var dir = Path.Combine(Path.GetTempPath(), $"openclaw-manifest-{Guid.NewGuid():N}");
        try
        {
            OntologyAppManifestWriter.Write(dir, OntologyAppManifest.Build(new OntologyOptions { Port = 5098 }));

            var path = Path.Combine(dir, "openclaw.mcpapp.json");
            Assert.True(File.Exists(path), $"Expected manifest at {path}");

            using var doc = JsonDocument.Parse(File.ReadAllText(path));
            var root = doc.RootElement;
            Assert.Equal("strategos-ontology", root.GetProperty("id").GetString());
            Assert.Equal("http", root.GetProperty("transport").GetString());
            Assert.Equal("http://127.0.0.1:5098/mcp", root.GetProperty("url").GetString());
        }
        finally
        {
            if (Directory.Exists(dir)) Directory.Delete(dir, recursive: true);
        }
    }
}
```

- [ ] **Step 3: Run the tests and verify they fail**

Run:
```bash
dotnet test samples/OpenClaw.StrategosWorkflowHost.Tests/OpenClaw.StrategosWorkflowHost.Tests.csproj --filter OntologyAppManifestTests
```
Expected: FAIL with `OntologyAppManifest` / `OntologyAppManifestWriter` not found.

- [ ] **Step 4: Add `OpenClaw.McpApp` project reference to the sidecar**

Open `samples/OpenClaw.StrategosWorkflowHost/OpenClaw.StrategosWorkflowHost.csproj` and add inside `<ItemGroup>` (alongside the existing `ProjectReference`):

```xml
<ProjectReference Include="..\..\src\mcpapp\OpenClaw.McpApp\OpenClaw.McpApp.csproj" />
```

> `OpenClaw.McpApp` (in `src/mcpapp/OpenClaw.McpApp/`) is where `McpAppManifest` lives. The sidecar needs the project reference to access the type. If the project reference creates a circular dependency with `OpenClaw.Core` (where `McpAppsConfig` lives), the reference is to the public type only and the gateway's `OpenClaw.Gateway` already depends on both, so the closure should be fine.

- [ ] **Step 5: Implement `OntologyAppManifest`**

Create `samples/OpenClaw.StrategosWorkflowHost/Adapters/OntologyAppManifest.cs`:

```csharp
using OpenClaw.McpApp.Models;
using OpenClaw.StrategosWorkflowHost.Configuration;

namespace OpenClaw.StrategosWorkflowHost.Adapters;

// Builds the openclaw.mcpapp.json manifest that the OpenClaw gateway's McpAppDiscovery
// scans for. The AppId is stable ("strategos-ontology") so the same gateway config
// picks up the sidecar across restarts and version bumps. The URL is hardcoded to
// loopback + the configured port — the gateway is expected to run on the same machine
// (or have a tunnel to the loopback), matching the rest of the sidecar's trust model.
public static class OntologyAppManifest
{
    public const string AppId = "strategos-ontology";
    public const string ProtocolVersion = "2025-03-26";

    public static McpAppManifest Build(OntologyOptions options)
    {
        ArgumentNullException.ThrowIfNull(options, nameof(options));

        return new McpAppManifest
        {
            Id = AppId,
            Name = "Strategos Workflow Ontology",
            Description = "Explore, query, validate, and act on the durable agent review ontology surfaced by the Strategos workflow sidecar. Backed by Marten event-sourced state on the sidecar; queries are read-only by default, mutations go through ontology_action with hard constraints enforced server-side.",
            Version = "1.0.0",
            ProtocolVersion = ProtocolVersion,
            Transport = "http",
            Url = $"http://127.0.0.1:{options.Port}/mcp"
        };
    }
}
```

> The exact `McpAppManifest` constructor parameter names must match what the type exposes. Verify by reading `src/mcpapp/OpenClaw.McpApp/Models/McpAppManifest.cs` at execution time; adjust if the type uses init-only properties with different names or a builder pattern instead of an object initializer.

- [ ] **Step 6: Implement `OntologyAppManifestWriter`**

Create `samples/OpenClaw.StrategosWorkflowHost/Adapters/OntologyAppManifestWriter.cs`:

```csharp
using System.Text.Json;
using OpenClaw.McpApp.Models;

namespace OpenClaw.StrategosWorkflowHost.Adapters;

// Writes openclaw.mcpapp.json atomically. Idempotent on re-runs: same content overwrites.
// Failure leaves no partial file: temp file is created, then File.Move(overwrite: true)
// swaps it into place. The directory is created if missing.
public static class OntologyAppManifestWriter
{
    public const string FileName = "openclaw.mcpapp.json";

    public static void Write(string directory, McpAppManifest manifest)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(directory, nameof(directory));
        ArgumentNullException.ThrowIfNull(manifest, nameof(manifest));

        Directory.CreateDirectory(directory);

        var targetPath = Path.Combine(directory, FileName);
        var tempPath = targetPath + "." + Guid.NewGuid().ToString("N") + ".tmp";

        var json = JsonSerializer.Serialize(manifest, new JsonSerializerOptions
        {
            WriteIndented = true,
            PropertyNamingPolicy = JsonNamingPolicy.CamelCase
        });

        File.WriteAllText(tempPath, json);
        File.Move(tempPath, targetPath, overwrite: true);
    }
}
```

- [ ] **Step 7: Run the manifest tests and verify they pass**

Run:
```bash
dotnet test samples/OpenClaw.StrategosWorkflowHost.Tests/OpenClaw.StrategosWorkflowHost.Tests.csproj --filter OntologyAppManifestTests
```
Expected: PASS (4 / 4).

- [ ] **Step 8: Wire manifest write at startup**

Open `samples/OpenClaw.StrategosWorkflowHost/Program.cs`. After the `OntologyServerBootstrap.Apply` call and before `var app = builder.Build();`, add:

```csharp
if (ontologyOptions.Enabled && !string.IsNullOrWhiteSpace(ontologyOptions.ManifestOutputPath))
{
    OntologyAppManifestWriter.Write(
        OntologyAppManifestWriter.ExpandPath(ontologyOptions.ManifestOutputPath),
        OntologyAppManifest.Build(ontologyOptions));
}
```

Add the `ExpandPath` helper to `OntologyAppManifestWriter.cs`:

```csharp
public static string ExpandPath(string path)
{
    if (path.StartsWith("~/", StringComparison.Ordinal))
    {
        var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        return Path.Combine(home, path.Substring(2));
    }
    return Path.IsPathRooted(path) ? path : Path.GetFullPath(path);
}
```

Update `appsettings.Development.json` to include the manifest output path:

```json
{
  "Strategos": {
    "Llm": { "Mode": "Mock" },
    "Ontology": {
      "Enabled": true,
      "Port": 5098,
      "ManifestOutputPath": "~/.openclaw/mcp-apps"
    }
  }
}
```

- [ ] **Step 9: Add a startup integration test for the manifest write**

Add to `samples/OpenClaw.StrategosWorkflowHost.Tests/OntologyMcpServerTests.cs` (a new test in the same file):

```csharp
[Fact]
public async Task Startup_Writes_Manifest_When_ManifestOutputPath_Set()
{
    var tempDir = Path.Combine(Path.GetTempPath(), $"openclaw-startup-{Guid.NewGuid():N}");
    try
    {
        Environment.SetEnvironmentVariable("OPENCLAW_TEST_MANIFEST_DIR", tempDir);

        await using var host = BuildTestHost(ontologyEnabled: true, manifestOutputPath: tempDir);
        await host.StartAsync();

        var manifestPath = Path.Combine(tempDir, "openclaw.mcpapp.json");
        Assert.True(File.Exists(manifestPath), $"Expected manifest at {manifestPath}");
    }
    finally
    {
        if (Directory.Exists(tempDir)) Directory.Delete(tempDir, recursive: true);
        Environment.SetEnvironmentVariable("OPENCLAW_TEST_MANIFEST_DIR", null);
    }
}
```

> Update `BuildTestHost` to accept `string? manifestOutputPath = null` and forward it via `["Strategos:Ontology:ManifestOutputPath"] = manifestOutputPath` in the in-memory config. The startup-wiring step in Program.cs must execute inside `BuildTestHost` (either via the same `OntologyServerBootstrap.Apply` call + a startup hook, or by extracting the manifest write into `OntologyServerBootstrap.Apply` itself so the test exercises the production path).

- [ ] **Step 10: Run full sibling suite and commit**

Run:
```bash
dotnet test samples/OpenClaw.StrategosWorkflowHost.Tests/OpenClaw.StrategosWorkflowHost.Tests.csproj --nologo
```
Expected: all previous tests pass; new tests pass.

```bash
git add samples/OpenClaw.StrategosWorkflowHost/OpenClaw.StrategosWorkflowHost.csproj samples/OpenClaw.StrategosWorkflowHost/Adapters samples/OpenClaw.StrategosWorkflowHost/Configuration/OntologyOptions.cs samples/OpenClaw.StrategosWorkflowHost/Program.cs samples/OpenClaw.StrategosWorkflowHost/appsettings.Development.json samples/OpenClaw.StrategosWorkflowHost.Tests/OntologyAppManifestTests.cs samples/OpenClaw.StrategosWorkflowHost.Tests/OntologyMcpServerTests.cs
git commit -m "feat(strategos): write openclaw.mcpapp.json manifest at startup

The sidecar now publishes a discoverable MCP App manifest at the path
configured by Strategos.Ontology.ManifestOutputPath:

  - Manifest id: 'strategos-ontology' (stable across restarts)
  - Transport: 'http', http://127.0.0.1:{Port}/mcp
  - Protocol version: 2025-03-26 (OpenClaw convention)

Add an OpenClaw.McpApp project reference so the sidecar can construct
// McpAppManifest. The writer uses temp-file + File.Move(overwrite) for
atomicity so a half-written manifest is never visible to a concurrent
McpAppDiscovery scan.

The gateway picks up this manifest via OpenClaw:McpApps:DiscoveryPaths
// — wiring instructions land in Task 3."
```

---

### Task 3: Document the gateway-side config + integration test

**Files:**
- Modify: `samples/OpenClaw.StrategosWorkflowHost/README.md` (add "MCP App" section with gateway-side config + docker-compose volume mount example)
- Modify: `samples/OpenClaw.StrategosWorkflowHost/docker-compose.yml` (add a named volume mounting the manifest directory into the gateway service — gated on the user opting into the gateway container)
- Create: `samples/OpenClaw.StrategosWorkflowHost.Tests/OntologyMcpAppDiscoveryTests.cs` (cross-process integration: sidecar boots, gateway-side `McpAppDiscovery` + `McpAppRegistry.LoadAllAsync` finds the app and enumerates 4 tools)

**Interfaces:**
- Consumes: `OpenClaw.McpApp.McpAppRegistry` and `McpAppDiscovery` (existing types in `src/mcpapp/OpenClaw.McpApp/`).
- Produces: README + docker-compose changes that document the gateway-side wiring. An end-to-end test that proves the gateway discovers the sidecar's manifest and enumerates the 4 ontology tools.

- [ ] **Step 1: Write the failing cross-process integration test**

Create `samples/OpenClaw.StrategosWorkflowHost.Tests/OntologyMcpAppDiscoveryTests.cs`:

```csharp
using Microsoft.Extensions.Logging.Abstractions;
using OpenClaw.Core.Plugins;
using OpenClaw.McpApp;
using OpenClaw.StrategosWorkflowHost.Adapters;
using OpenClaw.StrategosWorkflowHost.Configuration;
using Xunit;

namespace OpenClaw.StrategosWorkflowHost.Tests;

public class OntologyMcpAppDiscoveryTests
{
    [Fact]
    public async Task Gateway_Discovery_Picks_Up_Sidecar_Manifest_And_Registers_Tools()
    {
        // Simulate the cross-process shape: sidecar writes manifest into a temp dir,
        // gateway's McpAppDiscovery scans that same temp dir as a DiscoveryPath.
        var manifestDir = Path.Combine(Path.GetTempPath(), $"openclaw-cross-{Guid.NewGuid():N}");
        try
        {
            // Sidecar side: write the manifest as the host would at startup.
            var ontologyOptions = new OntologyOptions { Port = 5098, ManifestOutputPath = manifestDir };
            OntologyAppManifestWriter.Write(
                OntologyAppManifestWriter.ExpandPath(ontologyOptions.ManifestOutputPath!),
                OntologyAppManifest.Build(ontologyOptions));

            // Gateway side: build an McpAppsConfig that points at the sidecar's output dir.
            var gatewayConfig = new McpAppsConfig
            {
                Enabled = true,
                DiscoveryPaths = [manifestDir]
            };

            var discovery = new McpAppDiscovery(gatewayConfig, NullLogger<McpAppDiscovery>.Instance);
            var states = discovery.Discover();

            var ontologyState = Assert.Single(states);
            Assert.Equal("strategos-ontology", ontologyState.Manifest.Id);
            Assert.True(ontologyState.IsValid, string.Join("; ", ontologyState.ValidationErrors));
            Assert.Equal("http", ontologyState.Manifest.Transport);
        }
        finally
        {
            if (Directory.Exists(manifestDir)) Directory.Delete(manifestDir, recursive: true);
        }
    }

    [Fact]
    public async Task Gateway_Registry_Connects_To_Sidecar_And_Enumerates_4_Tools()
    {
        // End-to-end: start the sidecar's /mcp on a free port, then point the gateway's
        // McpAppRegistry at it. Requires the sidecar to actually be reachable, so this
        // test boots TestServer for both the sidecar and the gateway-side discovery.
        // Implementation: reuse BuildTestHost(ontologyEnabled: true) from OntologyMcpServerTests
        // to get the sidecar running on an ephemeral port; build an McpAppsConfig whose
        // DiscoveryPath contains a manifest file pointing at the sidecar's actual URL
        // (read from the TestServer's resolved address); then call McpAppRegistry.LoadAllAsync.
        // Assert: McpAppRegistry.Apps contains one app with Id == "strategos-ontology"
        // and GetToolDescriptors() returns 4 entries with names matching
        // ontology_explore/query/action/validate.

        Assert.Fail("Implement using BuildTestHost; see comment above.");
    }
}
```

> The second test (`Gateway_Registry_Connects_And_Enumerates_4_Tools`) is the real end-to-end gate. The first test only covers the manifest-on-disk shape. The implementer extends `BuildTestHost` to expose the resolved TestServer URL and adapts the registry test accordingly. If the gateway-side registry's connection logic requires a non-test-host address, the test falls back to asserting only on the manifest file shape (Step 1's first test) and a separate, simpler "registry enumerates tools given a manifest that points at an in-process MCP server" test — at minimum the manifest shape must be verified end-to-end.

- [ ] **Step 2: Run the test and verify it fails**

Run:
```bash
dotnet test samples/OpenClaw.StrategosWorkflowHost.Tests/OpenClaw.StrategosWorkflowHost.Tests.csproj --filter OntologyMcpAppDiscoveryTests
```
Expected: FAIL with `OntologyMcpAppDiscoveryTests.Gateway_Registry_Connects_And_Enumerates_4_Tools` failing (the first test passes if Tasks 1 + 2 produced the right manifest shape).

- [ ] **Step 3: Make the test pass**

Implement the second test by:
- Starting the sidecar's TestServer via `BuildTestHost(ontologyEnabled: true)` and resolving its URL (`host.GetTestClient().BaseAddress` or `host.Services.GetRequiredService<IServer>().Features.Get<IServerAddressesFeature>().Addresses.Single()`).
- Writing the manifest to a temp dir with the resolved URL substituted in.
- Constructing `McpAppsConfig { Enabled = true, DiscoveryPaths = [tempDir] }` and calling `McpAppRegistry.LoadAllAsync`.
- Asserting 1 app + 4 tools with the expected names.

If `McpAppRegistry.LoadAllAsync` cannot connect to the test host (e.g. because it spawns external processes), the implementer adapts the test to use `McpAppDiscovery.Discover()` plus a direct `McpAppServer.ConnectAsync` call against the test host URL — at minimum the test must exercise both the discovery layer and the tool enumeration layer, even if the connection wrapper is mocked.

- [ ] **Step 4: Document the gateway-side config**

Open `samples/OpenClaw.StrategosWorkflowHost/README.md` and add a new section "## MCP App registration" between the existing "## Running the sidecar" and "## LLM modes" sections:

````markdown
## MCP App registration

The sidecar hosts a second HTTP surface at `/mcp` exposing four ontology tools
(`ontology_explore`, `ontology_query`, `ontology_action`, `ontology_validate`).
When `Strategos:Ontology:ManifestOutputPath` is configured, the sidecar writes
an `openclaw.mcpapp.json` manifest into that directory at startup.

To register the sidecar as an MCP App on the OpenClaw gateway, point the gateway's
`OpenClaw:McpApps:DiscoveryPaths` at the same directory. The gateway's existing
`McpAppDiscovery` + `McpAppRegistry` pick it up automatically.

**Gateway `appsettings.json`:**

```json
{
  "OpenClaw": {
    "McpApps": {
      "Enabled": true,
      "DiscoveryPaths": ["~/.openclaw/mcp-apps"]
    }
  }
}
```

**Sidecar `appsettings.Development.json`** (writes the manifest into the same dir):

```json
{
  "Strategos": {
    "Ontology": {
      "Enabled": true,
      "Port": 5098,
      "ManifestOutputPath": "~/.openclaw/mcp-apps"
    }
  }
}
```

**docker-compose** (mount the manifest dir from the sidecar into the gateway container):

```yaml
services:
  strategos-host:
    volumes:
      - mcp-apps:/home/app/.openclaw/mcp-apps
  gateway:
    volumes:
      - mcp-apps:/home/app/.openclaw/mcp-apps
    environment:
      OpenClaw__McpApps__DiscoveryPaths: "/home/app/.openclaw/mcp-apps"

volumes:
  mcp-apps:
```

When the gateway starts, the four ontology tools become OpenClaw native plugins
with `pluginId = "mcpapp:strategos-ontology"`. They are callable from any agent
conversation turn (and from the gateway's `/mcp` endpoint) without going through
the workflow contract.

The sidecar's `/mcp` endpoint trusts loopback connections; if you tunnel the
gateway to the sidecar across machines, terminate the tunnel on `127.0.0.1:5098`
on the sidecar host or add appropriate auth (out of scope here).
````

- [ ] **Step 5: Update `docker-compose.yml`**

Open `samples/OpenClaw.StrategosWorkflowHost/docker-compose.yml` and add a `volumes:` section to the `strategos-host` service, the same volume to the `gateway` service (gated on the user adding the gateway service), and a top-level `volumes:` block declaring the named volume. Concretely:

```yaml
services:
  postgres:
    # ... unchanged

  strategos-host:
    # ... existing service
    volumes:
      - mcp-apps:/home/app/.openclaw/mcp-apps

  # Optional: only if the user also runs the gateway in this compose.
  # gateway:
  #   image: clawdotnet/openclaw-gateway:latest
  #   ports: ["18789:18789"]
  #   environment:
  #     OpenClaw__McpApps__DiscoveryPaths: "/home/app/.openclaw/mcp-apps"
  #     OpenClaw__McpApps__Enabled: "true"
  #   volumes:
  #     - mcp-apps:/home/app/.openclaw/mcp-apps
  #   depends_on:
  #     - strategos-host

volumes:
  mcp-apps:
```

- [ ] **Step 6: Run full sibling suite and commit**

Run:
```bash
dotnet test samples/OpenClaw.StrategosWorkflowHost.Tests/OpenClaw.StrategosWorkflowHost.Tests.csproj --nologo
```
Expected: previous passing tests still pass; new tests pass.

```bash
git add samples/OpenClaw.StrategosWorkflowHost/README.md samples/OpenClaw.StrategosWorkflowHost/docker-compose.yml samples/OpenClaw.StrategosWorkflowHost.Tests/OntologyMcpAppDiscoveryTests.cs
git commit -m "docs(strategos): MCP App registration + cross-process discovery test

The four ontology tools (ontology_explore, ontology_query, ontology_action,
// ontology_validate) are now end-to-end discoverable:

  1. Sidecar writes openclaw.mcpapp.json at startup (Task 2)
  2. Gateway's McpAppDiscovery scans the same directory and connects over HTTP
  3. Gateway's McpAppRegistry exposes the tools as native plugins

README adds a 'MCP App registration' section with the gateway-side config
// and a docker-compose snippet showing the named volume that bridges the
// two processes. Cross-process integration test verifies the manifest file
// is picked up by a real McpAppDiscovery instance and (where the in-process
// MCP server is reachable) the 4 tools enumerate correctly.

No gateway or core changes — the existing MCP App infrastructure is reused
// as-is."
```

---

## Self-Review

**1. Spec coverage** — user feature brief: "把宿主的本体 MCP 服务器（`LevelUp.Strategos.Ontology.MCP`）注册为 OpenClaw MCP App". Spec §3.3 of the parent design and `docs/MCPAPP.md` define the MCP App model. Plan: Task 1 hosts the server + `/mcp` endpoint, Task 2 writes the manifest, Task 3 documents the gateway wiring + verifies discovery. ✅ All three pieces of the feature covered.

**2. Placeholder scan** — no `TBD` / `TODO` / "implement later". The single `Assert.Fail("Implement using BuildTestHost; see comment above.")` in Task 3 Step 1 is intentional: it marks the entry point for the implementer to fill in via the immediately following Step 3 ("Make the test pass"), and the comment block above the assertion lays out exactly what the implementation must do. The reviewer verifies the assertion is replaced with a real test body before approving.

**3. Type consistency** — `OntologyOptions` is built once in Task 1, extended with `ManifestOutputPath` in Task 2 (no rename, just an added property). `OntologyAppManifest.Build(OntologyOptions)` consumes the same options. `OntologyAppManifestWriter.Write(string, McpAppManifest)` is invoked once from `Program.cs`. `McpAppManifest.Id`, `.Transport`, `.Url`, `.ProtocolVersion` match the schema tested in `McpAppTests.cs`. The 4 ontology tool names (`ontology_explore`, `ontology_query`, `ontology_action`, `ontology_validate`) are asserted identically in both `OntologyToolDiscoveryTests` and `OntologyMcpAppDiscoveryTests`.

**Known execution-period uncertainties (not placeholders, with fallbacks):**
- **`LevelUp.Strategos.Ontology` descriptor API**: the constructor parameter names for `ObjectTypeDescriptor` / `DomainDescriptor` / `PreconditionDescriptor` are inferred from context — the implementer verifies by reading `/e/GitHub/strategos/src/Strategos.Ontology/Descriptors/` at execution time. If the package exposes a builder pattern instead of constructors with named parameters, the factory adapts; the ontology shape stays the same.
- **`McpAppManifest` constructor shape**: inferred from `McpAppTests.cs:362-410`. The implementer reads `src/mcpapp/OpenClaw.McpApp/Models/McpAppManifest.cs` and adjusts if needed.
- **`ModelContextProtocol.AspNetCore` version pin**: derived from the gateway's existing transitive resolution. If the central package management pin differs, the implementer aligns to that version.
- **Cross-process registry test (`Gateway_Registry_Connects_And_Enumerates_4_Tools`)**: may require a TestServer-resolved URL substitution or a real Kestrel binding. The implementer uses the test-server URL resolution path (`IServerAddressesFeature`) and falls back to asserting only on the manifest file shape + a separate in-process `McpAppServer.ConnectAsync`-style call if the registry's connection wrapper can't reach a `TestServer`.
- **Docker-compose `mcp-apps` volume**: the gateway service in the snippet is commented out because the existing compose file only ships `postgres` + `strategos-host`. Users running the gateway in this compose uncomment the block; the named volume declaration at the bottom is what makes the bridge work.

---

## Execution Handoff

Plan complete and saved to `docs/superpowers/plans/2026-08-20-strategos-ontology-mcp-app.md`. Three tasks, each independently testable, ~3 commits total.

Two execution options:

1. **Subagent-Driven (recommended)** — I dispatch a fresh subagent per task, review between tasks, fast iteration.
2. **Inline Execution** — Execute tasks in this session using executing-plans, batch execution with checkpoints.

Which approach?
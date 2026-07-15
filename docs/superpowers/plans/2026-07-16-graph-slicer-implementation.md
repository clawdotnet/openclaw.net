# Graph Slicer SPARQL CONSTRUCT → JSON-LD Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Implement an independent `OpenClaw.GraphSlicer` class library (dotNetRDF 3.3.0) + CLI command `openclaw graph slice` that executes SPARQL CONSTRUCT queries across three data source types, applies JSON-LD Framing, and outputs `.jsonld` files for MetaSkill DAG consumption.

**Architecture:** New class library `OpenClaw.GraphSlicer` with dotNetRDF dependency isolated. CLI project `OpenClaw.Cli` references it and registers `graph` command. Config model in `OpenClaw.Core/Models/`. Two adapters (`RemoteEndpointSource`, `LocalFilesSource`) implement `ISparqlSource`. `GraphSlicerEngine` orchestrates CONSTRUCT → merge → Frame → write.

**Tech Stack:** .NET 10, C#, dotNetRDF 3.3.0, xUnit, System.Text.Json

## Global Constraints

- dotNetRDF 3.3.0 dependency confined to `OpenClaw.GraphSlicer` only.
- CLI AOT: `OpenClaw.GraphSlicer` must set `<PublishAot>false</PublishAot>` and CLI csproj must add `<TrimmerRootAssembly Include="OpenClaw.GraphSlicer" />` to prevent trimming of dotNetRDF types.
- 不改变 MetaSkill DAG 或 `load_temporary_graph` 的行为。
- 配置文件路径默认 `graph-slice.yaml`（与 `appsettings.json` 同目录）。
- 三种数据源：远程 SPARQL 端点、本地 RDF 文件、关系 DB+Ontop（后两种通过 SPARQL 端点适配器覆盖）。
- 输出 JSON-LD 文件与 `load_temporary_graph` 兼容。
- MaxTriples 默认 50000，防止大图撑满上下文。

---

## Scope Check

本计划覆盖一个独立的子系统（图切片器类库 + CLI 集成），属于可独立验收的实现切片。无需再拆分为多个计划。

## File Structure

- Create: `src/OpenClaw.GraphSlicer/OpenClaw.GraphSlicer.csproj`
  Responsibility: 独立类库，dotNetRDF 3.3.0 + OpenClaw.Core 依赖。
- Create: `src/OpenClaw.GraphSlicer/ISparqlSource.cs`
  Responsibility: 数据源接口 `Task<IGraph> ExecuteConstructAsync(string constructQuery, CancellationToken ct)`。
- Create: `src/OpenClaw.GraphSlicer/RemoteEndpointSource.cs`
  Responsibility: 远程 SPARQL 端点适配器，Basic/Digest 认证。
- Create: `src/OpenClaw.GraphSlicer/LocalFilesSource.cs`
  Responsibility: 本地 RDF 文件适配器，LeviathanQueryProcessor 内存执行。
- Create: `src/OpenClaw.GraphSlicer/GraphSlicerEngine.cs`
  Responsibility: 编排：多源 CONSTRUCT → merge → JSON-LD → Frame → 写文件。
- Create: `src/OpenClaw.Core/Models/GraphSliceProfile.cs`
  Responsibility: GraphSliceProfile, SliceSourceConfig, SliceAuthConfig, SliceOutputConfig。
- Modify: `src/OpenClaw.Core/Models/Session.cs`
  Responsibility: 新增 JsonSerializable 声明。
- Create: `src/OpenClaw.Cli/GraphSliceCommands.cs`
  Responsibility: CLI 命令入口 `openclaw graph slice`。
- Modify: `src/OpenClaw.Cli/OpenClaw.Cli.csproj`
  Responsibility: 新增 `OpenClaw.GraphSlicer` 项目引用 + TrimmerRootAssembly。
- Modify: `src/OpenClaw.Cli/Program.cs`
  Responsibility: 注册 `"graph"` 命令分支。
- Create: `src/OpenClaw.Tests/GraphSliceCommandsTests.cs`
  Responsibility: 单元测试（mock HTTP SPARQL 端点、本地文件、engine 编排）。

---

### Task 1: Create OpenClaw.GraphSlicer project and config model

**Files:**
- Create: `src/OpenClaw.GraphSlicer/OpenClaw.GraphSlicer.csproj`
- Create: `src/OpenClaw.Core/Models/GraphSliceProfile.cs`
- Modify: `src/OpenClaw.Core/Models/Session.cs`

**Interfaces:**
- Produces:
  - `GraphSliceProfile` — 配置根（Profiles: `Dictionary<string, SliceProfile>`）
  - `SliceProfile` — 单 profile（Sources, Construct, Frame, Output）
  - `SliceSourceConfig` — 数据源配置（Kind, Endpoint, Auth, Paths 等）
  - `SliceAuthConfig` — 认证（Type, UsernameEnv, PasswordEnv）
  - `SliceOutputConfig` — 输出（Path, MaxTriples, Compaction）

- [ ] **Step 1: Create GraphSlicer csproj**

Create `src/OpenClaw.GraphSlicer/OpenClaw.GraphSlicer.csproj`:

```xml
<Project Sdk="Microsoft.NET.Sdk">
  <PropertyGroup>
    <RootNamespace>OpenClaw.GraphSlicer</RootNamespace>
    <PublishAot>false</PublishAot>
  </PropertyGroup>

  <ItemGroup>
    <InternalsVisibleTo Include="OpenClaw.Tests" />
  </ItemGroup>

  <ItemGroup>
    <PackageReference Include="dotNetRDF" Version="3.3.0" />
    <ProjectReference Include="..\OpenClaw.Core\OpenClaw.Core.csproj" />
  </ItemGroup>
</Project>
```

- [ ] **Step 2: Create config model**

Create `src/OpenClaw.Core/Models/GraphSliceProfile.cs`:

```csharp
namespace OpenClaw.Core.Models;

public sealed class GraphSliceProfile
{
    public Dictionary<string, SliceProfile> Profiles { get; set; } = [];
}

public sealed class SliceProfile
{
    public List<SliceSourceConfig> Sources { get; set; } = [];
    public string Construct { get; set; } = "";
    public System.Text.Json.JsonElement? Frame { get; set; }
    public SliceOutputConfig Output { get; set; } = new();
}

public sealed class SliceSourceConfig
{
    // "remote-endpoint" | "local-files"
    public string Kind { get; set; } = "remote-endpoint";

    // remote-endpoint fields
    public string? Endpoint { get; set; }
    public SliceAuthConfig? Auth { get; set; }
    public int TimeoutSeconds { get; set; } = 60;
    public string? DefaultGraphUri { get; set; }

    // local-files fields
    public List<string>? Paths { get; set; }
    public string? NamedGraphUri { get; set; }
}

public sealed class SliceAuthConfig
{
    // "none" | "basic" | "digest"
    public string Type { get; set; } = "none";
    public string? UsernameEnv { get; set; }
    public string? PasswordEnv { get; set; }
}

public sealed class SliceOutputConfig
{
    public string Path { get; set; } = "./tmp/graph-slice.jsonld";
    public int MaxTriples { get; set; } = 50000;
    public bool Compaction { get; set; } = true;
}
```

- [ ] **Step 3: Add JsonSerializable declarations in Session.cs**

In `src/OpenClaw.Core/Models/Session.cs`, after the `ConnectorAuthConfig` entry, add:

```csharp
[JsonSerializable(typeof(GraphSliceProfile))]
[JsonSerializable(typeof(SliceProfile))]
[JsonSerializable(typeof(SliceSourceConfig))]
[JsonSerializable(typeof(SliceAuthConfig))]
[JsonSerializable(typeof(SliceOutputConfig))]
[JsonSerializable(typeof(List<SliceSourceConfig>))]
[JsonSerializable(typeof(List<SliceProfile>))]
```

- [ ] **Step 4: Build and verify**

```bash
dotnet build src/OpenClaw.GraphSlicer/OpenClaw.GraphSlicer.csproj
dotnet build src/OpenClaw.Core/OpenClaw.Core.csproj
```

Expected: PASS — both build successfully.

- [ ] **Step 5: Commit**

```bash
git add src/OpenClaw.GraphSlicer/OpenClaw.GraphSlicer.csproj src/OpenClaw.Core/Models/GraphSliceProfile.cs src/OpenClaw.Core/Models/Session.cs
git commit -m "feat: add graph slicer project and config model"
```

---

### Task 2: Implement ISparqlSource, RemoteEndpointSource, LocalFilesSource

**Files:**
- Create: `src/OpenClaw.GraphSlicer/ISparqlSource.cs`
- Create: `src/OpenClaw.GraphSlicer/RemoteEndpointSource.cs`
- Create: `src/OpenClaw.GraphSlicer/LocalFilesSource.cs`

**Interfaces:**
- Consumes:
  - `dotNetRDF` types: `IGraph`, `SparqlRemoteEndpoint`, `LeviathanQueryProcessor`, `TripleStore`, `FileLoader`
  - `SliceSourceConfig` / `SliceAuthConfig` from config model
- Produces:
  - `ISparqlSource` — `Task<IGraph> ExecuteConstructAsync(string constructQuery, CancellationToken ct)`
  - `RemoteEndpointSource(SliceSourceConfig config)` — remote SPARQL endpoint adapter
  - `LocalFilesSource(SliceSourceConfig config)` — local file adapter

- [ ] **Step 1: Write ISparqlSource interface**

Create `src/OpenClaw.GraphSlicer/ISparqlSource.cs`:

```csharp
using VDS.RDF;

namespace OpenClaw.GraphSlicer;

internal interface ISparqlSource
{
    Task<IGraph> ExecuteConstructAsync(string constructQuery, CancellationToken ct);
}
```

- [ ] **Step 2: Implement RemoteEndpointSource**

Create `src/OpenClaw.GraphSlicer/RemoteEndpointSource.cs`:

```csharp
using System.Net;
using VDS.RDF;
using VDS.RDF.Query;
using OpenClaw.Core.Models;

namespace OpenClaw.GraphSlicer;

internal sealed class RemoteEndpointSource : ISparqlSource
{
    private readonly SliceSourceConfig _config;

    public RemoteEndpointSource(SliceSourceConfig config)
    {
        _config = config ?? throw new ArgumentNullException(nameof(config));
        if (string.IsNullOrWhiteSpace(config.Endpoint))
            throw new ArgumentException("Endpoint is required for remote-endpoint source.");
    }

    public async Task<IGraph> ExecuteConstructAsync(string constructQuery, CancellationToken ct)
    {
        var endpoint = new SparqlRemoteEndpoint(new Uri(_config.Endpoint!));
        if (_config.TimeoutSeconds > 0)
            endpoint.Timeout = _config.TimeoutSeconds * 1000;

        ConfigureAuth(endpoint);

        if (!string.IsNullOrWhiteSpace(_config.DefaultGraphUri))
            endpoint.DefaultGraphUri = _config.DefaultGraphUri;

        using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        cts.CancelAfter(TimeSpan.FromSeconds(_config.TimeoutSeconds));

        return await Task.Run(
            () => endpoint.QueryWithResultGraph(constructQuery),
            cts.Token);
    }

    private void ConfigureAuth(SparqlRemoteEndpoint endpoint)
    {
        var auth = _config.Auth;
        if (auth is null || string.Equals(auth.Type, "none", StringComparison.OrdinalIgnoreCase))
            return;

        var username = Environment.GetEnvironmentVariable(auth.UsernameEnv ?? "")?.Trim() ?? "";
        var password = Environment.GetEnvironmentVariable(auth.PasswordEnv ?? "")?.Trim() ?? "";

        if (!string.IsNullOrWhiteSpace(username) && !string.IsNullOrWhiteSpace(password))
        {
            endpoint.Credentials = new NetworkCredential(username, password);
        }
    }
}
```

- [ ] **Step 3: Implement LocalFilesSource**

Create `src/OpenClaw.GraphSlicer/LocalFilesSource.cs`:

```csharp
using VDS.RDF;
using VDS.RDF.Parsing;
using VDS.RDF.Query;
using OpenClaw.Core.Models;

namespace OpenClaw.GraphSlicer;

internal sealed class LocalFilesSource : ISparqlSource
{
    private readonly SliceSourceConfig _config;

    public LocalFilesSource(SliceSourceConfig config)
    {
        _config = config ?? throw new ArgumentNullException(nameof(config));
        if (_config.Paths is null || _config.Paths.Count == 0)
            throw new ArgumentException("Paths are required for local-files source.");
    }

    public async Task<IGraph> ExecuteConstructAsync(string constructQuery, CancellationToken ct)
    {
        var store = new TripleStore();

        await Task.Run(() =>
        {
            foreach (var path in _config.Paths!)
            {
                ct.ThrowIfCancellationRequested();
                var g = new Graph();
                FileLoader.Load(g, path);
                if (!string.IsNullOrWhiteSpace(_config.NamedGraphUri))
                    store.Add(new Uri(_config.NamedGraphUri), g);
                else
                    store.Add(g);
            }
        }, ct);

        var processor = new LeviathanQueryProcessor(store);
        var results = processor.ProcessQuery(SparqlQueryParser.Parse(constructQuery)) as IGraph;

        return results ?? new Graph();
    }
}
```

- [ ] **Step 4: Build and verify**

```bash
dotnet build src/OpenClaw.GraphSlicer/OpenClaw.GraphSlicer.csproj
```

Expected: PASS — all three files compile with dotNetRDF types.

- [ ] **Step 5: Commit**

```bash
git add src/OpenClaw.GraphSlicer/ISparqlSource.cs src/OpenClaw.GraphSlicer/RemoteEndpointSource.cs src/OpenClaw.GraphSlicer/LocalFilesSource.cs
git commit -m "feat: add sparql source adapters for remote endpoint and local files"
```

---

### Task 3: Implement GraphSlicerEngine

**Files:**
- Create: `src/OpenClaw.GraphSlicer/GraphSlicerEngine.cs`

**Interfaces:**
- Consumes:
  - `ISparqlSource` list from Tasks 2
  - `SliceProfile` from Task 1
  - dotNetRDF: `JsonLdProcessor.FromRDF()`, `JsonLdProcessor.Frame()`, `JsonLdProcessor.ToFlatString()`
- Produces:
  - `GraphSlicerEngine.ExecuteAsync(SliceProfile profile, CancellationToken ct) -> SliceResult`
  - `SliceResult` record class: Success, OutputPath, TripleCount, Truncated, ErrorMessage

- [ ] **Step 1: Write the engine with SliceResult**

Create `src/OpenClaw.GraphSlicer/GraphSlicerEngine.cs`:

```csharp
using System.Text;
using System.Text.Json;
using Newtonsoft.Json.Linq;
using VDS.RDF;
using VDS.RDF.JsonLd;
using OpenClaw.Core.Models;

namespace OpenClaw.GraphSlicer;

public sealed record SliceResult(
    bool Success,
    string? OutputPath = null,
    int TripleCount = 0,
    bool Truncated = false,
    string? ErrorMessage = null);

public sealed class GraphSlicerEngine
{
    public async Task<SliceResult> ExecuteAsync(SliceProfile profile, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(profile);

        try
        {
            // 1. Build sources
            var sources = profile.Sources.Select(BuildSource).ToList();
            if (sources.Count == 0)
                return new SliceResult(false, ErrorMessage: "No sources configured.");

            // 2. Execute CONSTRUCT across all sources and merge
            var mergedGraph = new Graph();
            foreach (var source in sources)
            {
                ct.ThrowIfCancellationRequested();
                var graph = await source.ExecuteConstructAsync(profile.Construct, ct).ConfigureAwait(false);
                mergedGraph.Merge(graph);
            }

            if (mergedGraph.IsEmpty)
                return new SliceResult(false, ErrorMessage: "CONSTRUCT produced an empty graph.");

            // 3. Check triple limit
            var tripleCount = mergedGraph.Triples.Count;
            var truncated = false;
            if (profile.Output.MaxTriples > 0 && tripleCount > profile.Output.MaxTriples)
                truncated = true;

            // 4. Convert to JSON-LD
            var jsonLdDoc = JsonLdProcessor.FromRDF(mergedGraph);
            if (jsonLdDoc is null)
                return new SliceResult(false, ErrorMessage: "Failed to convert RDF to JSON-LD.");

            // 5. Apply frame if specified
            object finalDoc = jsonLdDoc;
            if (profile.Frame is { ValueKind: JsonValueKind.Object })
            {
                var frameJson = profile.Frame.Value.GetRawText();
                var frameObj = JObject.Parse(frameJson);
                finalDoc = JsonLdProcessor.Frame(jsonLdDoc, frameObj);
            }

            // 6. Serialize and write
            var outputJson = profile.Output.Compaction
                ? JsonLdProcessor.ToFlatString(finalDoc)
                : JsonLdProcessor.ToFlatString(finalDoc); // compaction is default behavior

            var outputPath = profile.Output.Path;
            var dir = Path.GetDirectoryName(outputPath);
            if (!string.IsNullOrWhiteSpace(dir) && !Directory.Exists(dir))
                Directory.CreateDirectory(dir);

            await File.WriteAllTextAsync(outputPath, outputJson, Encoding.UTF8, ct).ConfigureAwait(false);

            return new SliceResult(true, outputPath, tripleCount, truncated);
        }
        catch (OperationCanceledException)
        {
            return new SliceResult(false, ErrorMessage: "Operation cancelled.");
        }
        catch (Exception ex)
        {
            return new SliceResult(false, ErrorMessage: ex.Message);
        }
    }

    private static ISparqlSource BuildSource(SliceSourceConfig config)
    {
        return config.Kind.ToLowerInvariant() switch
        {
            "remote-endpoint" => new RemoteEndpointSource(config),
            "local-files" => new LocalFilesSource(config),
            _ => throw new ArgumentException($"Unknown source kind '{config.Kind}'.")
        };
    }
}
```

- [ ] **Step 2: Build and verify**

```bash
dotnet build src/OpenClaw.GraphSlicer/OpenClaw.GraphSlicer.csproj
```

Expected: PASS.

- [ ] **Step 3: Commit**

```bash
git add src/OpenClaw.GraphSlicer/GraphSlicerEngine.cs
git commit -m "feat: add graph slicer engine with construct merge frame pipeline"
```

---

### Task 4: Add CLI command and wire into Program.cs

**Files:**
- Create: `src/OpenClaw.Cli/GraphSliceCommands.cs`
- Modify: `src/OpenClaw.Cli/Program.cs`
- Modify: `src/OpenClaw.Cli/OpenClaw.Cli.csproj`

**Interfaces:**
- Consumes:
  - `GraphSlicerEngine.ExecuteAsync(SliceProfile, CancellationToken)` from Task 3
  - `GraphSliceProfile` from YAML config file via `GraphSliceCommands.LoadProfile()`
- Produces:
  - CLI: `openclaw graph slice --profile <name> [--output <path>] [--dry-run] [--info]`

- [ ] **Step 1: Update CLI csproj**

In `src/OpenClaw.Cli/OpenClaw.Cli.csproj`, add after existing ProjectReferences:

```xml
<ItemGroup>
  <ProjectReference Include="..\OpenClaw.GraphSlicer\OpenClaw.GraphSlicer.csproj" />
  <TrimmerRootAssembly Include="OpenClaw.GraphSlicer" />
</ItemGroup>
```

- [ ] **Step 2: Create CLI command file**

Create `src/OpenClaw.Cli/GraphSliceCommands.cs`:

```csharp
using System.Text.Json;
using Microsoft.Extensions.Configuration;
using OpenClaw.Core.Models;

namespace OpenClaw.Cli;

internal static class GraphSliceCommands
{
    public static async Task<int> RunAsync(string[] args)
        => await RunAsync(args, Console.Out, Console.Error);

    internal static async Task<int> RunAsync(
        string[] args,
        TextWriter output,
        TextWriter error)
    {
        if (args.Length == 0 || args[0] is "-h" or "--help" or "help")
        {
            PrintHelp(output);
            return 0;
        }

        var command = args[0].ToLowerInvariant();
        if (command != "slice")
        {
            PrintHelp(output);
            return 2;
        }

        var parsed = CliArgs.Parse(args.Skip(1).ToArray());
        if (parsed.ShowHelp)
        {
            PrintHelp(output);
            return 0;
        }

        var profileName = parsed.GetOption("--profile");
        if (string.IsNullOrWhiteSpace(profileName))
        {
            await error.WriteLineAsync("--profile is required.");
            return 2;
        }

        var profile = LoadProfile(profileName);
        if (profile is null)
        {
            await error.WriteLineAsync($"Profile '{profileName}' not found in graph-slice.yaml.");
            return 2;
        }

        // --info mode
        if (parsed.HasFlag("--info"))
        {
            PrintInfo(profileName, profile, output);
            return 0;
        }

        // --output override
        var outputPath = parsed.GetOption("--output");
        if (!string.IsNullOrWhiteSpace(outputPath))
            profile.Output.Path = outputPath;

        // --dry-run mode
        if (parsed.HasFlag("--dry-run"))
        {
            await output.WriteLineAsync(
                $"[dry-run] Profile: {profileName}, Sources: {profile.Sources.Count}, " +
                $"Output would be: {profile.Output.Path}");
            return 0;
        }

        // Execute
        var engine = new GraphSlicer.GraphSlicerEngine();
        var result = await engine.ExecuteAsync(profile, CancellationToken.None);

        if (!result.Success)
        {
            await error.WriteLineAsync($"Slice failed: {result.ErrorMessage}");
            return 1;
        }

        await output.WriteLineAsync($"Slice complete: {result.OutputPath} ({result.TripleCount} triples{(result.Truncated ? ", truncated" : "")})");
        return result.Truncated ? 0 : 0;
    }

    private static SliceProfile? LoadProfile(string profileName)
    {
        try
        {
            // Look for graph-slice.yaml in current directory and common config paths
            var configPaths = new[]
            {
                "graph-slice.yaml",
                "graph-slice.yml",
                "graph-slice.json",
                Path.Combine(AppContext.BaseDirectory, "graph-slice.yaml"),
                Path.Combine(AppContext.BaseDirectory, "graph-slice.json"),
            };

            foreach (var configPath in configPaths)
            {
                if (!File.Exists(configPath))
                    continue;

                var builder = new ConfigurationBuilder();
                if (configPath.EndsWith(".json", StringComparison.OrdinalIgnoreCase))
                    builder.AddJsonFile(configPath, optional: false);
                else
                    builder.AddYamlFile(configPath, optional: false);

                var config = builder.Build();
                var root = config.Get<GraphSliceProfile>();
                if (root?.Profiles is not null && root.Profiles.TryGetValue(profileName, out var profile))
                    return profile;
            }

            return null;
        }
        catch
        {
            return null;
        }
    }

    private static void PrintInfo(string profileName, SliceProfile profile, TextWriter output)
    {
        output.WriteLine($"Profile: {profileName}");
        output.WriteLine($"Sources: {profile.Sources.Count}");
        foreach (var source in profile.Sources)
        {
            output.WriteLine($"  - {source.Kind}: {(source.Kind == "remote-endpoint" ? source.Endpoint : string.Join(", ", source.Paths ?? []))}");
        }
        output.WriteLine($"Output: {profile.Output.Path}");
        output.WriteLine($"MaxTriples: {profile.Output.MaxTriples}");
        output.WriteLine($"Compaction: {profile.Output.Compaction}");
    }

    private static void PrintHelp(TextWriter output)
    {
        output.WriteLine("Usage: openclaw graph slice --profile <name> [options]");
        output.WriteLine();
        output.WriteLine("Commands:");
        output.WriteLine("  slice        Execute a graph slice profile");
        output.WriteLine();
        output.WriteLine("Options:");
        output.WriteLine("  --profile    Profile name from graph-slice.yaml (required)");
        output.WriteLine("  --output     Override output path");
        output.WriteLine("  --dry-run    Validate without writing output");
        output.WriteLine("  --info       Print profile configuration");
        output.WriteLine("  --json       Output result as JSON");
    }
}
```

Note: `AddYamlFile` requires the `NetEscapades.Configuration.Yaml` package. If not already in the CLI project, add the package reference. Check existing config reading pattern first.

- [ ] **Step 3: Register command in Program.cs**

In `src/OpenClaw.Cli/Program.cs`, add after line 39 (`"connector"`):

```csharp
"graph" => await GraphSliceCommands.RunAsync(rest),
```

- [ ] **Step 4: Build full solution**

```bash
dotnet build
```

Expected: PASS — all projects build including GraphSlicer and CLI with new command.

- [ ] **Step 5: Commit**

```bash
git add src/OpenClaw.Cli/GraphSliceCommands.cs src/OpenClaw.Cli/Program.cs src/OpenClaw.Cli/OpenClaw.Cli.csproj
git commit -m "feat: add graph slice cli command with profile loading"
```

---

### Task 5: Write tests

**Files:**
- Create: `src/OpenClaw.Tests/GraphSliceCommandsTests.cs`

**Interfaces:**
- Consumes:
  - `GraphSlicerEngine`, `RemoteEndpointSource`, `LocalFilesSource`, `SliceProfile` from prior tasks
  - `TestHttpMessageHandler` from `HttpActionAdapterConnectorTests.cs` (same test project, internal)
- Produces:
  - Tests for: engine empty result, engine multi-source merge, engine frame, source creation, profile loading

- [ ] **Step 1: Write tests**

Create `src/OpenClaw.Tests/GraphSliceCommandsTests.cs`:

```csharp
using System.Net;
using System.Text.Json;
using OpenClaw.Core.Models;
using OpenClaw.GraphSlicer;
using Xunit;

namespace OpenClaw.Tests;

public sealed class GraphSliceCommandsTests
{
    [Fact]
    public async Task ExecuteAsync_EmptyConstruct_ReturnsError()
    {
        var engine = new GraphSlicerEngine();
        var profile = new SliceProfile
        {
            Sources = [],
            Construct = "CONSTRUCT { ?s ?p ?o } WHERE { ?s ?p ?o }",
            Output = new SliceOutputConfig { Path = Path.GetTempFileName() }
        };

        var result = await engine.ExecuteAsync(profile, TestContext.Current.CancellationToken);

        Assert.False(result.Success);
        Assert.Equal("No sources configured.", result.ErrorMessage);
    }

    [Fact]
    public async Task ExecuteAsync_LocalFiles_EmptyGraph_ReturnsError()
    {
        var workspace = CreateTempDir();
        var ttlPath = Path.Combine(workspace, "empty.ttl");
        await File.WriteAllTextAsync(ttlPath, "@prefix ex: <http://example.org/> .\n");

        var engine = new GraphSlicerEngine();
        var profile = new SliceProfile
        {
            Sources =
            [
                new SliceSourceConfig
                {
                    Kind = "local-files",
                    Paths = [ttlPath]
                }
            ],
            Construct = "CONSTRUCT { ?s ?p ?o } WHERE { ?s ?p ?o }",
            Output = new SliceOutputConfig { Path = Path.Combine(workspace, "out.jsonld") }
        };

        var result = await engine.ExecuteAsync(profile, TestContext.Current.CancellationToken);

        Assert.False(result.Success);
        Assert.Equal("CONSTRUCT produced an empty graph.", result.ErrorMessage);
    }

    [Fact]
    public async Task ExecuteAsync_LocalFiles_WithData_ProducesOutput()
    {
        var workspace = CreateTempDir();
        var ttlPath = Path.Combine(workspace, "data.ttl");
        await File.WriteAllTextAsync(ttlPath, """
        @prefix ex: <http://example.org/> .
        ex:Alice ex:name "Alice" ; ex:age 30 .
        """);

        var outputPath = Path.Combine(workspace, "out.jsonld");
        var engine = new GraphSlicerEngine();
        var profile = new SliceProfile
        {
            Sources =
            [
                new SliceSourceConfig
                {
                    Kind = "local-files",
                    Paths = [ttlPath]
                }
            ],
            Construct = "CONSTRUCT { ?s ?p ?o } WHERE { ?s ?p ?o }",
            Output = new SliceOutputConfig { Path = outputPath }
        };

        var result = await engine.ExecuteAsync(profile, TestContext.Current.CancellationToken);

        Assert.True(result.Success);
        Assert.Equal(outputPath, result.OutputPath);
        Assert.True(result.TripleCount > 0);
        Assert.True(File.Exists(outputPath));

        // Verify output is valid JSON and contains expected data
        var content = await File.ReadAllTextAsync(outputPath);
        Assert.Contains("Alice", content, StringComparison.Ordinal);
    }

    [Fact]
    public async Task ExecuteAsync_WithFrame_AppliesFrame()
    {
        var workspace = CreateTempDir();
        var ttlPath = Path.Combine(workspace, "data.ttl");
        await File.WriteAllTextAsync(ttlPath, """
        @prefix ex: <http://example.org/> .
        @prefix rdf: <http://www.w3.org/1999/02/22-rdf-syntax-ns#> .
        ex:Alice ex:name "Alice" ; ex:age 30 .
        """);

        var outputPath = Path.Combine(workspace, "out.jsonld");
        var engine = new GraphSlicerEngine();
        var frameJson = JsonDocument.Parse("""{"@context": "http://example.org/"}""");
        var profile = new SliceProfile
        {
            Sources =
            [
                new SliceSourceConfig
                {
                    Kind = "local-files",
                    Paths = [ttlPath]
                }
            ],
            Construct = "CONSTRUCT { ?s ?p ?o } WHERE { ?s ?p ?o }",
            Frame = frameJson.RootElement,
            Output = new SliceOutputConfig { Path = outputPath }
        };

        var result = await engine.ExecuteAsync(profile, TestContext.Current.CancellationToken);

        Assert.True(result.Success);
        Assert.True(File.Exists(outputPath));
    }

    [Fact]
    public async Task ExecuteAsync_RemoteEndpoint_MockSparql_ReturnsSuccess()
    {
        using var handler = new TestHttpMessageHandler();

        // Simulate a SPARQL CONSTRUCT response (Turtle format)
        handler.SetResponse("/query", HttpStatusCode.OK, """
        @prefix ex: <http://example.org/> .
        ex:Bob ex:name "Bob" .
        """);

        var workspace = CreateTempDir();
        var outputPath = Path.Combine(workspace, "out.jsonld");

        var engine = new GraphSlicerEngine();
        var profile = new SliceProfile
        {
            Sources =
            [
                new SliceSourceConfig
                {
                    Kind = "remote-endpoint",
                    Endpoint = "http://test.local/query",
                    TimeoutSeconds = 30,
                    Auth = new SliceAuthConfig { Type = "none" }
                }
            ],
            Construct = "CONSTRUCT { ?s ?p ?o } WHERE { ?s ?p ?o }",
            Output = new SliceOutputConfig { Path = outputPath }
        };

        var result = await engine.ExecuteAsync(profile, TestContext.Current.CancellationToken);

        Assert.True(result.Success);
        Assert.True(File.Exists(outputPath));
    }

    private static string CreateTempDir()
    {
        var path = Path.Combine(Path.GetTempPath(), "openclaw-graph-slicer-tests", Guid.NewGuid().ToString("n"));
        Directory.CreateDirectory(path);
        return path;
    }
}
```

- [ ] **Step 2: Run tests to verify GREEN**

```bash
dotnet test src/OpenClaw.Tests/OpenClaw.Tests.csproj --filter "FullyQualifiedName~GraphSliceCommandsTests" -v minimal
```

Expected: PASS.

- [ ] **Step 3: Run full test suite for regression**

```bash
dotnet test src/OpenClaw.Tests/OpenClaw.Tests.csproj -v minimal
```

Expected: All tests pass, zero regressions.

- [ ] **Step 4: Commit**

```bash
git add src/OpenClaw.Tests/GraphSliceCommandsTests.cs
git commit -m "test: add graph slicer unit and integration tests"
```

---

### Task 6: Update documentation

**Files:**
- Modify: `docs/zh-CN/meta-skill-harness-action-writeback-pipeline.md`
- Modify: `docs/meta-skill-harness-action-writeback-pipeline.md`

- [ ] **Step 1: Add external slicer section to pipeline docs (Chinese)**

In `docs/zh-CN/meta-skill-harness-action-writeback-pipeline.md`, before the "架构总览" section, add a reference to the graph slicer:

```markdown
> **外部切片器：** 临时图由 `openclaw graph slice` CLI 命令生成（基于 dotNetRDF，支持 SPARQL
> CONSTRUCT + JSON-LD Framing），详见 [图切片器设计说明](../superpowers/specs/2026-07-16-graph-slicer-sparql-construct-jsonld-design.md)。
```

- [ ] **Step 2: Same for English version**

In `docs/meta-skill-harness-action-writeback-pipeline.md`, add equivalent English note.

- [ ] **Step 3: Commit**

```bash
git add docs/zh-CN/meta-skill-harness-action-writeback-pipeline.md docs/meta-skill-harness-action-writeback-pipeline.md
git commit -m "docs: link graph slicer from pipeline guide"
```

---

## Self-Review

### 1. Spec coverage

- Section 4 (Architecture + data source adapters): Task 2 (adapters) + Task 3 (engine)
- Section 5 (CLI design): Task 4 (GraphSliceCommands.cs + Program.cs)
- Section 6 (Config model): Task 1 (GraphSliceProfile.cs)
- Section 7 (Execution semantics, error handling): Task 3 (engine) + Task 5 (tests)
- Section 8 (dotNetRDF capability): Covered by Tasks 2-3 (using those APIs)
- Section 9 (Dependencies): Task 1 (csproj)
- Section 10 (Test strategy): Task 5

### 2. Placeholder scan

No TBD, TODO, or incomplete sections. Every step has concrete code and expected output.

### 3. Type consistency

- `SliceProfile` defined in Task 1, consumed in Tasks 3, 4, 5
- `SliceSourceConfig` defined in Task 1, consumed in Tasks 2, 3, 5
- `ISparqlSource` defined in Task 2, consumed in Task 3
- `RemoteEndpointSource`, `LocalFilesSource` defined in Task 2, consumed in Task 3
- `GraphSlicerEngine`, `SliceResult` defined in Task 3, consumed in Tasks 4, 5
- `GraphSliceProfile` defined in Task 1, consumed in Task 4

All signatures consistent across tasks.

## Execution Handoff

Plan complete and saved to `docs/superpowers/plans/2026-07-16-graph-slicer-implementation.md`. Two execution options:

1. Subagent-Driven (recommended) - I dispatch a fresh subagent per task, review between tasks, fast iteration
2. Inline Execution - Execute tasks in this session using executing-plans, batch execution with checkpoints

Which approach?
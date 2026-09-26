# Python-free Nacos Live Implementation Plan

> **For agentic workers:** Use superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** 用 .NET 10 完全替换 `eng/nacos-live` 的 Python 验收 harness，同时保留隔离 Nacos 3.2.4、Router 真实代理、managed/NativeAOT、双 runtime、事件失效及证据产物。

**Architecture:** `eng/nacos-live` 提供一个 .NET 10 可执行项目，父进程负责 Nacos、embedding 模型、Nacos MCP 注册、Router、既有 smoke/test 程序和清理；同一程序集的 child mode 启动 loopback Streamable HTTP weather MCP fixture。Router 从固定 NuGet global tool `NacosMcpRouter` 1.0.0 安装和运行，不 clone 源码仓库。

**Tech Stack:** .NET 10、RedNb.Nacos.All 2.1.0、ModelContextProtocol.AspNetCore 2.2.0、xUnit v3 3.2.2、Microsoft.NET.Test.Sdk 18.6.0、Nacos 3.2.4、NacosMcpRouter 1.0.0、Java 17。

## Global Constraints

- Target framework is `net10.0`.
- The Nacos archive is Nacos 3.2.4 and must match SHA-256 `da5eec77934140133fe93e5532079e4e99b4cae7eb50463f2f6cd2bc8f380a70`.
- Install Router with `dotnet tool install --global NacosMcpRouter --version 1.0.0`; invoke `nacos-mcp-router`.
- The fixture and Router listen only on loopback; fixture protocol is Streamable HTTP and tool is `get_weather(city)`.
- Download `model.onnx`, `tokenizer.json`, `vocab.txt`, and `config.json` over HTTPS; default source is `sentence-transformers/all-MiniLM-L6-v2`, branch `master` at ModelScope.
- No Python, pip, Python package, or Python entry point may remain in `eng/nacos-live` or `.github/workflows/nacos-live.yml`.
- Preserve existing OpenClaw managed/NativeAOT smoke and dual-runtime test contracts, including `OPENCLAW_NACOS_LIVE=1`, JSON reflection disabled, zero capability-path model calls, and config-publish invalidation within 2,000 ms.
- Do not put generated credentials in process arguments, logs, or evidence. Nacos internal auth secrets may exist only in the isolated temporary config file with restricted permissions.
- Do not edit runtime adapters or unrelated Python tools elsewhere under `eng/`.

---

## File Map

- Create `eng/nacos-live/NacosLiveAcceptance.csproj` for the .NET 10 harness and MCP fixture; exclude `tests/**/*.cs` from its default compile glob.
- Create `eng/nacos-live/Program.cs` for CLI parsing, top-level exit handling, and the `--weather-fixture` child mode.
- Create `eng/nacos-live/AcceptanceRunner.cs` for ordered orchestration and the single cleanup boundary.
- Create `eng/nacos-live/OwnedProcess.cs` for argument-list process launch, environment setup, log capture, timeout, and process-tree termination.
- Create `eng/nacos-live/NacosServer.cs` for archive download/hash validation, extraction, authenticated temporary configuration, process start, and readiness polling.
- Create `eng/nacos-live/ModelAssets.cs` for model URI construction, HTTPS download, file validation, and `EMBEDDING_MODEL_DIR` setup.
- Create `eng/nacos-live/WeatherFixture.cs` for Streamable HTTP MCP hosting, `/health`, and deterministic `get_weather(city)` fixture data.
- Create `eng/nacos-live/McpRegistration.cs` for HTTP MCP release/read/delete and gRPC endpoint register/deregister through `RedNb.Nacos.All`.
- Create `eng/nacos-live/tests/NacosLiveAcceptance.Tests.csproj` and focused tests for archive/model helpers, MCP registration payloads, CLI parsing, and owned-process cleanup.
- Modify `eng/nacos-live/README.md` to document Python-free prerequisites, tool installation, and run commands.
- Delete `eng/nacos-live/requirements.txt`, `router_server.py`, `weather_server.py`, `verify.py`, and `.gitignore` (`__pycache__/` is its only entry; root `.gitignore` already ignores `bin/`, `obj/`, `TestResults/`, and `*.trx`).
- Modify `.github/workflows/nacos-live.yml` to remove Python setup and invoke the .NET harness while retaining current build and artifact steps.
- Do not add eng projects to `OpenClaw.Net.slnx`; existing `eng/NacosLiveSmoke` is also built directly by path.

## Task 1: Create the .NET Harness, CLI, and Process Foundation

**Files:**

- Create: `eng/nacos-live/NacosLiveAcceptance.csproj`
- Create: `eng/nacos-live/Program.cs`
- Create: `eng/nacos-live/OwnedProcess.cs`
- Create: `eng/nacos-live/tests/NacosLiveAcceptance.Tests.csproj`
- Create: `eng/nacos-live/tests/AcceptanceOptionsTests.cs`
- Create: `eng/nacos-live/tests/OwnedProcessTests.cs`

**Interfaces:**

- `AcceptanceOptions.Parse(string[] args)` returns `AcceptanceOptions(string ManagedPath, string NativePath, string TestDllPath, string OutputDirectory)`; it validates the three input files and requires a new output directory.
- `Program.Main` dispatches `--weather-fixture` to the fixture host and all other invocations to `AcceptanceRunner`; errors print a concise message and return nonzero.
- `OwnedProcess.Start(string fileName, IEnumerable<string> arguments, string workingDirectory, IReadOnlyDictionary<string,string?> environment, string logPath)` returns an owned process with `WaitForExitAsync(TimeSpan timeout, CancellationToken cancellationToken)` and `StopAsync(TimeSpan timeout, CancellationToken cancellationToken)`.
- The acceptance project exposes internals only to assembly `NacosLiveAcceptance.Tests`.

- [ ] **Step 1: Add failing CLI and process tests.** `AcceptanceOptionsTests` covers all required paths and rejects a missing path or existing evidence directory. `OwnedProcessTests` verifies discrete arguments, captured logs, and bounded termination of an owned child tree.
- [ ] **Step 2: Run both focused tests and confirm they fail.**

Run: `dotnet test eng/nacos-live/tests/NacosLiveAcceptance.Tests.csproj -c Release --filter "FullyQualifiedName~AcceptanceOptionsTests|FullyQualifiedName~OwnedProcessTests"`

Expected: FAIL because the acceptance project, `AcceptanceOptions`, and `OwnedProcess` do not exist.

- [ ] **Step 3: Add the .NET 10 project files.** Reference `RedNb.Nacos.All` 2.1.0, `ModelContextProtocol` 2.2.0, and `ModelContextProtocol.AspNetCore` 2.2.0; add `FrameworkReference` to `Microsoft.AspNetCore.App`. Set `TargetFramework` to `net10.0` and `OutputType` to `Exe`. Exclude `tests/**/*.cs` from the parent project compile items. The test project uses `Microsoft.NET.Test.Sdk` 18.6.0, `xunit.v3` 3.2.2, `xunit.runner.visualstudio` 3.1.5, and a project reference to the harness.
- [ ] **Step 4: Implement `AcceptanceOptions.Parse` and the entry-point dispatch.** Use an explicit option-to-value parser for `--managed`, `--native`, `--test-dll`, and `--output`; validate every input path and reject an output path that already exists. Keep the private fixture switch separate from user-facing acceptance options.
- [ ] **Step 5: Implement `OwnedProcess`.** Use `ProcessStartInfo.ArgumentList`, `UseShellExecute=false`, explicit environment assignment, redirected stdout/stderr to the per-process log, and `Process.Kill(entireProcessTree: true)` followed by a bounded wait.
- [ ] **Step 6: Run focused tests and build.**

Run: `dotnet test eng/nacos-live/tests/NacosLiveAcceptance.Tests.csproj -c Release --filter "FullyQualifiedName~AcceptanceOptionsTests|FullyQualifiedName~OwnedProcessTests"`

Expected: PASS. Then run `dotnet build eng/nacos-live/NacosLiveAcceptance.csproj -c Release`; expected: build succeeds with warnings treated as errors.

## Task 2: Add Verified Nacos and Model Provisioning

**Files:**

- Create: `eng/nacos-live/NacosServer.cs`
- Create: `eng/nacos-live/ModelAssets.cs`
- Create: `eng/nacos-live/tests/NacosArchiveTests.cs`
- Create: `eng/nacos-live/tests/ModelAssetsTests.cs`

**Interfaces:**

- `NacosServer.StartAsync(HttpClient client, string workDirectory, CancellationToken cancellationToken)` downloads/verifies/extracts Nacos 3.2.4, starts the owned Java process, waits for readiness, and returns `NacosDeployment(string ServerAddress, int ConsolePort, string Username, string Password, OwnedProcess Process)`; `ServerAddress` includes the main HTTP port and namespace is the Nacos public namespace.
- `ModelAssets.PrepareAsync(HttpClient client, string destination, ModelSource source, CancellationToken cancellationToken)` downloads the four required files and returns the absolute model directory.
- Define `ModelSource(Uri Endpoint, string Repository, string Branch)` in `ModelAssets.cs`; when `HF_BRANCH` is unset, use `master` for ModelScope and `main` for other endpoints.
- `NacosServer.SelectPorts()` returns a named tuple `(int ServerPort, int ConsolePort, int RouterPort, int FixturePort)`, keeps the server and its `+1000`/`+1001` gRPC ports dynamic, and uses Console port `8080` because Router 1.0.0 defaults its Nacos Console API address there.
- `NacosServer` and `ModelAssets` expose pure URL/hash/file validation helpers internally for focused tests.

- [ ] **Step 1: Add failing archive and model helper tests.** Verify the pinned SHA-256 accepts matching bytes and rejects a mismatch; verify a model URL uses the configured endpoint/repository/branch and that each missing model file is reported by name.
- [ ] **Step 2: Run the focused tests and confirm they fail because helpers are absent.**

Run: `dotnet test eng/nacos-live/tests/NacosLiveAcceptance.Tests.csproj -c Release --filter "FullyQualifiedName~NacosArchiveTests|FullyQualifiedName~ModelAssetsTests"`

Expected: FAIL because the helper types are not implemented.

- [ ] **Step 3: Implement Nacos archive download and verification.** Use the official release URL `https://github.com/alibaba/nacos/releases/download/3.2.4/nacos-server-3.2.4.tar.gz`, compare lowercase SHA-256 to the pinned value before extraction, extract with .NET archive APIs into a new run-specific directory, and reject path traversal entries.
- [ ] **Step 4: Implement isolated authenticated Nacos startup.** Preserve the existing application properties from `verify.py`: loopback HTTP binding, gRPC offsets `+1000` and `+1001`, Console port `8080` (required by the pinned Router's default Console API address), temporary admin password, server identity, generated token secret, and disabled Nacos AI/Skill registries. Start `java` with `ProcessStartInfo.ArgumentList`; poll `http://127.0.0.1:{consolePort}/v3/console/health/readiness` with an overall 90-second timeout. Store generated Nacos internal secrets only in the run-specific config file and restrict its permissions on supported platforms.
- [ ] **Step 5: Implement model acquisition.** Build download URIs from `HF_ENDPOINT`, `HF_REPO`, and `HF_BRANCH` with defaults `https://www.modelscope.cn`, `sentence-transformers/all-MiniLM-L6-v2`, and `master`. Download `onnx/model.onnx` as `model.onnx` plus `tokenizer.json`, `vocab.txt`, and `config.json`; use temporary files and rename only after successful HTTPS responses. Reject empty or missing assets.
- [ ] **Step 6: Run the focused tests.**

Run: `dotnet test eng/nacos-live/tests/NacosLiveAcceptance.Tests.csproj -c Release --filter "FullyQualifiedName~NacosArchiveTests|FullyQualifiedName~ModelAssetsTests"`

Expected: PASS, including mismatch and missing-file cases.

## Task 3: Implement HTTP Fixture and Nacos MCP Registration

**Files:**

- Create: `eng/nacos-live/WeatherFixture.cs`
- Create: `eng/nacos-live/McpRegistration.cs`
- Create: `eng/nacos-live/tests/McpRegistrationTests.cs`
- Create: `eng/nacos-live/tests/WeatherFixtureProtocolTests.cs`

**Interfaces:**

- `WeatherFixture.RunAsync(int port, CancellationToken cancellationToken)` hosts `/mcp` using Streamable HTTP and exposes `/health` for readiness.
- `WeatherMcpTools.GetWeather(string city)` accepts `Oslo` and returns JSON with `city`, numeric `temperature_c`, and `source: "acceptance fixture"`; other cities fail deterministically.
- `McpRegistration.CreateReleaseRequest(string mcpName, string serviceName)` returns `McpReleaseRequest(McpServerBasicInfo Server, McpToolSpecification Tools, McpEndpointSpec Endpoint)` for unit testing the exact Nacos payload.
- `McpRegistration.RegisterAsync(NacosDeployment deployment, string fixtureAddress, int fixturePort, CancellationToken cancellationToken)` creates the HTTP and gRPC AI services, releases MCP metadata, registers the endpoint, and returns an `IAsyncDisposable` handle that owns the service lifetimes.
- Registration disposal attempts endpoint deregistration over gRPC and MCP deletion over HTTP independently.

- [ ] **Step 1: Add a failing registration-model test.** Assert `CreateReleaseRequest` uses `AiConstants.Mcp.ProtocolStreamable`, version `1.0.0`, a `get_weather` tool specification, a `RemoteServerConfig.ServiceRef` for the temporary service name, and an endpoint spec with `AiConstants.Mcp.EndpointTypeRef` and matching namespace/group/service values.
- [ ] **Step 2: Run the focused test and confirm it fails.**

Run: `dotnet test eng/nacos-live/tests/NacosLiveAcceptance.Tests.csproj -c Release --filter FullyQualifiedName~McpRegistrationTests`

Expected: FAIL because the registration builder is absent.

- [ ] **Step 3: Implement the fixture host and tool.** Register `AddMcpServer().WithHttpTransport().WithTools<WeatherMcpTools>()`, map `/mcp` and `/health`, listen on `127.0.0.1`, and bind the selected port. Keep logs on stderr/stdout captured by the parent process.
- [ ] **Step 4: Implement the Nacos registration lifecycle using the SDK.** Create HTTP and gRPC `IAiService` instances with the temporary credentials. Release the MCP server and REF specification over HTTP, register the endpoint through `RegisterMcpServerEndpointAsync` over gRPC, and make the returned cleanup handle independently attempt gRPC deregistration and HTTP deletion. Follow the field structure in `samples/RedNb.Nacos.Sample.AI/McpSamples.cs`.
- [ ] **Step 5: Add an HTTP fixture protocol test.** Start the fixture on a dynamically selected loopback port, connect with `McpClient` using `HttpClientTransport` in `StreamableHttp` mode, initialize, assert the listed tool is `get_weather`, invoke it with `{"city":"Oslo"}`, and assert the returned fixture JSON.
- [ ] **Step 6: Run the focused fixture and registration tests.**

Run: `dotnet test eng/nacos-live/tests/NacosLiveAcceptance.Tests.csproj -c Release --filter "FullyQualifiedName~McpRegistrationTests|FullyQualifiedName~WeatherFixtureProtocolTests"`

Expected: PASS for registration-payload and live fixture HTTP tests.

## Task 4: Orchestrate Router, Existing Smoke, and Reliable Cleanup

**Files:**

- Create: `eng/nacos-live/AcceptanceRunner.cs`
- Create: `eng/nacos-live/RouterConfiguration.cs`
- Modify: `eng/nacos-live/Program.cs`
- Create: `eng/nacos-live/tests/RouterConfigurationTests.cs`

**Interfaces:**

- `RouterConfiguration.BuildRouterEnvironment(NacosDeployment deployment, int port, string modelDirectory, string dataDirectory)` returns Router-only variables: Nacos address/credentials/namespace, `TRANSPORT_TYPE=streamable_http`, `PORT`, `UPDATE_INTERVAL=10`, `EMBEDDING_MODEL_DIR`, and `SONNETDB_DATA_DIR`.
- `RouterConfiguration.BuildSmokeEnvironment(NacosDeployment deployment, string routerUrl)` returns OpenClaw variables: `OPENCLAW_NACOS_LIVE=1`, `OPENCLAW_NACOS_ROUTER_URL`, `OPENCLAW_NACOS_SERVER`, `OPENCLAW_NACOS_USERNAME`, and `OPENCLAW_NACOS_PASSWORD`.
- `AcceptanceRunner.RunAsync(AcceptanceOptions options, CancellationToken cancellationToken)` provisions Nacos/model/fixture, registers the fixture, starts Router, runs the existing smoke executables and test DLL, writes evidence, and returns an exit code.

- [ ] **Step 1: Add failing environment-builder tests.** Assert Router config contains its Nacos/router/model/data keys and excludes `OPENCLAW_NACOS_*`; assert smoke config contains the existing OpenClaw keys and excludes router-only settings.
- [ ] **Step 2: Run the focused test and confirm it fails.**

Run: `dotnet test eng/nacos-live/tests/NacosLiveAcceptance.Tests.csproj -c Release --filter FullyQualifiedName~RouterConfigurationTests`

Expected: FAIL because `RouterConfiguration` does not exist.

- [ ] **Step 3: Implement `RouterConfiguration`.** Construct Router and smoke environments separately with the exact keys in the interfaces above; do not forward Router-only variables to smoke processes or OpenClaw variables to Router.
- [ ] **Step 4: Implement the acceptance orchestration in strict order.** Start Nacos; prepare model; start fixture child mode; wait for fixture `/health`; register MCP endpoint; start Router global command; wait for Router `/health`; launch managed and NativeAOT smoke with distinct `OPENCLAW_NACOS_REPORT` paths; run `OpenClaw.Tests.dll` filtered to `FullyQualifiedName~LiveRouter`; write `acceptance.json`; print `NACOS_LIVE_ACCEPTANCE_PASS` only after all checks pass.
- [ ] **Step 5: Make cleanup independent and reverse ordered.** In `finally`, dispose MCP registration, stop Router, stop fixture, stop Nacos, close log streams, and remove the temporary work directory. Give cleanup its own 5-second budget per child/registration and preserve logs/reports in the requested output directory.
- [ ] **Step 6: Prove embedding search did not silently fall back.** Capture Router logs and fail the run if they contain `vector search failed`; make this check part of the successful acceptance path after the dynamic search tool is invoked.
- [ ] **Step 7: Run focused tests and build.**
Run: `dotnet test eng/nacos-live/tests/NacosLiveAcceptance.Tests.csproj -c Release --filter FullyQualifiedName~RouterConfigurationTests`

Expected: PASS. Then run `dotnet build eng/nacos-live/NacosLiveAcceptance.csproj -c Release`; expected: PASS.

## Task 5: Replace CI and Local Instructions, Remove Python

**Files:**

- Modify: `.github/workflows/nacos-live.yml`
- Modify: `eng/nacos-live/README.md`
- Delete: `eng/nacos-live/requirements.txt`
- Delete: `eng/nacos-live/router_server.py`
- Delete: `eng/nacos-live/weather_server.py`
- Delete: `eng/nacos-live/verify.py`
- Delete: `eng/nacos-live/.gitignore`

**Interfaces:**

- CI invokes the acceptance project with explicit `--managed`, `--native`, `--test-dll`, and `--output` arguments.
- Local instructions use the same global tool version and the same .NET acceptance command as CI.

- [ ] **Step 1: Update CI tool installation.** Remove `actions/setup-python`, pip cache/install, Python Chroma model preflight, and `verify.py` invocation. Add `dotnet tool install --global NacosMcpRouter --version 1.0.0` and add `$HOME/.dotnet/tools` to `GITHUB_PATH`. Preserve Java 17, NativeAOT prerequisites, Nacos archive behavior through the harness, existing publish/build steps, concurrency, timeout, and artifact upload.
- [ ] **Step 2: Replace the Python verifier call.** Run helper tests using `dotnet test eng/nacos-live/tests/NacosLiveAcceptance.Tests.csproj -c Release`, then invoke the harness with the exact existing build outputs:

```sh
dotnet run --project eng/nacos-live/NacosLiveAcceptance.csproj -c Release --no-build -- --managed eng/NacosLiveSmoke/bin/Release/net10.0/NacosLiveSmoke.dll --native "$RUNNER_TEMP/nacos-native/NacosLiveSmoke" --test-dll src/OpenClaw.Tests/bin/Release/net10.0/OpenClaw.Tests.dll --output "$RUNNER_TEMP/nacos-evidence"
```

Keep evidence upload patterns compatible with the existing JSON, TRX, and logs.

- [ ] **Step 3: Rewrite the local README.** Document .NET 10, Java 17+, NativeAOT prerequisites, network access, the exact global tool installation command, model source overrides, managed/native/test build commands, and a fresh output directory. Explain that fixture weather is labelled test data and that success requires live Router/Nacos/event round trips.
- [ ] **Step 4: Delete all four Python files and the Python-only `.gitignore`.** Do not change other `eng/` Python utilities.
- [ ] **Step 5: Search for stale scoped references.** Confirm `eng/nacos-live` and `.github/workflows/nacos-live.yml` contain no `python`, `pip`, `requirements.txt`, `verify.py`, or Python Chroma preflight references. Keep unrelated repo-wide Python tools untouched.
- [ ] **Step 6: Run focused gates.**

Run: `dotnet test eng/nacos-live/tests/NacosLiveAcceptance.Tests.csproj -c Release`

Expected: PASS. Run: `dotnet build eng/NacosLiveSmoke -c Release -p:PublishAot=false` and `dotnet build src/OpenClaw.Tests -c Release -p:OpenClawSkipDashboardBuild=true`.

Expected: both builds succeed. Run the new README acceptance command against the local .NET 10 / Java 17 environment; expected: managed/native reports, dual-runtime TRX, retained process logs, and `NACOS_LIVE_ACCEPTANCE_PASS`.

## Plan Self-Review

- Every design section maps to implementation work: project boundaries in Task 1; Nacos/model provisioning in Task 2; HTTP fixture and HTTP+gRPC registration in Task 3; process lifecycle, tool invocation, existing acceptance, evidence, and cleanup in Task 4; CI/local docs and Python removal in Task 5.
- Embedding fallback is explicitly rejected by a Router-log assertion after a live dynamic search.
- The Nacos archive digest, package versions, tool command, test command, environment variables, and retained acceptance marker are specified concretely.
- No production adapter, unrelated `eng/` scripts, or solution-file changes are included.

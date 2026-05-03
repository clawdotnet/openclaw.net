# Start Here

OpenClaw.NET is a NativeAOT-friendly AI agent runtime and gateway for .NET with practical OpenClaw ecosystem compatibility.

Read this page first if you are cloning the repository to decide whether the project is useful, buildable, and worth evaluating.

## What It Is

OpenClaw.NET is a self-hosted .NET agent stack. It includes:

| Part | Responsibility |
| --- | --- |
| Runtime | Runs the agent loop, model calls, tool calls, streaming, memory, sessions, cancellation, retries, and runtime hooks. |
| Gateway | Hosts the local HTTP, OpenAI-compatible, MCP, websocket, admin, health, and diagnostic surfaces. |
| CLI | Handles setup, launch, model/profile diagnostics, maintenance, plugin/skill commands, and scripted prompt runs. |
| Companion | Desktop setup and managed-gateway flow for users who prefer a local app over terminal commands. |

The project is aimed at .NET developers and operators who want a local or self-hosted agent gateway with explicit failure modes, NativeAOT-friendly defaults, and compatibility with practical parts of the OpenClaw ecosystem.

## What Works Today

| Surface | Status | Notes |
| --- | --- | --- |
| Agent runtime | Stable core | Agent loop, tool execution, streaming events, cancellation, retry, session history, and memory are covered by tests. |
| Gateway | Stable local path | Local loopback startup, health/status, OpenAI-compatible endpoints, admin diagnostics, MCP, and websocket paths build and test. |
| CLI | Stable local path | `start`, `setup`, `run`, `chat`, `models`, `maintenance`, `upgrade`, plugin, skill, and diagnostic commands are available. |
| Companion | Supported with caveats | Builds with the solution and includes managed-gateway tests. Desktop release managers should still run the manual smoke checklist in [RELEASES.md](RELEASES.md). |
| Native tools | Supported | Core native tools are first-party .NET tools with explicit path, network, and approval guardrails. |
| Model providers | Supported with provider setup | OpenAI, Claude, Gemini, Azure OpenAI, Ollama, and OpenAI-compatible endpoints are represented in the provider/config surface. |
| NativeAOT | Supported/friendly | Runtime and gateway are designed for the AOT lane. Dynamic/plugin surfaces are explicitly separated where needed. |
| OpenClaw compatibility | Practical compatibility | `SKILL.md`, ClawHub-style skill install, mainstream tool/service plugin APIs, and package discovery are supported. Full upstream API parity is not claimed. |

## Experimental Or Partial

- JIT-only plugin surfaces such as dynamic channels, commands, hooks, provider registration, and native dynamic .NET plugins.
- Microsoft Agent Framework adapter experiments.
- Browser tool local execution when dynamic code or a non-local execution backend is not configured.
- Companion UI behavior beyond the managed-gateway unit coverage; use the release smoke checklist before public desktop releases.
- Full production signing/notarization polish for desktop archives.

## First Successful Source Run

Use the deterministic hello-agent sample first. It proves the runtime can build, call a tool, complete the agent loop, and print a known result without requiring provider keys, Ollama, Docker, or a browser.

```bash
git clone https://github.com/clawdotnet/openclaw.net
cd openclaw.net

dotnet restore OpenClaw.Net.slnx
dotnet build OpenClaw.Net.slnx --configuration Release --no-restore
dotnet run --project samples/OpenClaw.HelloAgent -c Release --no-build
```

Expected output:

```text
OpenClaw.HelloAgent
User: hello
Agent: hello from OpenClaw.NET
Tool: echo(hello): ok
```

After that works, choose one of these:

```bash
# Guided local gateway setup and launch
dotnet run --project src/OpenClaw.Cli -c Release -- start

# Direct interactive gateway fallback
dotnet run --project src/OpenClaw.Gateway -c Release -- --quickstart

# Full test suite
dotnet test OpenClaw.Net.slnx --configuration Release --no-build
```

For the full local setup path, continue with [QUICKSTART.md](QUICKSTART.md).

## Support Matrix

| Area | Windows | Linux | macOS | Notes |
| --- | --- | --- | --- | --- |
| Source build/test | Supported | Supported | Supported | Requires .NET 10 SDK. |
| Runtime | Supported | Supported | Supported | NativeAOT-friendly core; dynamic plugin surfaces are mode-gated. |
| Gateway | Supported | Supported | Supported | Loopback local path is the primary developer evaluation path. |
| CLI | Supported | Supported | Supported | Also published as NativeAOT release archives. |
| Companion | Supported | Supported | Supported | Desktop bundles include Companion, gateway, and CLI. |
| Desktop release archives | Windows x64 | Linux x64 | Apple Silicon macOS | Windows/macOS archives are currently unsigned, so OS warnings are expected. |
| Plugin compatibility | Supported with caveats | Supported with caveats | Supported with caveats | See [COMPATIBILITY.md](COMPATIBILITY.md) for the exact AOT/JIT boundary. |
| NativeAOT publish | Supported | Supported | Supported with linker caveat | The gateway has a scoped macOS classic-linker fallback until the Apple/.NET linker path is reliable. |

## Compatibility Boundaries

OpenClaw.NET supports practical OpenClaw ecosystem reuse:

- standalone `SKILL.md` packages
- plugin-packaged skills
- ClawHub-style skill install flow
- mainstream `api.registerTool()` and `api.registerService()` plugin surfaces
- explicit diagnostics for unsupported plugin APIs

OpenClaw.NET is not a full upstream OpenClaw clone. Unsupported or JIT-only surfaces fail fast instead of loading partially. See [COMPATIBILITY.md](COMPATIBILITY.md) for the canonical matrix.

## Known Limitations

- Tool-heavy local Ollama routes need a tool-capable model profile or configured fallback. Plain chat works as prompt-only when no explicit tool preset is requested.
- Public-bind startup intentionally refuses unsafe configurations until auth, secrets, tool roots, and channel signature settings are hardened.
- TypeScript plugin loading requires `jiti`; OpenClaw.NET does not bundle a TypeScript runtime automatically.
- Browser tool setup defaults are conservative. Local/public generated profiles leave it disabled unless a compatible execution backend is selected.
- Windows and macOS release archives are unsigned today, so first-run operating-system warnings are expected.
- Some advanced plugin/provider surfaces are JIT-only by design.

## Where To Go Next

| Goal | Read |
| --- | --- |
| Run a real local gateway | [QUICKSTART.md](QUICKSTART.md) |
| Understand repository shape | [GETTING_STARTED.md](GETTING_STARTED.md) |
| Check compatibility | [COMPATIBILITY.md](COMPATIBILITY.md) |
| Use tools and skills | [USER_GUIDE.md](USER_GUIDE.md) and [TOOLS_GUIDE.md](TOOLS_GUIDE.md) |
| Download desktop bundles | [RELEASES.md](RELEASES.md) |
| Contribute | [../CONTRIBUTING.md](../CONTRIBUTING.md) |

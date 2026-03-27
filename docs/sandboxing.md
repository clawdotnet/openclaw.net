# Optional Sandbox Execution

OpenClaw.NET can route high-risk native tools through an external sandbox service instead of executing them on the gateway host.

The current optional backend is [OpenSandbox](https://github.com/AIDotNet/OpenSandbox), integrated through a separate `OpenClawNet.Sandbox.OpenSandbox` assembly so the standard runtime artifact stays lightweight and NativeAOT-friendly.

## Architecture

Runtime flow:

1. The agent selects a tool.
2. `OpenClawToolExecutor` checks whether the tool implements `ISandboxCapableTool`.
3. The effective sandbox mode is resolved from:
   - per-tool config override, or
   - the tool's code-level default (`Prefer` for `shell`, `code_exec`, and `browser`).
4. The executor either:
   - runs locally,
   - runs through `IToolSandbox`, or
   - fails closed if the tool is configured as `Require` and no sandbox is available.

`OpenClaw:Sandbox:Provider=None` is the global off switch. When set, sandbox-capable tools run locally even if per-tool sandbox modes remain configured.

Tool execution layers:

- Native in-process tools
- TS/JS plugin bridge
- OpenSandbox-backed native tool execution

## Supported Tools

V1 sandbox routing covers native high-risk tools only:

- `shell`
- `code_exec`
- `browser`

JS/TS bridge tools are unchanged in this first pass.

## Build And Enable

The OpenSandbox integration is not included in the default gateway/test build.

Build a sandbox-enabled artifact with:

```bash
dotnet build -c Release -p:OpenClawEnableOpenSandbox=true src/OpenClaw.Gateway
```

Or run tests for the sandbox-enabled build:

```bash
dotnet test -c Release -p:OpenClawEnableOpenSandbox=true src/OpenClaw.Tests
```

## Configuration

Shipped gateway default:

```json
{
  "OpenClaw": {
    "Sandbox": {
      "Provider": "OpenSandbox",
      "Endpoint": "http://localhost:5000",
      "ApiKey": "env:OPEN_SANDBOX_API_KEY",
      "DefaultTTL": 300,
      "Tools": {
        "shell": {
          "Mode": "Prefer",
          "Template": "alpine:3.20",
          "TTL": 300
        },
        "code_exec": {
          "Mode": "Prefer",
          "Template": "nikolaik/python-nodejs:python3.12-nodejs22-slim",
          "TTL": 300
        },
        "browser": {
          "Mode": "Prefer",
          "Template": "mcr.microsoft.com/playwright:v1.52.0-noble",
          "TTL": 600
        }
      }
    }
  }
}
```

Force local execution everywhere:

```json
{
  "OpenClaw": {
    "Sandbox": {
      "Provider": "None"
    }
  }
}
```

Example stricter public-bind shell deployment:

```json
{
  "OpenClaw": {
    "Sandbox": {
      "Provider": "OpenSandbox",
      "Endpoint": "http://localhost:5000",
      "ApiKey": "env:OPEN_SANDBOX_API_KEY",
      "DefaultTTL": 300,
      "Tools": {
        "shell": {
          "Mode": "Require",
          "Template": "alpine:3.20",
          "TTL": 300
        },
        "code_exec": {
          "Mode": "Prefer",
          "Template": "nikolaik/python-nodejs:python3.12-nodejs22-slim",
          "TTL": 300
        },
        "browser": {
          "Mode": "Prefer",
          "Template": "mcr.microsoft.com/playwright:v1.52.0-noble",
          "TTL": 600
        }
      }
    }
  }
}
```

Notes:

- `Provider=None` forces local execution for sandbox-capable tools and is the simplest opt-out switch.
- `Prefer` uses the sandbox when available, then falls back to local execution if the provider is missing or temporarily unreachable.
- `Require` fails closed and never falls back to local execution.
- `Template` currently maps directly to the container image URI passed to OpenSandbox when the lease is created.
- `TTL` is the sandbox lease lifetime in seconds.
- The shipped image URIs are starter defaults. Override them if you need different runtimes, hardened images, or private registry control.

## Security Benefits

Using OpenSandbox reduces host risk for tools that can execute code or interact with untrusted content:

- `shell` commands no longer execute on the gateway host
- `code_exec` snippets run in disposable remote containers
- `browser` automation can keep session state inside a reused sandbox lease instead of on the host

For non-loopback/public binds, `shell` in `Require` mode with `Provider=OpenSandbox` is treated as sandboxed by the gateway hardening checks. `Prefer` is still considered unsafe because it can fall back to local execution.

## Operational Notes

- The executor reuses sandbox leases by `sessionId:toolName`.
- Browser automation uses a persistent profile directory inside the sandbox lease so multi-step browsing keeps state.
- The gateway `--doctor` command checks OpenSandbox reachability when `Provider=OpenSandbox`.
- Runtime metrics now expose sandbox lease create/reuse/recovery counters.
- The admin incident export includes redacted sandbox execution and lease-lifecycle context for debugging.
- The integration uses raw `HttpClient` plus source-generated `System.Text.Json` models. No OpenSandbox SDK dependency is added to the core runtime.

# Nacos Live Acceptance

This .NET 10 harness verifies the optional Nacos adapters against an isolated,
authenticated Nacos 3.2.4 deployment and the pinned `NacosMcpRouter` 1.0.0
global tool. It registers a loopback Streamable HTTP `weather-mcp` fixture and
checks managed and NativeAOT smoke executables plus both live runtime tests.
Generated credentials and temporary service data stay outside the evidence
directory; reports and process logs are retained there.

The weather response is deterministic, labelled acceptance-fixture data for
Oslo. It validates real Nacos registration, Router proxying, and event-driven
binding invalidation, but does not measure external weather availability or
general discovery quality. No LLM is called.

## Prerequisites

- .NET SDK 10
- Java 17 or newer
- NativeAOT prerequisites for the current host
- Network access to NuGet, the pinned Nacos release, and the model source

Install the pinned Router tool and make its global tool directory available on
`PATH`:

```powershell
dotnet tool install --global NacosMcpRouter --version 1.0.0
$env:PATH += ";$HOME\.dotnet\tools"
```

The model defaults to `sentence-transformers/all-MiniLM-L6-v2` on ModelScope,
branch `master`. Set `HF_ENDPOINT`, `HF_REPO`, or `HF_BRANCH` to use another
HTTPS-compatible model source; non-ModelScope endpoints default to branch `main`.

## Run Locally

Run these commands from the repository root. Replace `win-x64` and the native
executable path if building on another host. Use a new evidence directory for
each run.

```powershell
$native = Join-Path $env:TEMP "openclaw-nacos-native"
$evidence = Join-Path $env:TEMP "openclaw-nacos-evidence"
dotnet publish eng/NacosLiveSmoke -c Release -r win-x64 -p:PublishAot=true -o $native
dotnet build eng/NacosLiveSmoke -c Release -p:PublishAot=false
dotnet build src/OpenClaw.Tests -c Release -p:OpenClawSkipDashboardBuild=true
dotnet test eng/nacos-live/tests/NacosLiveAcceptance.Tests.csproj -c Release
dotnet run --project eng/nacos-live/NacosLiveAcceptance.csproj -c Release --no-build -- `
  --managed eng/NacosLiveSmoke/bin/Release/net10.0/NacosLiveSmoke.dll `
  --native (Join-Path $native "NacosLiveSmoke.exe") `
  --test-dll src/OpenClaw.Tests/bin/Release/net10.0/OpenClaw.Tests.dll `
  --output $evidence
```

The harness downloads and verifies the official Nacos archive before extraction,
creates a fresh authenticated server, downloads and validates all four embedding
assets, starts the fixture and Router, and removes only processes and temporary
data that it owns. It fails if Router embedding search reports a keyword fallback.

## Evidence

Success prints `NACOS_LIVE_ACCEPTANCE_PASS` and produces `managed.json`,
`native.json`, `acceptance.json`, `live-runtimes.trx`, and logs for Nacos, Router,
the fixture, and smoke/test processes. The checks retain exactly three Router
tools, static/dynamic binding and cache reuse, authenticated subscription,
zero capability-path model calls, JSON reflection disabled, and config-publish
invalidation/rebind within 2,000 ms. A successful AOT build alone is not an
acceptance pass. Review third-party logs before publishing them.

The dedicated workflow also publishes the full Gateway with both optional
Nacos flags and NativeAOT enabled. The ordinary adapter matrix continues to
verify that default Gateway builds contain no Nacos SDK and Router-only builds
contain no RedNb packages.

Historical contributor evidence remains in [the Router guide](../../docs/nacos-mcp-router.md#historical-contributor-evidence).

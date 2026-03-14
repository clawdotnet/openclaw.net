# MAF Optional Backend Readiness

## Conclusion
Microsoft Agent Framework is now a supported optional orchestration backend for OpenClaw.NET in both `jit` and `aot`, with `native` still the default orchestrator and `auto` runtime-mode resolution unchanged.

This branch does not replace the native OpenClaw runtime. OpenClaw.NET continues to own the runtime, gateway, approvals, policy, plugin compatibility, observability, deployment, and hosting surfaces. MAF remains an optional orchestration engine selected by configuration in the MAF-enabled artifacts.

## Supported Artifact Model

| Artifact | Build flag | Supported orchestrators | Purpose |
|---|---|---|---|
| `gateway-standard-jit` | `OpenClawEnableMafExperiment=false`, `PublishAot=false` | `native` | Default JIT gateway artifact |
| `gateway-maf-enabled-jit` | `OpenClawEnableMafExperiment=true`, `PublishAot=false` | `native`, `maf` | JIT artifact with optional MAF backend |
| `gateway-standard-aot` | `OpenClawEnableMafExperiment=false`, NativeAOT publish | `native` | Default AOT gateway artifact |
| `gateway-maf-enabled-aot` | `OpenClawEnableMafExperiment=true`, NativeAOT publish | `native`, `maf` | AOT artifact with optional MAF backend |

`native` remains the default orchestrator in every artifact. Standard artifacts fail fast if `Runtime.Orchestrator=maf` is configured. MAF-enabled artifacts support both `native` and `maf`.

The publish entrypoint for these deliverables is [publish-gateway-artifacts.sh](../../eng/publish-gateway-artifacts.sh).

## CI Support

GitHub Actions now treats both artifact families as supported build targets:

- standard and MAF-enabled gateway builds are restored, built, and tested in CI
- JIT and NativeAOT smoke coverage runs for both `native` and `maf`
- pushes to `main` upload the four supported gateway artifacts with distinct artifact names

This keeps the two-artifact packaging model explicit instead of relying on ad hoc local publish commands.

## Runtime Selection

```json
{
  "OpenClaw": {
    "Runtime": {
      "Mode": "auto|jit|aot",
      "Orchestrator": "native|maf"
    }
  }
}
```

- `Runtime.Mode=auto` still resolves exactly as it did before this branch.
- `Runtime.Orchestrator=native` remains the default.
- `Runtime.Orchestrator=maf` is supported only in the MAF-enabled artifacts.

## Parity Summary

The branch now has published-binary four-cell coverage for:

- HTTP tool flow: [maf-aot-jit-findings.md](maf-aot-jit-findings.md)
- WebSocket approval happy path: [maf-aot-jit-approval-findings.md](maf-aot-jit-approval-findings.md)
- WebSocket approval denial/timeout/requester-mismatch path: [maf-aot-jit-approval-outcome-findings.md](maf-aot-jit-approval-outcome-findings.md)
- WebSocket restart/resume and MAF sidecar fallback: [maf-aot-jit-restart-findings.md](maf-aot-jit-restart-findings.md)
- HTTP plugin tool flow: [maf-aot-jit-plugin-findings.md](maf-aot-jit-plugin-findings.md)
- HTTP plugin tool flow with file-bound `Plugins:Entries:*:Config`: [maf-aot-jit-plugin-config-findings.md](maf-aot-jit-plugin-config-findings.md)
- HTTP/SSE text streaming: [maf-aot-jit-stream-findings.md](maf-aot-jit-stream-findings.md)
- HTTP/SSE streamed tool flow: [maf-aot-jit-stream-tool-findings.md](maf-aot-jit-stream-tool-findings.md)
- `/v1/responses` streamed tool flow: [maf-aot-jit-responses-stream-tool-findings.md](maf-aot-jit-responses-stream-tool-findings.md)
- `/v1/responses` failure flow: [maf-aot-jit-responses-failed-findings.md](maf-aot-jit-responses-failed-findings.md)

Representative findings from the new completion work:

- Restart/resume is externally stable in all four cells, and MAF sidecar restore plus canonical-history fallback are both logged in `jit` and `aot`.
- Negative approval paths match across all four cells: websocket deny, HTTP admin deny, websocket requester-mismatch rejection, and websocket timeout.
- File-bound plugin config now works in all four cells, including published NativeAOT artifacts.
- Hidden provider-SDK retries were disabled so logical and transport retry counts no longer diverge in the failure matrix.

## Remaining Intentional Differences

- The supported packaging model is intentionally two deliverable families: standard and MAF-enabled.
- MAF package risk remains a dependency-management concern because Microsoft Agent Framework is still prerelease/RC software.

Neither of those differences changes OpenClaw’s runtime ownership model or weakens security defaults.

## Recommendation

Adopt MAF as a supported optional backend in both `jit` and `aot`, with the two-artifact packaging model above and `native` retained as the default orchestrator.

# OpenClaw.NET Hardening Audit

Date: 2026-05-02

## Executive Summary

OpenClaw.NET is in a generally healthy state for the standard runtime path: restore, Release build, full test suite, CLI NativeAOT publish, and gateway NativeAOT publish all completed successfully on macOS arm64 with .NET SDK 10.0.100. The main product risk found in the initial pass was not the default path; it was the optional compatibility and filesystem-hardening edge cases around plugin loading and MAF-enabled builds. Those highest-value issues were fixed in this pass, and the follow-up limitation pass tightened the remaining developer-confidence issues where the repository controls them.

No Critical issue was found in the standard runtime, gateway, CLI, companion, or test path. The highest-value fixes selected for this pass are:

- High: fixed the MAF-enabled solution build by giving the test project direct conditional references to the MAF/A2A packages used by MAF tests.
- High: hardened plugin entry path containment so symlinked entry files or symlinked parent directories cannot escape the plugin root.
- Medium: hardened tool and contract-scope path canonicalization for existing files under symlinked parent directories.
- Medium: normalized Companion managed-gateway websocket URLs by stripping query and fragment data.
- Medium: made streaming clients receive `tool_start` before long-running non-streaming tools block the stream.
- Medium: made browser-tool tests use a local page and Playwright's normal browser cache instead of a per-run browser install path and external `example.com`.
- Low: scoped the macOS `-ld_classic` fallback to the gateway AOT project instead of every osx-arm64 AOT publish.
- Low: made top-level CLI help list the existing `plugins` command.

## Build And Test Status

- `dotnet restore OpenClaw.Net.slnx`: Passed.
- `dotnet build OpenClaw.Net.slnx --configuration Release --no-restore`: Passed with 0 warnings and 0 errors.
- Initial `dotnet test OpenClaw.Net.slnx --configuration Release --no-build`: Passed, 1039/1039 tests.
- Initial post-fix focused regression tests: Passed, 46/46 tests.
- Post-fix `dotnet test OpenClaw.Net.slnx --configuration Release --no-build`: Passed, 1044/1044 tests.
- Limitation-pass focused tests for streaming and browser coverage: Passed, 34/34 tests.
- Limitation-pass `dotnet test OpenClaw.Net.slnx --configuration Release --no-restore`: Passed, 1045/1045 tests.
- `dotnet publish src/OpenClaw.Cli/OpenClaw.Cli.csproj --configuration Release --runtime osx-arm64 --self-contained true`: Passed and the resulting `openclaw --help` ran successfully. After scoping `-ld_classic`, the CLI publish no longer emits the deprecated linker warning.
- `dotnet publish src/OpenClaw.Gateway/OpenClaw.Gateway.csproj --configuration Release --runtime osx-arm64 --self-contained true`: Passed. Gateway still emits the deprecated `-ld_classic` warning plus package/toolchain module-cache debug-info warnings. A probe without the classic linker failed with an Apple `ld::Fixup` assertion, so the gateway fallback remains intentionally scoped.
- Post-fix `dotnet build OpenClaw.Net.slnx --configuration Release -p:OpenClawEnableOpenSandbox=true`: Passed with 0 warnings and 0 errors.
- Post-fix `dotnet build OpenClaw.Net.slnx --configuration Release -p:OpenClawEnableMafExperiment=true`: Passed with 0 warnings and 0 errors.
- CI follow-up: `.github/workflows/ci.yml` now restores/builds `OpenClaw.Net.slnx` for the standard, OpenSandbox-enabled, and MAF-enabled variants; PR CI now runs the standard Linux NativeAOT gateway/CLI smoke, and scheduled/manual CI probes macOS gateway publish with `-p:OpenClawUseClassicMacLd=false`.

## Critical Bugs Found

None in the standard build, test, CLI, gateway, or companion paths audited so far.

## High Severity

- High: MAF-enabled solution build fails.
  - Evidence: enabling `OpenClawEnableMafExperiment=true` produced missing namespace/type errors for `A2A`, `Microsoft.Agents.AI`, `ChatClientAgent`, and `AgentSession` in `OpenClaw.Tests`.
  - Impact: the documented optional MAF/AOT-JIT compatibility lane cannot be validated by a full solution build.
  - Fix: added direct conditional test package references for the MAF/A2A packages used by those test files.
  - Status: fixed; post-fix MAF-enabled solution build passes with 0 warnings.

- High: plugin entry containment can be bypassed through symlinked entry paths.
  - Evidence: `PluginDiscovery.FindEntryFile` returns common entry files such as `index.js` without a real-path containment check after resolving symlinks. Existing containment checks also only resolved the final path component, not symlinked parent directories.
  - Impact: a plugin directory could point its entry file or parent directory outside the plugin root while still appearing to be under the root path.
  - Fix: canonicalized paths segment-by-segment, resolved symlinked ancestors, and re-checked containment for manifest-discovered entry files.
  - Status: fixed with regression tests for manifest entry symlinks and package entries under symlinked parent directories.

## Medium Severity

- Medium: tool path policy canonicalization does not resolve symlinked parent directories for existing read targets.
  - Impact: allowed-root checks are stronger for non-existent write targets than for existing files reachable through symlinked directories.
  - Fix: uses segment-by-segment symlink resolution for all tool path checks and aligns contract-scope allowed roots with the same canonicalization.
  - Status: fixed with regression tests.

- Medium: Companion managed-gateway websocket URLs preserve query strings/fragments from the configured base URL.
  - Impact: a configured base URL containing query or fragment data can produce a confusing `/ws?...` URL.
  - Fix: clears query and fragment when constructing the websocket endpoint.
  - Status: fixed with regression coverage.

- Medium: the full test suite has a cold-machine network dependency.
  - Evidence: `BrowserToolTests.BrowserTool_CanNavigateAndGetText` downloaded Playwright Chromium, FFmpeg, and headless shell during `dotnet test`.
  - Impact: first-time contributors can see slow or network-sensitive test runs even when all product code is correct.
  - Fix: the test now serves a local loopback page, disables private-network URL blocking only for that local fixture, and uses Playwright's normal browser cache by default instead of a fresh per-run temp browser path. The first run can still download Playwright assets if none are installed.
  - Status: fixed as far as practical without removing browser coverage.

- Medium: streaming non-streaming tool progress is delayed until tool completion.
  - Impact: websocket clients see no `tool_start` for a long-running non-streaming tool or parallel non-streaming batch until the runtime has already finished the batch.
  - Fix: streaming now emits `tool_start` before executing non-streaming tools and preserves completion status/failure metadata in `tool_result`.
  - Status: fixed with regression coverage.

## Low Severity

- Low: CLI top-level usage omitted the existing `openclaw plugins <install|remove|list|search>` command even though plugin help/examples appeared lower in the same output. Fixed in this pass.
- Low: macOS gateway NativeAOT publish emits native linker/debug-info warnings. The CLI no longer uses `-ld_classic` by default; the gateway keeps it because the current toolchain failed to link the gateway without it. Release documentation now calls this out.

## Business Logic Mismatches

- The standard product identity and positioning are consistent: the code separates NativeAOT-friendly core/gateway/CLI paths from optional non-AOT adapters, and the README keeps the OpenClaw.NET identity.
- The MAF optional lane is documented as an experiment/optional compatibility surface; the flagged solution build now passes after adding direct test package references.
- The CLI exposes plugin management and top-level help now advertises the command in the usage block.

## Security And Safety Concerns

- Public-bind hardening and endpoint auth are covered by tests and implementation patterns; `/health/live` and `/health/ready` intentionally expose only minimal unauthenticated probe data.
- Plugin entry path containment needed symlink hardening.
- Tool read/write path policy needed equivalent ancestor-symlink hardening for existing files and non-existent targets.
- No evidence was found in this pass of provider API keys being passed on the command line by Companion setup; it passes them through a child-process environment reference.

## Performance And Resource Usage

- Standard build/test performance is acceptable for a large integration-heavy suite.
- The browser-tool test still may trigger a first-time Playwright asset install, but it no longer forces a fresh browser download path per run and no longer depends on `example.com`.
- Runtime tool execution has bounded timeouts, cancellation propagation, and bounded streaming collection. Streaming now emits `tool_start` before non-streaming tools execute, including parallel batches, so clients see progress earlier.

## NativeAOT And Trimming Concerns

- Standard CLI and gateway NativeAOT publish succeeded on osx-arm64.
- Gateway suppresses known package-level Playwright trim/AOT warnings while still surfacing project-owned diagnostics.
- Optional adapters are marked as non-AOT or conditional where appropriate.
- The MAF-enabled validation build failure was the main NativeAOT/JIT boundary confidence issue found; the flagged build now passes.
- The CLI osx-arm64 NativeAOT publish no longer carries the gateway's classic-linker fallback. The gateway still carries it intentionally because removing it reproduced an Apple linker assertion on this toolchain.

## Documentation Mismatches

- CLI help needed to list `openclaw plugins`; fixed in this pass.
- Contributor/onboarding docs needed to mention that the full test suite may install Playwright browser assets on a cold machine; `docs/QUICKSTART.md` now notes the one-time cache behavior and the isolation override.
- Release docs now note the gateway-specific macOS NativeAOT classic-linker fallback and when to revisit it.

## Recommended Follow-Up Work

- Monitor the first GitHub Actions run after these workflow changes, especially the PR-time Linux NativeAOT smoke and scheduled macOS linker probe.
- Remove the gateway `OpenClawUseClassicMacLd` default once the scheduled macOS probe links the gateway reliably without it.
- Consider an explicit browser-dependent test category only if CI or contributor environments need a fully offline default test command.

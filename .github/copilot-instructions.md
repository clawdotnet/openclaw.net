# Copilot Instructions for OpenClaw.NET

This repository implements a .NET-native OpenClaw-compatible runtime and gateway.

## Key rules

- Preserve NativeAOT friendliness unless a change is explicitly scoped to the JIT or experimental lane.
- Avoid introducing reflection-heavy or trim-unsafe dependencies into core runtime paths.
- Keep plugin compatibility explicit and fail-fast; do not silently degrade unsupported surfaces.
- Prefer small, composable changes over large rewrites.
- Do not broaden compatibility claims in docs without tests.
- Gateway concerns should stay separate from agent reasoning and runtime concerns where possible.

## Architecture priorities

- Core runtime should remain lightweight.
- Optional integrations must stay optional.
- Security and hardening take precedence over convenience for public-bind scenarios.
- New tool execution paths should fit behind the tool execution layer.

## Contribution expectations

When making changes:

- update tests where appropriate
- update docs if behavior changes
- call out AOT and JIT implications explicitly
- preserve existing configuration behavior unless intentionally changing it


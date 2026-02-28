# Semantic Kernel Interop

OpenClaw.NET can host `Microsoft.SemanticKernel` code behind OpenClaw's tool execution model.

This repo intentionally keeps Semantic Kernel integration **optional**:
- `src/OpenClaw.Gateway` remains SK-free and stays **NativeAOT-friendly**.
- SK support lives in `src/OpenClaw.SemanticKernelAdapter` and is designed for non-AOT hosts.

## What "interop" means here

OpenClaw does not attempt to replace Semantic Kernel.

Instead, SK runs *inside* a tool call, so OpenClaw can still enforce:
- authentication and gateway policy (in your host)
- tool approvals and allow/deny policies
- rate limiting / budgeting (in your host)
- OpenTelemetry tracing around execution

## Known-good configurations

| Scenario | Supported | Notes |
|---|---:|---|
| OpenClaw gateway NativeAOT publish | Yes | No SK dependency in the gateway.
| SK interop via adapter library | Yes | Intended for normal .NET apps (non-AOT).
| Sample host (`samples/OpenClaw.SemanticKernelInteropHost`) | Yes | Self-contained demo; not intended for NativeAOT.

## NativeAOT / trimming guidance

Semantic Kernel and some SK plugin patterns may rely on reflection and dynamic behaviors.

Recommendations:
1. Keep SK interop in a separate, non-AOT host process (recommended).
2. If you want AOT anyway: disable trimming for the SK host project and accept a larger binary.
3. Treat SK interop as best-effort and validate your exact plugin set under your chosen publish settings.

## Packages

- `OpenClaw.SemanticKernelAdapter`
  - Provides:
    - `semantic_kernel` entrypoint tool (invoke by plugin/function)
    - Per-function tools named `sk_<plugin>_<function>`
    - Optional governance hook (`SemanticKernelPolicyHook`) using `IToolHookWithContext`

## Production Readiness
The `OpenClaw.SemanticKernelAdapter` currently implements **all phases** of the Semantic Kernel interop roadmap. It is considered robust and production-ready for handling selective mapping, rate-limiting, and `IStreamingTool` context hooks.

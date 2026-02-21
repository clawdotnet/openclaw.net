---
title: "Building a Production AI Agent Gateway in .NET 10 with NativeAOT: Introducing OpenClaw.NET"
published: false
description: "A self-hosted agent gateway + runtime in C# with NativeAOT-friendly tooling, channels, and OpenTelemetry."
tags: dotnet, ai, csharp, nativeaot, llm
---

Today we’re open-sourcing **OpenClaw.NET** — a self-hosted agent gateway + runtime built for .NET teams who want a deployable service, not just a library.

If you’ve used popular agent frameworks in other ecosystems (like LangChain or AutoGen), you’ll recognize some familiar primitives (tools, memory, “agent loops”). The difference is that OpenClaw is intentionally **.NET-native** and **service-shaped**: configuration-first, AOT-friendly, with security and observability baked in.

We’re not trying to be a drop-in port of another framework. This is OpenClaw’s opinionated take on what it looks like to ship an “agent product” on .NET.

*Note: This post avoids claiming ownership of any domain. If you deploy OpenClaw publicly, you’ll use **your own** hostname (e.g. `openclaw.example.com`) behind your own TLS.*

## The Problem

Building an AI agent prototype is easy. Moving it to production is harder:

- Python dependencies can be difficult to manage and isolate in strict enterprise environments.
- TypeScript agents often bloat into complex “Node runtime + bundling + deployment” pipeline projects.
- Many popular frameworks are libraries, not runtimes. You still have to build the API surface, authentication, rate limiting, and observability yourself.

We wanted something different: **A deployable artifact.**

## Enter OpenClaw.NET

Built on **.NET 10** with a NativeAOT-first posture, OpenClaw is designed to be treated like infrastructure: you configure it, deploy it, and connect to it over WebSocket or a small set of HTTP endpoints.

### Features at a Glance

*   **Self-hosted gateway**: WebSocket-first control plane with optional webhooks (Telegram/Twilio).
*   **Provider-agnostic LLMs**: OpenAI, Azure OpenAI, Ollama, plus other OpenAI-compatible endpoints (e.g., LM Studio or an API gateway via `MODEL_PROVIDER_ENDPOINT`).
*   **Native tools**: Shell, file I/O, web fetch, interactive browser automation (Playwright), Git, code execution, PDF reading, and more. *All restricted by explicit opt-in flags.*
*   **Conversation branching**: Snapshot and restore session history (effectively "forking" a conversation state).
*   **Background jobs**: Cron-style scheduled prompts for persistent automated tasks.
*   **Resilience**: Intelligent retry/backoff, per-request timeouts, circuit breaking, and session-level token budgets.
*   **Observability**: OpenTelemetry logs/metrics/traces (OTLP exporter) alongside a built-in JSON `/metrics` endpoint.

## Why .NET 10 & NativeAOT?

We chose **.NET 10** and **NativeAOT** for three reasons:

1.  **Deployment Footprint**: NativeAOT compilation starts instantly (no JIT warmup) and generates a single binary. Docker images can easily stay under 50 MB.
2.  **Type Safety**: The entire tool execution pipeline is strongly typed, making parameter bindings from untrusted LLM outputs safe.
3.  **Ecosystem**: `Microsoft.Extensions.AI` gives us a clean provider abstraction without locking the core runtime to a specific vendor's SDK.

## The Architecture

OpenClaw isn't just a cognitive loop. It's a pipeline.

1.  **Gateway**: Handles WebSocket/HTTP connections and token-based authentication.
2.  **Middleware**: Enforces rate limiting, token budgeting limiters, and context tracking before the inner LLM is hit.
3.  **Agent Runtime**: The core operational loop (Think → Act → Observe).
4.  **Tool Sandbox**: Executes tools with strict permission boundaries (e.g., `AllowShell=false`, explicit `AllowedReadRoots`, limits on JavaScript bridging).
5.  **LLM Backend**: Handles provider complexity and system message injection.

**Session Branching**:
A unique capability of OpenClaw is branching memory states.

```csharp
// Fork the current session’s history via the gateway pipeline
var branchId = await sessionManager.BranchAsync(session, "exploring-sql-solution", ct);

// Restore that branch later to backtrack on a mistake
await sessionManager.RestoreBranchAsync(session, branchId, ct);
```

**Delegation**:
The gateway can be optionally configured to expose a `delegate_agent` tool, allowing the main agent process to spawn worker agents for specialized tasks.

## Getting Started

You can run OpenClaw locally with the .NET SDK. Tell it what LLM model backplane to use using environment variables:

```bash
export MODEL_PROVIDER_KEY="sk-..."
export MODEL_PROVIDER_ENDPOINT="https://api.openai.com/v1" # Optional if using OpenAI
dotnet run --project src/OpenClaw.Gateway -c Release
```

If you prefer containers, use Docker Compose (builds the image natively):

```bash
export MODEL_PROVIDER_KEY="sk-..."
export OPENCLAW_AUTH_TOKEN="$(openssl rand -hex 32)"
docker compose up -d openclaw
```

Then connect your frontend client via WebSocket:

```bash
ws://127.0.0.1:18789/ws
```

## Community & Ecosystem

OpenClaw is MIT licensed and built entirely in the open. 

*   **GitHub Repository**: `<REPO_URL>`
*   **Docs and Tutorials**: `<DOCS_URL>`
*   **Community Discord**: `<DISCORD_URL>`

If you're a .NET developer tired of porting Python libraries into your monolith, give OpenClaw a try. Let's build agents that are robust, compiled, and ready for production.

Happy coding!

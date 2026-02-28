# [Open Source] OpenClaw.NET — A NativeAOT AI Agent Framework with a TypeScript Plugin Bridge. Looking for collaborators!

**GitHub**: [https://github.com/clawdotnet/openclaw.net](https://github.com/clawdotnet/openclaw.net)  
**License**: MIT

---

Hey r/dotnet,

I've been building **OpenClaw.NET**, an open-source, NativeAOT-compatible AI agent framework written entirely in C# 13. It's an independent .NET port of the [OpenClaw](https://github.com/openclaw/openclaw) TypeScript agent framework — rewritten from scratch to take full advantage of the .NET ecosystem.

The motivation is simple: I wanted a way for .NET developers to have an OpenClaw equivalent in a language we understand best.

I wanted to share the project and also **put out a call for collaborators**, especially from folks with experience in areas where I'd love more expert eyes.

## What it does

OpenClaw.NET is a self-hosted gateway + agent runtime that orchestrates LLM tool-calling (ReAct loop) with built-in multi-channel support. The entire orchestration core compiles down to a **~23MB NativeAOT binary** — no JIT, no runtime dependencies.

**Key technical highlights:**

- **100% trim/AOT safe** — Heavy use of `System.Text.Json` source generators, zero runtime reflection. Getting dynamic tool schemas to work without reflection was one of the bigger engineering challenges.
- **`IAsyncEnumerable` streaming throughout** — Response streaming from LLM → agent → client is fully async.
- **Autonomy + approvals** — `readonly/supervised/full` autonomy modes, tool approval prompts, and a simple `/doctor` diagnostics report to validate setup and security posture.
- **TypeScript plugin bridge** — A JSON-RPC bridge over `stdin`/`stdout` that spawns a Node.js child process, loads existing OpenClaw TS/JS plugins via `jiti`, and pipes tool calls back to C#. Stray `console.log()` calls from plugins are intercepted and redirected to `stderr`, then routed into `Microsoft.Extensions.Logging` on the C# side.
- **Native channel adapters** — WhatsApp (Cloud API + custom bridges), Telegram (webhooks), and Twilio SMS — all zero-dependency, built directly on ASP.NET Core middleware.
- **Hardened security defaults** — Wildcard tooling roots, shell access, and plugin bridges are automatically blocked when binding to non-loopback addresses.
- **OpenTelemetry integration** — Structured logging, distributed traces, and `/health` + `/metrics` endpoints out of the box.
- **Docker-ready** — Multi-stage Dockerfile producing an Ubuntu Chiseled (distroless) image.

## Architecture

The repo is split into decoupled assemblies: `Core`, `Channels`, `Agent`, and `Gateway`. The gateway acts as a hub — WebSocket + webhook ingress, channel routing, and agent orchestration all handled by Kestrel.

## Looking for collaborators

This is where I'd especially appreciate the community's help. If any of these areas are your wheelhouse, I'd love contributions, feedback, or even just a code review:

- **NativeAOT / trimming experts** — I've gotten it working, but I'm sure there are patterns I'm missing or places where I'm leaving performance on the table. Would love someone who has deep AOT experience to review the source generator usage and trimming annotations.
- **ASP.NET Core middleware / Kestrel internals** — The WebSocket handling and webhook verification pipelines could probably be tighter. Anyone who has built production WebSocket servers on Kestrel, I'd really value your input.
- **Cross-process IPC / JSON-RPC** — The stdin/stdout bridge works well, but I'm curious if anyone has experience with more robust cross-process communication patterns in .NET (named pipes, Unix domain sockets, etc.) that could improve throughput or reliability.
- **Security review** — I've implemented auth token validation, Twilio signature verification, proxy header trust, and rate limiting, but a security-focused review from someone experienced in hardening ASP.NET Core APIs would be invaluable.
- **IMAP / email protocol** — I recently added an Inbox Zero tool (email triage via IMAP). If you have experience with `MailKit` or email protocol quirks, I'd love a review.
- **Testing** — Unit and integration test coverage can always be better. Contributions here are very welcome.

## How to try it

```bash
export MODEL_PROVIDER_KEY="your-api-key"
dotnet run --project src/OpenClaw.Gateway -c Release
# Connect via WebSocket at ws://127.0.0.1:18789/ws
```

There's also a cross-platform **Avalonia companion app** for a desktop GUI experience.

## Status

The project is functional and I'm actively using it as my daily agent. It's MIT licensed and I'm committed to keeping the Community Edition fully open source. Enterprise add-ons (when they come) will live in a separate repo.

---

Happy to answer any questions about the architecture, the AOT migration, or the plugin bridge. If you're interested in contributing, feel free to open an issue or PR, or just drop a comment here — I'd love to connect.

Thanks for reading!

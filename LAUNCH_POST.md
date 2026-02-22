# Show HN: I ported OpenClaw to .NET NativeAOT (23MB binary, TS plugin bridge)

**Link**: [https://github.com/clawdotnet/openclaw.net](https://github.com/clawdotnet/openclaw.net)

Hey HN,

I've been working on a NativeAOT-compatible .NET port of the popular OpenClaw AI agent framework. I love the original Node.js/TypeScript project, but I wanted something with a smaller memory footprint, faster startup times, and deeper integration into the enterprise C# ecosystem.

The biggest challenge with framework ports is usually the "cold start" problem—you lose the entire ecosystem of community plugins. To solve this, I built a high-performance Node.js JSON-RPC bridge directly into the gateway. 

Here's how it works and what it achieved:

### 1. The .NET NativeAOT Core
The orchestration core (ReAct loop, tool dispatch, WebSockets, Webhooks) is written completely in C# 13 and optimized for NativeAOT.
- **Footprint**: It compiles down to a single binary. The NativeAOT executable is just **23MB**.
- **Memory**: It idles at a fraction of the RAM compared to the Node.js/V8 equivalent, making it way easier to self-host on cheap VPS instances or tiny Raspberry Pis.
- **Security**: It has hardened public-bind defaults (rejecting wildcard tooling roots, disabling shell tools if bound publicly, and requiring tokens).

### 2. The TypeScript Plugin Bridge
I didn't want to lose the hundreds of community plugins built for the original OpenClaw. 
- The .NET gateway dynamically spawns a Node.js child process using `--experimental-vm-modules`.
- It loads original OpenClaw `.ts` or `.js` plugins (using `jiti` for on-the-fly TS compilation).
- Tools are bridged back to C# via a newline-delimited JSON-RPC pipe over `stdin`/`stdout`.
- **The fun part**: To prevent community plugins from accidentally corrupting the JSON-RPC stream via stray `console.log()` calls, the bridge intercepts `console.log` and `console.info`, redirecting them to `stderr`. The .NET parent process asynchronously reads that `stderr` pipe and routes it natively into the `Microsoft.Extensions.Logging` pipeline. You get full C# structured logging for JavaScript plugins without blocking the OS pipe buffers.

### 3. Built-in Channels
Instead of just a web/CLI interface, the gateway acts as a hub. It has native, zero-dependency adapters for:
- **WhatsApp**: Dual support for the Official Meta Cloud API (webhook verification, contextual replies) and custom bridges (like `whatsmeow`).
- **Telegram**: Native webhook support.
- **Twilio SMS**: Signature validation and strict number allowlists.

### 4. Code & Architecture
The repo is split into cleanly decoupled assemblies (`Core`, `Channels`, `Agent`, `Gateway`), heavily utilizing `IAsyncEnumerable` for streaming and `System.Text.Json` source generators to ensure 100% trim/AOT safety (no runtime reflection).

I'd love to hear your thoughts, especially from anyone who has wrestled with cross-language RPC pipes or AOT compilation in .NET!

Source code is available under the MIT license.

*(Happy to answer any questions about the architecture, the bridge, or the AOT migration process!)*

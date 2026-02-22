# Why I rewrote the OpenClaw AI Agent in .NET

*If you just want to see the code, the repository is [here on GitHub](https://github.com/clawdotnet/openclaw.net).*

The Agentic AI space is moving incredibly fast, and right now, it is dominated by two languages: Python and TypeScript. [OpenClaw](https://github.com/openclaw/openclaw) quickly emerged as one of the best open-source TypeScript frameworks for building capable AI agents, boasting a fantastic ecosystem of community plugins and a clean architecture.

But as I looked at the typical deployments for these agents—often requiring a full Node.js runtime, hefty memory footprints, and multi-second startup times—I couldn’t help but ask: **What if we built this in C#?**

Thus began **OpenClaw.NET**: an independent, clean-room .NET implementation of the OpenClaw architecture designed specifically for minimum footprint, maximum performance, and enterprise integration.

Here is the story of how it was built, the two major engineering hurdles I hit along the way, and why I believe strongly-typed compiled languages are the future of LLM orchestration.

---

## The Goal: NativeAOT from Day One

The primary objective was to build an agent runtime that wasn’t just "fast for C#", but fast *period*. That meant targeting **NativeAOT** (Ahead-of-Time compilation).

NativeAOT strips away the traditional .NET Just-In-Time (JIT) compiler. It compiles the C# code directly into a standalone, OS-specific machine code executable. The benefits are staggering:
*   **Startup time drops to sub-milliseconds.**
*   **Memory usage plummets** because there is no virtual machine or JIT compiler loaded into RAM.
*   **Deployment is trivial.** The NativeAOT executable is just **23MB** — a single, standalone binary with no runtime dependencies.

But NativeAOT comes with a very strict rule: **No runtime reflection.** 

And in the world of AI Agents—where the LLM returns unpredictable JSON that must be dynamically parsed into tool arguments—this rule became my biggest headache.

### Challenge 1: Dynamic Tool Schemas without Reflection

The ReAct (Reasoning and Acting) loop of an agent is heavily dependent on JSON serialization. The agent tells the LLM, "Here are the tools you can use," via a JSON Schema. The LLM then returns a JSON object saying, "Call the `read_file` tool with these arguments."

In standard .NET, `System.Text.Json` uses reflection to look at your C# classes at runtime and figure out how to parse the JSON. NativeAOT forbids this.

**The Solution:** Heavy reliance on **Source Generators**.
Instead of figuring out JSON mapping at runtime, C# 12/13 source generators analyze the code *during the build process* and generate highly optimized, strongly-typed parsing code. 

However, tools returning unconstrained dynamic structures (like arbitrary API payloads) still required careful handling. I had to build a custom `ToolResultContext` to map dynamic generic objects down to base `JsonElement` structures securely without triggering AOT warnings. This forced a much cleaner separation of concerns: the tools execute, map their outputs to strict shapes, and the framework simply shuttles bytes.

---

## The Cold Start Problem

The problem with porting any successful framework to a new language is the "Cold Start Problem". The original OpenClaw has a thriving community building plugins—tools to control Spotify, search Notion, read GitHub PRs, etc. 

I didn't want OpenClaw.NET users to have to wait for the C# community to rebuild all of those integrations. I wanted them to use the C# orchestration engine *immediately*, with the existing JavaScript ecosystem.

### Challenge 2: Spanning the Language Divide (The Plugin Bridge)

I needed the C# gateway to be able to seamlessly execute JavaScript tools. 

**Attempt 1:** Embedding V8. I initially looked into embedding a V8 engine (like ClearScript) directly into the .NET process. This violated the "keep it small and AOT-safe" principle immediately, blowing up the binary size and introducing complex memory-management interop.

**Attempt 2:** The JSON-RPC Bridge. The winning architecture was pure simplicity. 
If the user enables legacy plugins, the .NET gateway dynamically spawns a Node.js child process (`node --experimental-vm-modules`). The .NET process and the Node process talk to each other purely via standard input/output (`stdin`/`stdout`) using newline-delimited JSON-RPC.

When the Node.js bridge script starts, it uses [jiti](https://github.com/unjs/jiti) to load Original OpenClaw TypeScript plugins on the fly, intercepts their tool registrations, and sends those schemas back to C#.

When the C# Agent asks the LLM what to do, it passes along both the Native C# tools *and* the bridged TS tools. If the LLM chooses a TS tool, C# fires a JSON-RPC `execute()` payload through the pipe, Node runs the TS code, and the result pipes back.

**The Nightmare of `console.log`:**
This architecture worked perfectly—until a community plugin used `console.log()`. 
Because JSON-RPC was operating on `stdout`, a stray `console.log("Fetching data...")` from a JavaScript plugin would completely corrupt the JSON stream being parsed by C#.

To fix this, the Node.js bridge script forcibly intercepts `console.log` and `console.info` at startup and redirects them to `console.error` (`stderr`). Back in C#, the `.NET` process asynchronously reads the `stderr` pipe and intelligently routes those outputs directly into the C# `ILogger` pipeline. 

The result? You get the blazingly fast C# NativeAOT core orchestrating the LLM, seamlessly executing thousands of existing TypeScript plugins, with all of their logs appearing beautifully formatted in your C# console. 

---

## Why this matters

We are entering a phase where AI agents need to move out of the prototype phase (Python scripts in Jupyter notebooks) and into the enterprise data center.

Orchestrating complex LLM reasoning loops requires robust asynchronous primitives, memory safety, structural typing, and observable telemetry (like OpenTelemetry). C# and ASP.NET Core provide all of this natively.

By building OpenClaw.NET, we get the best of both worlds:
1.  **Enterprise Grade**: Built on ASP.NET Core Kestrel with full middleware, DI, and configuration support.
2.  **Edge Ready**: A 23MB standalone NativeAOT binary.
3.  **Ecosystem Rich**: Day-0 compatibility with original TypeScript plugins.

If you are interested in self-hosting an Agent or seeing how C# handles complex LLM orchestration, check out the repository! 

**[OpenClaw.NET on GitHub](https://github.com/clawdotnet/openclaw.net)**

*Pull requests, stars, and feedback are always welcome!*

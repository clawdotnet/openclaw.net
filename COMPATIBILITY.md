# Plugin Compatibility Guide

OpenClaw.NET is built to be a high-performance, NativeAOT-compatible implementation of the OpenClaw gateway. To maintain ecosystem compatibility, it includes a **Node.js Plugin Bridge** that allows you to run original TypeScript and JavaScript plugins.

## Compatibility Matrix

| Feature | Support | Compatibility Note |
| --- | --- | --- |
| **Tool Registration** | ✅ Full | `api.registerTool()` works exactly as expected. |
| **Tool Execution** | ✅ Full | Asynchronous and synchronous execution is supported. |
| **Background Services** | ✅ Full | `api.registerService()` lifecycle (`start`/`stop`) is fully supported. |
| **Stdout/Stderr Logs** | ✅ Full | Console logs are captured and prefix-routed to .NET logs. |
| **Configuration** | ✅ Full | `openclaw.plugin.json` settings are passed to the plugin. |
| **TypeScript (.ts)** | ✅ Partial | Requires `jiti` to be installed in the plugin's `node_modules`. |
| **ES Modules (.mjs)** | ✅ Full | Native support via Node.js. |
| **CommonJS (.js)** | ✅ Full | Supported via `createRequire`. |
| **Channels** | ⚠️ Limited | Channels can register, but the .NET Gateway handles routing differently. Prefer Native .NET channels for performance. |
| **Model Providers** | ❌ None | LLM provider logic must be implemented in C# for AOT safety. |

## Implementation Details

The bridge uses a **JSON-RPC 2.0** protocol over standard I/O pipes. This ensures zero network overhead and allows the .NET process to strictly control the lifecycle of the Node.js sub-process.

### TypeScript Support
If your plugin is written in TypeScript, the bridge will look for `jiti` in your plugin's dependency tree. If found, it will automatically transpile and load the plugin. 

> [!TIP]
> To ensure your TS plugins work, run `npm install jiti` in your plugin directory.

### Performance Considerations
While the bridge is optimized, native C# plugins (implemented in `src/OpenClaw.Agent/Plugins/Replicas`) will always be faster as they avoid the serialization overhead and the second Node.js process. 

For high-traffic production environments, consider porting mission-critical tools to the **Native Plugin API** in C#.

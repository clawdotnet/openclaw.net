/**
 * OpenClaw.NET Plugin Bridge
 *
 * This script is spawned as a child process by the .NET gateway.
 * It loads an OpenClaw TypeScript/JavaScript plugin using jiti,
 * captures tool registrations, and exposes them via stdin/stdout JSON-RPC.
 *
 * Protocol: newline-delimited JSON on stdin/stdout.
 * Methods:
 *   - init({ entryPath, pluginId, config })  → { tools: PluginToolRegistration[] }
 *   - execute({ name, params })              → { content: ToolContentItem[] }
 *   - shutdown()                             → { ok: true }
 */

import { createRequire } from "node:module";
import { createInterface } from "node:readline";
import { pathToFileURL } from "node:url";
import { existsSync } from "node:fs";
import { join, dirname } from "node:path";

/** @type {Map<string, { execute: Function, optional?: boolean }>} */
const registeredTools = new Map();

/** @type {Map<string, Function>} */
const registeredServices = new Map();

/** @type {Map<string, object>} */
const registeredChannels = new Map();

/**
 * Build the plugin API object that gets passed to the plugin's register function.
 */
function createPluginApi(pluginId, pluginConfig, logger) {
  return {
    pluginId,
    config: pluginConfig ?? {},
    logger,
    runtime: {
      // Stub — .NET gateway handles TTS natively if needed
      tts: {
        textToSpeechTelephony: async () => ({
          audio: Buffer.alloc(0),
          sampleRate: 8000,
        }),
      },
    },

    registerTool(def, opts) {
      const name = def.name;
      if (registeredTools.has(name)) {
        logger.warn(`Tool "${name}" already registered, skipping duplicate`);
        return;
      }

      // Normalize parameters to plain JSON Schema
      let parameters = def.parameters;
      if (parameters && typeof parameters === "object" && !parameters.type) {
        // TypeBox object — extract to plain schema
        parameters = JSON.parse(JSON.stringify(parameters));
      }

      registeredTools.set(name, {
        name,
        description: def.description ?? "",
        parameters: parameters ?? { type: "object", properties: {} },
        optional: opts?.optional ?? false,
        execute: def.execute,
      });
    },

    registerChannel(channelDef) {
      const id = channelDef?.id ?? "unknown";
      logger.warn(
        `Plugin "${pluginId}" registered channel "${id}". Note: OpenClaw.NET bridge does not currently activate bridged channels. Use native .NET channels if possible.`
      );
    },

    registerGatewayMethod(name, _handler) {
      logger.warn(
        `Plugin "${pluginId}" tried to register gateway method "${name}". Custom gateway RPC methods are not currently supported via the .NET bridge.`
      );
    },

    registerCli(factory, opts) {
      logger.warn(
        `Plugin "${pluginId}" tried to register CLI command. CLI extensions are not supported via the .NET bridge.`
      );
    },

    registerCommand(def) {
      logger.warn(
        `Plugin "${pluginId}" tried to register an auto-reply command. Auto-reply commands are not supported via the .NET bridge.`
      );
    },

    registerService(def) {
      const id = def.id ?? "unknown";
      logger.info(`Registering background service "${id}" for plugin "${pluginId}"`);
      registeredServices.set(id, def);
    },

    registerProvider(def) {
      logger.warn(
        `Plugin "${pluginId}" tried to register a model provider. Model provider authentication flows are not supported via the .NET bridge.`
      );
    },
  };
}

function createLogger(pluginId) {
  const prefix = `[plugin:${pluginId}]`;
  return {
    info: (...args) => console.error(prefix, "INFO", ...args),
    warn: (...args) => console.error(prefix, "WARN", ...args),
    error: (...args) => console.error(prefix, "ERROR", ...args),
    debug: (...args) => console.error(prefix, "DEBUG", ...args),
  };
}

/**
 * Load a plugin entry file using dynamic import (supports .ts via jiti if available,
 * or plain .js/.mjs).
 */
async function loadPlugin(entryPath) {
  const ext = entryPath.split(".").pop()?.toLowerCase();

  if (ext === "ts") {
    // Try loading via jiti for TypeScript support
    try {
      const jitiPath = findJiti(entryPath);
      if (jitiPath) {
        const { default: createJiti } = await import(jitiPath);
        const jiti = createJiti(entryPath, { interopDefault: true });
        return jiti(entryPath);
      }
    } catch {
      // Fall through to dynamic import
    }
  }

  // Standard ESM import
  const url = pathToFileURL(entryPath).href;
  const mod = await import(url);
  return mod.default ?? mod;
}

function findJiti(entryPath) {
  // Look for jiti in the plugin's node_modules, then global
  const dir = dirname(entryPath);
  const candidates = [
    join(dir, "node_modules", "jiti", "lib", "index.mjs"),
    join(dir, "node_modules", "jiti", "dist", "jiti.mjs"),
  ];

  // Walk up directories looking for node_modules/jiti
  let current = dir;
  for (let i = 0; i < 10; i++) {
    const candidate = join(current, "node_modules", "jiti", "lib", "index.mjs");
    if (existsSync(candidate)) return candidate;
    const candidate2 = join(current, "node_modules", "jiti", "dist", "jiti.mjs");
    if (existsSync(candidate2)) return candidate2;
    const parent = dirname(current);
    if (parent === current) break;
    current = parent;
  }

  return null;
}

// ── JSON-RPC handler ─────────────────────────────────────────────────

let pluginId = "unknown";
let logger = createLogger(pluginId);

async function handleRequest(req) {
  switch (req.method) {
    case "init": {
      const { entryPath, pluginId: pid, config } = req.params ?? {};
      pluginId = pid ?? "unknown";
      logger = createLogger(pluginId);

      try {
        const pluginExport = await loadPlugin(entryPath);
        const api = createPluginApi(pluginId, config, logger);

        if (typeof pluginExport === "function") {
          await pluginExport(api);
        } else if (pluginExport && typeof pluginExport.register === "function") {
          await pluginExport.register(api);
        } else {
          logger.warn("Plugin did not export a function or {register}");
        }

        // Start any registered services
        for (const [id, svc] of registeredServices) {
          try {
            if (typeof svc.start === "function") await svc.start();
          } catch (e) {
            logger.error(`Service "${id}" failed to start:`, e?.message);
          }
        }

        const tools = [];
        for (const [, tool] of registeredTools) {
          tools.push({
            name: tool.name,
            description: tool.description,
            parameters: tool.parameters,
            optional: tool.optional,
          });
        }

        return { tools };
      } catch (e) {
        throw new Error(`Failed to load plugin "${pluginId}": ${e?.message}`);
      }
    }

    case "execute": {
      const { name, params } = req.params ?? {};
      const tool = registeredTools.get(name);
      if (!tool) {
        throw new Error(`Unknown tool: ${name}`);
      }

      try {
        const result = await tool.execute(pluginId, params ?? {});

        // Normalize result to content array
        if (result && Array.isArray(result.content)) {
          return result;
        }
        if (typeof result === "string") {
          return { content: [{ type: "text", text: result }] };
        }
        if (result && typeof result.text === "string") {
          return { content: [{ type: "text", text: result.text }] };
        }

        return {
          content: [{ type: "text", text: JSON.stringify(result ?? null) }],
        };
      } catch (e) {
        return {
          content: [{ type: "text", text: `Error: ${e?.message ?? "unknown error"}` }],
        };
      }
    }

    case "shutdown": {
      // Stop services
      for (const [id, svc] of registeredServices) {
        try {
          if (typeof svc.stop === "function") await svc.stop();
        } catch (e) {
          logger.error(`Service "${id}" failed to stop:`, e?.message);
        }
      }
      // Allow the process to exit after responding
      setTimeout(() => process.exit(0), 100);
      return { ok: true };
    }

    default:
      throw new Error(`Unknown method: ${req.method}`);
  }
}

function sendResponse(id, result, error) {
  const resp = { id };
  if (error) {
    resp.error = { code: -1, message: String(error?.message ?? error) };
  } else {
    resp.result = result;
  }
  process.stdout.write(JSON.stringify(resp) + "\n");
}

// ── Main loop ────────────────────────────────────────────────────────

const rl = createInterface({ input: process.stdin, terminal: false });

rl.on("line", async (line) => {
  let req;
  try {
    req = JSON.parse(line);
  } catch {
    return; // Ignore malformed input
  }

  try {
    const result = await handleRequest(req);
    sendResponse(req.id, result, null);
  } catch (e) {
    sendResponse(req.id, null, e);
  }
});

rl.on("close", () => {
  process.exit(0);
});

// Keep process alive
process.stdin.resume();

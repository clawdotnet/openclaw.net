/**
 * OpenClaw.NET Plugin Bridge
 *
 * The bridge supports three transport modes:
 * - stdio: requests/responses/notifications over stdin/stdout
 * - socket: requests/responses/notifications over local IPC socket or named pipe
 * - hybrid: init over stdio, then runtime traffic over the socket transport
 */

import { createRequire } from "node:module";
import { createInterface } from "node:readline";
import { pathToFileURL } from "node:url";
import { existsSync } from "node:fs";
import { createConnection } from "node:net";
import { join, dirname } from "node:path";

console.log = console.error;
console.info = console.error;

/** @type {Map<string, { execute: Function, optional?: boolean, name: string, description: string, parameters: object }>} */
const registeredTools = new Map();

/** @type {Map<string, any>} */
const registeredServices = new Map();

/** @type {Map<string, { id: string, send?: Function, start?: Function, stop?: Function }>} */
const registeredChannels = new Map();

/** @type {Map<string, { name: string, description: string, handler: Function }>} */
const registeredCommands = new Map();

/** @type {Map<string, Function[]>} */
const registeredEventHandlers = new Map();

/** @type {Map<string, { id: string, models: string[], complete?: Function }>} */
const registeredProviders = new Map();

/** @type {Array<{severity: string, code: string, message: string, surface?: string, path?: string}>} */
let compatibilityDiagnostics = [];

/** @type {Set<string>} */
const startedChannels = new Set();

/** @type {"stdio" | "socket" | "hybrid"} */
let transportMode = normalizeMode(process.env.OPENCLAW_BRIDGE_TRANSPORT_MODE ?? "stdio");

/** @type {string | null} */
let socketPath = process.env.OPENCLAW_BRIDGE_SOCKET_PATH ?? null;

/** @type {import("node:net").Socket | null} */
let transportSocket = null;

/** @type {boolean} */
let shuttingDown = false;

/** @type {Promise<void>} */
let socketReadyPromise = Promise.resolve();

/** @type {(value?: void | PromiseLike<void>) => void} */
let resolveSocketReady = () => {};

/** @type {(reason?: any) => void} */
let rejectSocketReady = () => {};

if (transportMode === "socket" || transportMode === "hybrid") {
  socketReadyPromise = new Promise((resolve, reject) => {
    resolveSocketReady = resolve;
    rejectSocketReady = reject;
  });
  connectSocketTransport();
}

function normalizeMode(mode) {
  const normalized = String(mode ?? "stdio").trim().toLowerCase();
  if (normalized === "stdio" || normalized === "socket" || normalized === "hybrid") {
    return normalized;
  }

  return "stdio";
}

function connectSocketTransport() {
  if (!socketPath) {
    rejectSocketReady(new Error("Socket transport selected without OPENCLAW_BRIDGE_SOCKET_PATH."));
    return;
  }

  transportSocket = createConnection(socketPath);
  transportSocket.setEncoding("utf8");

  transportSocket.once("connect", () => {
    resolveSocketReady();
    attachSocketReader(transportSocket);
  });

  transportSocket.once("error", (error) => {
    console.error(`[plugin:${pluginId}] ERROR socket transport failed:`, error?.message ?? error);
    rejectSocketReady(error);
    if (!shuttingDown) {
      setTimeout(() => process.exit(1), 50);
    }
  });

  transportSocket.on("close", () => {
    if (!shuttingDown && transportMode !== "stdio") {
      setTimeout(() => process.exit(1), 50);
    }
  });
}

function attachSocketReader(socket) {
  const rl = createInterface({ input: socket, terminal: false });
  rl.on("line", (line) => {
    handleInboundLine(line, "socket");
  });
}

function resetState() {
  registeredTools.clear();
  registeredServices.clear();
  registeredChannels.clear();
  registeredCommands.clear();
  registeredEventHandlers.clear();
  registeredProviders.clear();
  compatibilityDiagnostics = [];
  startedChannels.clear();
}

function addDiagnostic(code, message, surface, path) {
  compatibilityDiagnostics.push({
    severity: "error",
    code,
    message,
    surface,
    path,
  });
}

function defaultNotificationChannel() {
  if ((transportMode === "socket" || transportMode === "hybrid") && transportSocket && !transportSocket.destroyed) {
    return "socket";
  }

  return "stdio";
}

function writeMessage(channel, payload) {
  const line = JSON.stringify(payload) + "\n";

  if (channel === "socket") {
    if (!transportSocket || transportSocket.destroyed) {
      throw new Error("Socket transport is not connected.");
    }

    transportSocket.write(line);
    return;
  }

  process.stdout.write(line);
}

function sendNotification(notificationType, params) {
  writeMessage(defaultNotificationChannel(), { notification: notificationType, params });
}

function sendResponse(id, result, error, channel) {
  const resp = { id };
  if (error) {
    resp.error = { code: -1, message: String(error?.message ?? error) };
  } else {
    resp.result = result;
  }

  writeMessage(channel, resp);
}

function collectCapabilities() {
  const capabilities = [];

  if (registeredTools.size > 0) capabilities.push("tools");
  if (registeredServices.size > 0) capabilities.push("services");
  if (registeredChannels.size > 0) capabilities.push("channels");
  if (registeredCommands.size > 0) capabilities.push("commands");
  if (registeredProviders.size > 0) capabilities.push("providers");
  if (registeredEventHandlers.size > 0) capabilities.push("hooks");

  return capabilities;
}

function getParam(params, name) {
  if (!params || typeof params !== "object") {
    return undefined;
  }

  const pascal = name.charAt(0).toUpperCase() + name.slice(1);
  return params[name] ?? params[pascal];
}

function createPluginApi(pluginId, pluginConfig, logger) {
  return {
    pluginId,
    config: pluginConfig ?? {},
    pluginConfig: pluginConfig ?? {},
    logger,
    runtime: {
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

      let parameters = def.parameters;
      if (parameters && typeof parameters === "object") {
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
      registeredChannels.set(id, {
        id,
        send: channelDef.send ?? channelDef.onMessage,
        start: channelDef.start,
        stop: channelDef.stop,
      });
      if (channelDef) {
        channelDef.receive = (msg) => {
          sendNotification("channel_message", { channelId: id, ...msg });
        };
      }
      logger.info(`Channel "${id}" registered`);
    },

    registerGatewayMethod(name, _handler) {
      const message =
        `Plugin "${pluginId}" tried to register gateway method "${name}", but custom gateway methods are not supported by OpenClaw.NET.`;
      logger.error(message);
      addDiagnostic("unsupported_gateway_method", message, "registerGatewayMethod", name);
    },

    registerCli(_factory, _opts) {
      const message =
        `Plugin "${pluginId}" tried to register a CLI command, but CLI extensions are not supported by OpenClaw.NET.`;
      logger.error(message);
      addDiagnostic("unsupported_cli_registration", message, "registerCli");
    },

    registerCommand(def) {
      const name = def?.name ?? def?.id ?? "unknown";
      registeredCommands.set(name, {
        name,
        description: def?.description ?? "",
        handler: def?.handler ?? def?.execute,
      });
      logger.info(`Command "${name}" registered`);
    },

    registerService(def) {
      const id = def.id ?? "unknown";
      logger.info(`Registering background service "${id}" for plugin "${pluginId}"`);
      registeredServices.set(id, def);
    },

    registerProvider(def) {
      const id = def?.id ?? "unknown";
      registeredProviders.set(id, {
        id,
        models: def?.models ?? [],
        complete: def?.complete ?? def?.execute,
      });
      logger.info(`Provider "${id}" registered`);
    },

    on(eventName, handler) {
      if (!registeredEventHandlers.has(eventName)) {
        registeredEventHandlers.set(eventName, []);
      }
      registeredEventHandlers.get(eventName).push(handler);
      logger.info(`Event hook "${eventName}" registered`);
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

async function loadPlugin(entryPath) {
  const ext = entryPath.split(".").pop()?.toLowerCase();
  const entryUrl = pathToFileURL(entryPath).href;

  if (ext === "ts") {
    const jitiPath = findJiti(entryPath);
    if (!jitiPath) {
      throw new Error(
        `TypeScript plugin "${entryPath}" requires the 'jiti' package in the plugin dependency tree. Run 'npm install jiti' in the plugin directory.`
      );
    }

    try {
      const { default: createJiti } = await import(pathToFileURL(jitiPath).href);
      const jiti = createJiti(entryUrl, { interopDefault: true });
      return jiti(entryUrl);
    } catch (e) {
      throw new Error(
        `Failed to load TypeScript plugin "${entryPath}" via jiti: ${e?.message ?? "unknown error"}. Ensure 'jiti' is installed and the plugin is valid.`
      );
    }
  }

  if (ext === "js" || ext === "cjs") {
    try {
      const req = createRequire(pathToFileURL(entryPath));
      const mod = req(entryPath);
      return mod?.default ?? mod;
    } catch {
      // Fall through to dynamic import for ESM-style .js packages.
    }
  }

  const mod = await import(entryUrl);
  return mod.default ?? mod;
}

function findJiti(entryPath) {
  const dir = dirname(entryPath);
  let current = dir;
  for (let i = 0; i < 10; i++) {
    const candidates = [
      join(current, "node_modules", "jiti", "lib", "index.mjs"),
      join(current, "node_modules", "jiti", "lib", "jiti.mjs"),
      join(current, "node_modules", "jiti", "lib", "jiti.cjs"),
      join(current, "node_modules", "jiti", "dist", "jiti.mjs"),
      join(current, "node_modules", "jiti", "dist", "jiti.cjs"),
      join(current, "jiti", "lib", "index.mjs"),
      join(current, "jiti", "lib", "jiti.mjs"),
      join(current, "jiti", "lib", "jiti.cjs"),
      join(current, "jiti", "dist", "jiti.mjs"),
      join(current, "jiti", "dist", "jiti.cjs"),
    ];
    for (const candidate of candidates) {
      if (existsSync(candidate)) return candidate;
    }
    const parent = dirname(current);
    if (parent === current) break;
    current = parent;
  }

  return null;
}

let pluginId = "unknown";
let logger = createLogger(pluginId);

async function handleRequest(req) {
  switch (req.method) {
    case "init": {
      const entryPath = getParam(req.params, "entryPath");
      const pid = getParam(req.params, "pluginId");
      const config = getParam(req.params, "config");
      const transport = getParam(req.params, "transport");
      pluginId = pid ?? "unknown";
      transportMode = normalizeMode(getParam(transport, "mode") ?? transportMode);
      socketPath = getParam(transport, "socketPath") ?? socketPath;
      logger = createLogger(pluginId);
      resetState();

      try {
        const pluginExport = await loadPlugin(entryPath);
        const api = createPluginApi(pluginId, config, logger);

        if (typeof pluginExport === "function") {
          await pluginExport(api);
        } else if (pluginExport && typeof pluginExport.register === "function") {
          await pluginExport.register(api);
        } else {
          const message = `Plugin "${pluginId}" did not export a function or { register } API.`;
          logger.error(message);
          addDiagnostic("invalid_plugin_export", message, "register");
        }

        if (compatibilityDiagnostics.length > 0) {
          return {
            tools: [],
            channels: [],
            commands: [],
            eventSubscriptions: [],
            providers: [],
            capabilities: collectCapabilities(),
            compatible: false,
            diagnostics: compatibilityDiagnostics,
          };
        }

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

        const channels = [];
        for (const [, ch] of registeredChannels) {
          channels.push({ id: ch.id });
        }

        const commands = [];
        for (const [, cmd] of registeredCommands) {
          commands.push({ name: cmd.name, description: cmd.description });
        }

        const eventSubscriptions = [...registeredEventHandlers.keys()];

        const providers = [];
        for (const [, prov] of registeredProviders) {
          providers.push({ id: prov.id, models: prov.models });
        }

        return {
          tools,
          channels,
          commands,
          eventSubscriptions,
          providers,
          capabilities: collectCapabilities(),
          compatible: true,
          diagnostics: compatibilityDiagnostics,
        };
      } catch (e) {
        throw new Error(`Failed to load plugin "${pluginId}": ${e?.message}`);
      }
    }

    case "execute": {
      const name = getParam(req.params, "name");
      const params = getParam(req.params, "params");
      const tool = registeredTools.get(name);
      if (!tool) {
        throw new Error(`Unknown tool: ${name}`);
      }

      try {
        const result = await tool.execute(pluginId, params ?? {});

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

    case "channel_start": {
      const channelId = getParam(req.params, "channelId");
      const ch = registeredChannels.get(channelId);
      if (!ch) throw new Error(`Unknown channel: ${channelId}`);
      if (typeof ch.start === "function") {
        await ch.start();
      }
      startedChannels.add(channelId);
      return { ok: true };
    }

    case "channel_send": {
      const channelId = getParam(req.params, "channelId");
      const recipientId = getParam(req.params, "recipientId");
      const text = getParam(req.params, "text");
      const ch = registeredChannels.get(channelId);
      if (!ch) throw new Error(`Unknown channel: ${channelId}`);
      if (typeof ch.send === "function") {
        await ch.send({ channelId, recipientId, text });
      }
      return { ok: true };
    }

    case "channel_stop": {
      const channelId = getParam(req.params, "channelId");
      await stopChannel(channelId);
      return { ok: true };
    }

    case "command_execute": {
      const name = getParam(req.params, "name");
      const args = getParam(req.params, "args");
      const cmd = registeredCommands.get(name);
      if (!cmd) throw new Error(`Unknown command: ${name}`);
      if (typeof cmd.handler === "function") {
        const result = await cmd.handler(args ?? "");
        return { result: typeof result === "string" ? result : JSON.stringify(result ?? null) };
      }
      return { result: "" };
    }

    case "hook_before": {
      const eventName = getParam(req.params, "eventName");
      const toolName = getParam(req.params, "toolName");
      const toolArgs = getParam(req.params, "arguments");
      const handlers = registeredEventHandlers.get(eventName) ?? [];
      let allow = true;
      for (const handler of handlers) {
        try {
          const result = await handler({ toolName, arguments: toolArgs, phase: "before" });
          if (result === false || (result && result.allow === false)) {
            allow = false;
            break;
          }
        } catch (e) {
          logger.error(`Event hook "${eventName}" threw:`, e?.message);
        }
      }
      return { allow };
    }

    case "hook_after": {
      const eventName = getParam(req.params, "eventName");
      const toolName = getParam(req.params, "toolName");
      const toolArgs = getParam(req.params, "arguments");
      const result = getParam(req.params, "result");
      const durationMs = getParam(req.params, "durationMs");
      const failed = getParam(req.params, "failed");
      const handlers = registeredEventHandlers.get(eventName) ?? [];
      for (const handler of handlers) {
        try {
          await handler({ toolName, arguments: toolArgs, result, duration: durationMs, failed, phase: "after" });
        } catch (e) {
          logger.error(`Event hook "${eventName}" threw:`, e?.message);
        }
      }
      return { ok: true };
    }

    case "provider_complete": {
      const providerId = getParam(req.params, "providerId");
      const messages = getParam(req.params, "messages");
      const options = getParam(req.params, "options");
      const prov = registeredProviders.get(providerId);
      if (!prov) throw new Error(`Unknown provider: ${providerId}`);
      if (typeof prov.complete === "function") {
        return await prov.complete({ messages, options });
      }
      throw new Error(`Provider "${providerId}" has no complete handler`);
    }

    case "shutdown": {
      shuttingDown = true;

      for (const channelId of [...startedChannels]) {
        await stopChannel(channelId);
      }

      for (const [id, svc] of registeredServices) {
        try {
          if (typeof svc.stop === "function") await svc.stop();
        } catch (e) {
          logger.error(`Service "${id}" failed to stop:`, e?.message);
        }
      }

      setTimeout(() => process.exit(0), 100);
      return { ok: true };
    }

    default:
      throw new Error(`Unknown method: ${req.method}`);
  }
}

async function stopChannel(channelId) {
  if (!startedChannels.has(channelId)) {
    return;
  }

  startedChannels.delete(channelId);
  const ch = registeredChannels.get(channelId);
  if (!ch) {
    return;
  }

  try {
    if (typeof ch.stop === "function") {
      await ch.stop();
    }
  } catch (e) {
    logger.error(`Channel "${channelId}" failed to stop:`, e?.message);
  }
}

function handleInboundLine(line, channel) {
  let req;
  try {
    req = JSON.parse(line);
  } catch {
    return;
  }

  if (channel === "stdio" && transportMode === "hybrid" && req.method !== "init" && req.method !== "shutdown") {
    sendResponse(req.id, null, new Error(`Unsupported stdio method in hybrid mode: ${req.method}`), channel);
    return;
  }

  void (async () => {
    try {
      if (channel === "socket") {
        await socketReadyPromise;
      }
      const result = await handleRequest(req);
      sendResponse(req.id, result, null, channel);
    } catch (e) {
      sendResponse(req.id, null, e, channel);
    }
  })();
}

if (transportMode === "stdio" || transportMode === "hybrid") {
  const rl = createInterface({ input: process.stdin, terminal: false });
  rl.on("line", (line) => {
    handleInboundLine(line, "stdio");
  });
  rl.on("close", () => {
    if (transportMode === "stdio") {
      process.exit(0);
    }
  });
  process.stdin.resume();
}

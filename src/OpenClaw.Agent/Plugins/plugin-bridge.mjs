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

const standaloneCliMode = process.argv[2] === "--cli-run";
const standaloneCliDescribeMode = process.argv[2] === "--cli-describe";

// Runtime bridge traffic owns stdout. Standalone CLI execution inherits stdout
// so plugin commands can render output and use an interactive terminal.
if (!standaloneCliMode) {
  console.log = console.error;
  console.info = console.error;
}

/** @type {Map<string, { execute: Function, optional?: boolean, name: string, description: string, parameters: object }>} */
const registeredTools = new Map();

/** @type {Map<string, any>} */
const registeredServices = new Map();

/** @type {Map<string, { id: string, send?: Function, start?: Function, stop?: Function }>} */
const registeredChannels = new Map();

/** @type {Map<string, { name: string, description: string, handler: Function }>} */
const registeredCommands = new Map();

/** @type {Array<{ factory: Function, options: any }>} */
const registeredCliFactories = [];

/** @type {CliCommandNode | null} */
let registeredCliProgram = null;

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

/** @type {string | null} */
let socketAuthToken = process.env.OPENCLAW_BRIDGE_SOCKET_AUTH_TOKEN ?? null;

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

let pluginId = "unknown";
let logger = createLogger(pluginId);

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
    if (socketAuthToken) {
      transportSocket.write(`${JSON.stringify({ type: "bridge_auth", token: socketAuthToken })}\n`);
    }
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
  registeredCliFactories.length = 0;
  registeredCliProgram = null;
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
  if (registeredCliFactories.length > 0) capabilities.push("cli");
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

function createPluginApi(pluginId, pluginConfig, logger, registrationMode = "full") {
  return {
    pluginId,
    registrationMode,
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
        outputSchema: def.outputSchema ?? def.returnSchema ?? null,
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
        typing: channelDef.typing,
        readReceipt: channelDef.readReceipt,
        react: channelDef.react,
      });
      if (channelDef) {
        channelDef.receive = (msg) => {
          sendNotification("channel_message", { channelId: id, ...msg });
        };
        channelDef.emitAuthEvent = (evt) => {
          sendNotification("channel_auth_event", { channelId: id, ...evt });
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

    registerCli(factory, options) {
      if (typeof factory !== "function") {
        const message = `Plugin "${pluginId}" passed a non-function registrar to registerCli().`;
        logger.error(message);
        addDiagnostic("invalid_cli_registration", message, "registerCli");
        return;
      }

      registeredCliFactories.push({ factory, options: options ?? {} });
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

class CliCommandNode {
  constructor(signature = "", parent = null) {
    const tokens = String(signature).trim().split(/\s+/).filter(Boolean);
    this._name = tokens.shift() ?? "";
    this.parent = parent;
    this.commands = [];
    this.options = [];
    this._arguments = tokens.map(parseCliArgument);
    this._description = "";
    this._aliases = [];
    this._action = null;
    this._parsedOptions = {};
    this._usage = "";
  }

  command(signature, description) {
    const child = new CliCommandNode(signature, this);
    if (typeof description === "string") child.description(description);
    this.commands.push(child);
    return child;
  }

  addCommand(command) {
    if (command && typeof command === "object") {
      command.parent = this;
      this.commands.push(command);
    }
    return this;
  }

  description(value) {
    if (value === undefined) return this._description;
    this._description = sanitizeCliText(value);
    return this;
  }

  summary(value) { return this.description(value); }
  name() { return this._name; }
  alias(value) { this._aliases.push(String(value)); return this; }
  aliases(values) { this._aliases.push(...(values ?? []).map(String)); return this; }
  usage(value) { if (value === undefined) return this._usage; this._usage = String(value); return this; }

  argument(spec, description, defaultValue) {
    const argument = parseCliArgument(spec);
    argument.description = sanitizeCliText(description ?? "");
    argument.defaultValue = defaultValue;
    this._arguments.push(argument);
    return this;
  }

  arguments(spec) {
    for (const token of String(spec).trim().split(/\s+/).filter(Boolean)) {
      this._arguments.push(parseCliArgument(token));
    }
    return this;
  }

  option(flags, description, defaultValue) {
    this.options.push(parseCliOption(flags, description, defaultValue, false));
    return this;
  }

  requiredOption(flags, description, defaultValue) {
    this.options.push(parseCliOption(flags, description, defaultValue, true));
    return this;
  }

  addOption(option) {
    if (option && typeof option === "object") this.options.push(normalizeExternalCliOption(option));
    return this;
  }

  action(handler) { this._action = handler; return this; }
  opts() { return { ...this._parsedOptions }; }
  optsWithGlobals() { return this.opts(); }
  getOptionValue(name) { return this._parsedOptions[name]; }
  setOptionValue(name, value) { this._parsedOptions[name] = value; return this; }
  setOptionValueWithSource(name, value) { return this.setOptionValue(name, value); }

  // Commander configuration methods used by plugins during registration. The
  // bridge owns parsing and help rendering, so these remain safe fluent no-ops.
  allowUnknownOption() { return this; }
  allowExcessArguments() { return this; }
  passThroughOptions() { return this; }
  enablePositionalOptions() { return this; }
  showHelpAfterError() { return this; }
  showSuggestionAfterError() { return this; }
  configureHelp() { return this; }
  configureOutput() { return this; }
  helpOption() { return this; }
  addHelpText() { return this; }
  exitOverride() { return this; }
  hook() { return this; }
  version() { return this; }
}

function parseCliArgument(spec) {
  const text = String(spec ?? "").trim();
  const required = text.startsWith("<");
  const optional = text.startsWith("[");
  const inner = required || optional ? text.slice(1, -1) : text;
  const variadic = inner.endsWith("...");
  return {
    name: variadic ? inner.slice(0, -3) : inner,
    required,
    variadic,
    description: "",
    defaultValue: undefined,
  };
}

function parseCliOption(flags, description, defaultValue, requiredOption) {
  const text = String(flags ?? "");
  const parts = text.split(/[ ,|]+/).filter(Boolean);
  const long = parts.find((part) => part.startsWith("--")) ?? null;
  const short = parts.find((part) => /^-[^-]/.test(part)) ?? null;
  const valueToken = parts.find((part) => part.startsWith("<") || part.startsWith("["));
  const rawName = (long ?? short ?? "").replace(/^-+/, "").replace(/^no-/, "");
  return {
    flags: text,
    long: long?.split(/[<[\s]/, 1)[0] ?? null,
    short: short?.split(/[<[\s]/, 1)[0] ?? null,
    name: toCamelCase(rawName),
    requiredValue: Boolean(valueToken?.startsWith("<")),
    optionalValue: Boolean(valueToken?.startsWith("[")),
    requiredOption,
    negate: Boolean(long?.startsWith("--no-")),
    defaultValue,
    description: sanitizeCliText(description ?? ""),
  };
}

function normalizeExternalCliOption(option) {
  const normalized = parseCliOption(
    option.flags ?? `${option.short ?? ""} ${option.long ?? ""}`,
    option.description ?? "",
    option.defaultValue,
    option.mandatory === true,
  );
  if (typeof option.attributeName === "function") normalized.name = option.attributeName();
  return normalized;
}

function toCamelCase(value) {
  return String(value).replace(/-([a-zA-Z0-9])/g, (_, char) => char.toUpperCase());
}

function sanitizeCliText(value) {
  return String(value ?? "")
    .replace(/[\u001b\u009b][[\]()#;?]*(?:(?:(?:[a-zA-Z\d]*(?:;[-a-zA-Z\d\/#&.:=?%@~_]+)*)?\u0007)|(?:(?:\d{1,4}(?:;\d{0,4})*)?[\dA-PR-TZcf-nq-uy=><~]))/g, "")
    .replace(/[\u0000-\u0008\u000b\u000c\u000e-\u001f\u007f]/g, "")
    .slice(0, 500);
}

function isValidCliRootName(name) {
  return /^[A-Za-z0-9][A-Za-z0-9_-]*$/.test(String(name ?? ""));
}

async function buildCliProgram(metadataOnly = false) {
  const program = new CliCommandNode();
  const declaredRoots = new Map();

  for (const registration of registeredCliFactories) {
    const descriptors = Array.isArray(registration.options?.descriptors)
      ? registration.options.descriptors
      : [];
    for (const descriptor of descriptors) {
      if (descriptor && typeof descriptor.name === "string") {
        declaredRoots.set(descriptor.name, sanitizeCliText(descriptor.description ?? ""));
      }
    }

    const commands = Array.isArray(registration.options?.commands)
      ? registration.options.commands
      : [];
    for (const command of commands) declaredRoots.set(String(command), "");

    // Modern descriptors and the legacy commands list are sufficient for
    // non-activating root discovery. Registrars without metadata use the eager
    // compatibility fallback so older plugins do not disappear.
    if (metadataOnly && (descriptors.length > 0 || commands.length > 0)) continue;

    try {
      await registration.factory({ program });
    } catch (error) {
      const message = `Plugin "${pluginId}" CLI registrar failed: ${error?.message ?? error}`;
      logger.error(message);
      addDiagnostic("cli_registration_failed", message, "registerCli");
    }
  }

  for (const command of program.commands) {
    if (!isValidCliRootName(command.name())) {
      const message = `Plugin "${pluginId}" registered invalid CLI root "${command.name()}".`;
      addDiagnostic("invalid_cli_command_name", message, "registerCli", command.name());
    }
  }

  for (const name of declaredRoots.keys()) {
    if (!isValidCliRootName(name)) {
      const message = `Plugin "${pluginId}" declared invalid CLI root "${name}".`;
      addDiagnostic("invalid_cli_command_name", message, "registerCli", name);
    }
  }

  const seenRoots = new Set();
  for (const command of program.commands) {
    if (seenRoots.has(command.name())) {
      const message = `Plugin "${pluginId}" registered duplicate CLI root "${command.name()}".`;
      addDiagnostic("duplicate_cli_command_name", message, "registerCli", command.name());
    }
    seenRoots.add(command.name());
  }

  registeredCliProgram = program;
  const registrations = program.commands.map((command) => ({
    name: command.name(),
    description: declaredRoots.get(command.name()) || command.description() || "",
  }));
  for (const [name, description] of declaredRoots) {
    if (!registrations.some((command) => command.name === name)) {
      registrations.push({ name, description });
    }
  }
  return registrations;
}

function findCliChild(command, token) {
  return command.commands.find((child) => child.name() === token || child._aliases.includes(token));
}

function parseCliInvocation(command, argv) {
  const options = {};
  for (const option of command.options) {
    if (option.defaultValue !== undefined) options[option.name] = option.defaultValue;
    else if (option.negate) options[option.name] = true;
  }

  const positionals = [];
  let index = 0;
  while (index < argv.length) {
    const token = argv[index];
    if (token === "--") {
      positionals.push(...argv.slice(index + 1));
      break;
    }

    const [flag, inlineValue] = token.startsWith("--") && token.includes("=")
      ? [token.slice(0, token.indexOf("=")), token.slice(token.indexOf("=") + 1)]
      : [token, undefined];
    const option = command.options.find((candidate) => candidate.long === flag || candidate.short === flag);
    if (!option) {
      if (token.startsWith("-")) throw new Error(`Unknown option: ${token}`);
      positionals.push(token);
      index++;
      continue;
    }

    if (option.negate) {
      options[option.name] = false;
    } else if (option.requiredValue || option.optionalValue) {
      const next = inlineValue ?? argv[index + 1];
      const nextFlag = typeof next === "string" && next.startsWith("-")
        ? next.slice(0, next.includes("=") ? next.indexOf("=") : undefined)
        : undefined;
      const nextIsKnownOption = inlineValue === undefined && nextFlag !== undefined &&
        command.options.some((candidate) => candidate.long === nextFlag || candidate.short === nextFlag);
      if (next === undefined || (option.requiredValue && nextIsKnownOption)) {
        throw new Error(`Option ${flag} requires a value.`);
      }
      if (option.optionalValue && nextIsKnownOption) {
        options[option.name] = true;
      } else {
        options[option.name] = next;
        if (inlineValue === undefined) index++;
      }
    } else {
      options[option.name] = true;
    }
    index++;
  }

  for (const option of command.options) {
    if (option.requiredOption && options[option.name] === undefined) {
      throw new Error(`Required option missing: ${option.long ?? option.short}`);
    }
  }

  const actionArgs = [];
  let positionalIndex = 0;
  for (const argument of command._arguments) {
    if (argument.variadic) {
      const rest = positionals.slice(positionalIndex);
      if (argument.required && rest.length === 0) throw new Error(`Missing required argument: ${argument.name}`);
      actionArgs.push(rest.length > 0 ? rest : argument.defaultValue);
      positionalIndex = positionals.length;
      continue;
    }
    const value = positionals[positionalIndex] ?? argument.defaultValue;
    if (argument.required && value === undefined) throw new Error(`Missing required argument: ${argument.name}`);
    actionArgs.push(value);
    if (positionalIndex < positionals.length) positionalIndex++;
  }
  if (positionalIndex < positionals.length) throw new Error(`Unexpected argument: ${positionals[positionalIndex]}`);

  command._parsedOptions = options;
  return [...actionArgs, options, command];
}

function renderCliHelp(command) {
  const path = [];
  for (let current = command; current && current.name(); current = current.parent) path.unshift(current.name());
  console.log(`Usage: openclaw ${path.join(" ")}${command.commands.length > 0 ? " <command>" : ""}`);
  if (command.description()) console.log(`\n${command.description()}`);
  if (command.commands.length > 0) {
    console.log("\nCommands:");
    for (const child of command.commands) {
      console.log(`  ${child.name().padEnd(20)} ${child.description()}`.trimEnd());
    }
  }
  if (command.options.length > 0) {
    console.log("\nOptions:");
    for (const option of command.options) {
      console.log(`  ${option.flags.padEnd(24)} ${option.description}`.trimEnd());
    }
  }
}

async function executeCli(argv) {
  if (!registeredCliProgram) await buildCliProgram();
  let command = registeredCliProgram;
  let index = 0;
  while (index < argv.length) {
    const child = findCliChild(command, argv[index]);
    if (!child) break;
    command = child;
    index++;
  }

  if (command === registeredCliProgram) throw new Error(`Unknown plugin CLI command: ${argv[0] ?? ""}`);
  const remaining = argv.slice(index);
  if (remaining.includes("-h") || remaining.includes("--help") || (!command._action && command.commands.length > 0)) {
    renderCliHelp(command);
    return 0;
  }
  if (typeof command._action !== "function") throw new Error(`No action registered for: ${argv.slice(0, index).join(" ")}`);

  const actionArgs = parseCliInvocation(command, remaining);
  const result = await command._action(...actionArgs);
  if (typeof result === "number") return result;
  return Number.isInteger(process.exitCode) ? process.exitCode : 0;
}

function createLogger(pluginId) {
  const prefix = `[plugin:${pluginId}]`;
  const quietStandalone = standaloneCliMode || standaloneCliDescribeMode;
  return {
    info: quietStandalone ? () => {} : (...args) => console.error(prefix, "INFO", ...args),
    warn: (...args) => console.error(prefix, "WARN", ...args),
    error: (...args) => console.error(prefix, "ERROR", ...args),
    debug: quietStandalone ? () => {} : (...args) => console.error(prefix, "DEBUG", ...args),
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
        const api = createPluginApi(pluginId, config, logger, "full");

        if (typeof pluginExport === "function") {
          await pluginExport(api);
        } else if (pluginExport && typeof pluginExport.register === "function") {
          await pluginExport.register(api);
        } else {
          const message = `Plugin "${pluginId}" did not export a function or { register } API.`;
          logger.error(message);
          addDiagnostic("invalid_plugin_export", message, "register");
        }

        const cliCommands = await buildCliProgram();

        if (compatibilityDiagnostics.length > 0) {
          return {
            tools: [],
            channels: [],
            commands: [],
            cliCommands: [],
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
            outputSchema: tool.outputSchema,
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
          cliCommands,
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

        if (result && (Array.isArray(result.content) || result.details !== undefined)) {
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
      let startResult;
      if (typeof ch.start === "function") {
        startResult = await ch.start();
      }
      startedChannels.add(channelId);
      const result = { ok: true };
      if (startResult && typeof startResult === "object" && startResult.selfId) {
        result.selfId = startResult.selfId;
      }
      return result;
    }

    case "channel_send": {
      const channelId = getParam(req.params, "channelId");
      const recipientId = getParam(req.params, "recipientId");
      const text = getParam(req.params, "text");
      const accountId = req.params?.accountId ?? null;
      const sessionId = req.params?.sessionId ?? null;
      const replyToMessageId = req.params?.replyToMessageId ?? null;
      const subject = req.params?.subject ?? null;
      const attachments = req.params?.attachments ?? null;
      const ch = registeredChannels.get(channelId);
      if (!ch) throw new Error(`Unknown channel: ${channelId}`);
      if (typeof ch.send !== "function") throw new Error(`Channel has no send handler: ${channelId}`);
      await ch.send({ channelId, recipientId, accountId, text, sessionId, replyToMessageId, subject, attachments });
      return { ok: true };
    }

    case "channel_typing": {
      const channelId = getParam(req.params, "channelId");
      const recipientId = getParam(req.params, "recipientId");
      const accountId = req.params?.accountId ?? null;
      const isTyping = req.params?.isTyping ?? true;
      const ch = registeredChannels.get(channelId);
      if (!ch) throw new Error(`Unknown channel: ${channelId}`);
      if (typeof ch.typing === "function") {
        await ch.typing({ channelId, recipientId, accountId, isTyping });
      }
      return { ok: true };
    }

    case "channel_read_receipt": {
      const channelId = getParam(req.params, "channelId");
      const messageId = getParam(req.params, "messageId");
      const accountId = req.params?.accountId ?? null;
      const remoteJid = req.params?.remoteJid ?? null;
      const participant = req.params?.participant ?? null;
      const ch = registeredChannels.get(channelId);
      if (!ch) throw new Error(`Unknown channel: ${channelId}`);
      if (typeof ch.readReceipt === "function") {
        await ch.readReceipt({ channelId, messageId, accountId, remoteJid, participant });
      }
      return { ok: true };
    }

    case "channel_react": {
      const channelId = getParam(req.params, "channelId");
      const messageId = getParam(req.params, "messageId");
      const accountId = req.params?.accountId ?? null;
      const emoji = getParam(req.params, "emoji");
      const remoteJid = req.params?.remoteJid ?? null;
      const participant = req.params?.participant ?? null;
      const ch = registeredChannels.get(channelId);
      if (!ch) throw new Error(`Unknown channel: ${channelId}`);
      if (typeof ch.react === "function") {
        await ch.react({ channelId, messageId, accountId, emoji, remoteJid, participant });
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

function readStandaloneCliConfig() {
  const encoded = process.env.OPENCLAW_PLUGIN_CLI_CONFIG_BASE64 ?? "";
  if (!encoded) return {};
  try {
    return JSON.parse(Buffer.from(encoded, "base64").toString("utf8"));
  } catch (error) {
    throw new Error(`Invalid standalone CLI plugin config: ${error?.message ?? error}`);
  }
}

async function initializeStandaloneCli(registrationMode, metadataOnly = false) {
  const entryPath = process.env.OPENCLAW_PLUGIN_CLI_ENTRY;
  pluginId = process.env.OPENCLAW_PLUGIN_CLI_ID ?? "unknown";
  if (!entryPath) throw new Error("OPENCLAW_PLUGIN_CLI_ENTRY is required.");

  logger = createLogger(pluginId);
  resetState();
  const pluginExport = await loadPlugin(entryPath);
  const api = createPluginApi(pluginId, readStandaloneCliConfig(), logger, registrationMode);
  if (typeof pluginExport === "function") {
    await pluginExport(api);
  } else if (pluginExport && typeof pluginExport.register === "function") {
    await pluginExport.register(api);
  } else {
    throw new Error(`Plugin "${pluginId}" did not export a function or { register } API.`);
  }

  const cliCommands = await buildCliProgram(metadataOnly);
  if (compatibilityDiagnostics.length > 0) {
    throw new Error(compatibilityDiagnostics.map((item) => `[${item.code}] ${item.message}`).join(" | "));
  }
  return cliCommands;
}

async function runStandaloneCli() {
  try {
    if (standaloneCliDescribeMode) {
      const cliCommands = await initializeStandaloneCli("cli-metadata", true);
      process.stdout.write(`${JSON.stringify(cliCommands)}\n`, () => process.exit(0));
      return;
    }

    await initializeStandaloneCli("full");
    const exitCode = await executeCli(process.argv.slice(3));
    process.exitCode = exitCode;
  } catch (error) {
    console.error(`Plugin CLI error: ${error?.message ?? error}`);
    process.exitCode = 1;
  }
}

if (standaloneCliMode || standaloneCliDescribeMode) {
  void runStandaloneCli();
} else if (transportMode === "stdio" || transportMode === "hybrid") {
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

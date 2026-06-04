# Optional Dependency Split

OpenClaw.NET keeps the default runtime local-first and NativeAOT-friendly. Optional integrations should live behind clear project or package boundaries when they add protocol-specific dependencies, provider SDKs, dynamic loading, or browser automation weight.

## Current Split

MQTT is a native protocol surface extracted from `OpenClaw.Agent`:

- project: `src/OpenClaw.Protocols.Mqtt`
- package dependency: `MQTTnet`
- config owner: `OpenClaw.Core` still owns `MqttConfig`
- composition owner: `OpenClaw.Gateway` registers MQTT tools through `NativePluginRegistry.RegisterExternalTool(...)`
- behavior: existing `OpenClaw:Plugins:Native:Mqtt` config remains unchanged

Browser automation is also split from `OpenClaw.Agent`:

- project: `src/OpenClaw.Protocols.Browser`
- package dependency: `Microsoft.Playwright`
- config owner: `OpenClaw.Core` still owns `Tooling.EnableBrowserTool` and browser tooling options
- composition owner: `OpenClaw.Gateway` registers the browser tool as part of the built-in gateway tool surface when browser availability checks pass
- behavior: existing `OpenClaw:Tooling:EnableBrowserTool` config and local/sandbox/backend fallback behavior remain unchanged

The agent executor no longer depends on browser-specific types for sandbox fallback. Tools that cannot safely fall back to local execution implement the protocol-neutral `IToolLocalExecutionPolicy` contract in `OpenClaw.Core`.

These splits keep protocol packages optional while preserving the gateway behavior operators already use.

## Remaining Boundaries

The following dependencies intentionally remain in `OpenClaw.Agent` for now:

| Surface | Current blocker | Next seam |
| --- | --- | --- |
| MCP tool registry | MCP registration participates in gateway startup and native registry composition. | Separate MCP registration contracts from Agent-owned tool registry implementation. |
| Plugin bridge | Plugin host, bridge process, dynamic native host, hooks, skills, providers, and diagnostics share runtime startup state. | Split bridge contracts and host lifecycle before moving transport-specific code. |
| OpenAI-specific provider packages | Provider construction still flows through current agent/runtime composition. | Keep provider-specific SDK use in provider projects or gateway composition before removing package weight from Agent. |

## Contributor Rule

Do not create empty optional projects to imply separation. Move a dependency only when the target project owns the implementation and the default runtime behavior can be validated with build, tests, and the HelloAgent smoke.

If a split requires changing public runtime semantics, document the needed seam first and keep the dependency in place until the seam is reviewed.

# A2A

OpenClaw can expose its gateway agent through A2A through the supported Microsoft Agent Framework adapter. A2A remains opt-in by configuration, while the adapter is included in normal gateway builds.

## Enablement

A2A support is available in normal gateway builds. At runtime, set:

```json
{
  "OpenClaw": {
    "MicrosoftAgentFramework": {
      "EnableA2A": true
    }
  }
}
```

The legacy `OpenClaw:Experimental:MicrosoftAgentFramework` section is still read for one release cycle. Startup records a warning when that legacy section is used.

## Discovery

The standard A2A discovery endpoint is:

```text
/.well-known/agent-card.json
```

For compatibility with earlier OpenClaw experimental builds, the same card is also available under the A2A path prefix:

```text
/a2a/.well-known/agent-card.json
```

Clients should prefer the standard root discovery endpoint.

## Protocol Endpoints

By default, OpenClaw exposes these A2A protocol bindings:

| Binding | Default path |
| --- | --- |
| HTTP+JSON | `/a2a` |
| JSON-RPC | `/a2a/rpc` |

The path prefix can be changed with `OpenClaw:MicrosoftAgentFramework:A2APathPrefix`.

## Agent Names

OpenClaw uses two A2A-facing names with different stability contracts:

| Surface | Value | Purpose |
| --- | --- | --- |
| Hosted A2A service id | `openclaw` | Stable Microsoft Agent Framework host key used for keyed A2A server, task store, and handler registration. |
| Agent Card `name` | `OpenClaw:MicrosoftAgentFramework:AgentName` | Public display name advertised to A2A clients during discovery. |

The hosted service id intentionally stays `openclaw` even when the Agent Card display name is customized. User-visible fallback messages use the Agent Card display name so responses correlate with discovery metadata. Keep the hosted id and card name aligned only if the host registration, endpoint mapping, and compatibility tests are changed together.

## Public Base URL

Agent Card URLs are generated from the current request host by default. Set `OpenClaw:MicrosoftAgentFramework:A2APublicBaseUrl` when the gateway runs behind a reverse proxy, container ingress, tunnel, or any host where the bind address is not externally reachable.

Example:

```json
{
  "OpenClaw": {
    "MicrosoftAgentFramework": {
      "EnableA2A": true,
      "A2APublicBaseUrl": "https://agents.example.com/openclaw"
    }
  }
}
```

With that configuration, the Agent Card advertises endpoints such as `https://agents.example.com/openclaw/a2a` and `https://agents.example.com/openclaw/a2a/rpc`.

## Authentication

Discovery is public by default so standard A2A card resolvers can fetch the Agent Card. Execution endpoints continue to use the gateway authentication and IP rate limiting policy.

For public deployments, configure gateway authentication before exposing the A2A execution paths.

## Streaming

The current A2A endpoint does not advertise protocol-level streaming. OpenClaw's internal agent runtime can stream, but the current A2A handler completes responses as a single A2A result until protocol-level streaming is implemented and tested.

## REST Response Materialization

The REST `POST /a2a/message:send` path must always materialize at least one A2A protocol event for every non-cancelled request. If the A2A server receives no events from the handler, the A2A SDK returns HTTP 500 with an error similar to:

```text
A2A.A2AException: Agent handler did not produce any response events.
```

OpenClaw avoids this by using an explicit keyed `OpenClawA2AAgentHandler` for the A2A server execution path. The handler bridges the OpenClaw runtime stream into a direct A2A agent `Message` event instead of relying on SDK response-update conversion from `AIAgent` streaming updates. This keeps `message:send` deterministic even when the underlying OpenClaw turn completes without text deltas.

Expected handler behavior:

| Runtime outcome | A2A REST response behavior |
| --- | --- |
| Text deltas are produced | Concatenate text deltas into one agent message. |
| The runtime completes without text | Return `[<AgentName>] Request completed.` as an agent message. |
| A recoverable bridge/runtime exception occurs | Log the exception and return `A2A request failed.` as an agent message. |
| The request is cancelled | Propagate cancellation instead of fabricating a response. |

The gateway still registers the Microsoft Agent Framework `AIAgent` host surface for metadata and session integration, but REST execution is intentionally routed through the explicit A2A event-queue handler. This is important because the preview hosting package can otherwise finish an OpenClaw turn while producing no materializable A2A response events.

If this error appears in logs, verify the active build includes the explicit keyed `IAgentHandler` registration for the `openclaw` A2A service and that the process was restarted after rebuilding. Focused regression coverage lives in the A2A HTTP endpoint tests and includes bridge exceptions, complete-without-text turns, and requests that omit `MessageId`.

## AOT and JIT Notes

This integration uses the Microsoft Agent Framework and A2A SDK package surface, so it adds dependency surface to the gateway build. Core OpenClaw runtime behavior remains independent because `Runtime.Orchestrator=native` is still the default and A2A endpoints remain disabled unless `OpenClaw:MicrosoftAgentFramework:EnableA2A=true`.

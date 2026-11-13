# Workflow Backends

OpenClaw.NET can run normal agent turns directly and delegate long-running work to configured workflow backends. Workflow delegation is for durable plans, approval gates, fan-out/fan-in reviews, and audit-oriented enterprise automation. It is not used for every fast agent turn.

## Configuration

Workflow backends live under `OpenClaw:Workflows`:

```json
{
  "OpenClaw": {
    "Workflows": {
      "Enabled": true,
      "Backends": {
        "durable-review": {
          "Kind": "maf-durable-http",
          "DisplayName": "Durable Agent Review",
          "BaseUrl": "http://127.0.0.1:5095",
          "WorkflowName": "DurableAgentReview",
          "PollIntervalSeconds": 2,
          "TimeoutSeconds": 120
        }
      }
    }
  }
}
```

`maf-durable-http` is the first supported backend kind. The gateway calls a durable MAF/Azure Functions host over HTTP; Durable Task and Azure Functions dependencies stay in that host or sample, not in the gateway.

### Authentication modes

The default `Authentication.Mode` is `static`. Existing deployments retain their behavior:
an optional backend `ApiTokenSecret` (for example `env:WORKFLOW_API_TOKEN`) supplies a
shared bearer token; omitting it sends no authentication. Static mode preserves
caller-supplied `SenderId` and `ActorId`. Do not add delegation-only settings to this mode.

Delegated backends use either `entra-obo` or RFC 8693 `token-exchange`, selected per
backend. Both require HTTPS `BaseUrl` and `TokenEndpoint` URLs without userinfo,
query strings, or fragments; an explicit gateway `Security.Oidc.Authority` and
`Security.Oidc.Audience`; and no `ApiTokenSecret`. Use `env:` or another supported
secret reference for `ClientSecretRef`, not a plaintext production secret.

For example, configure gateway OIDC separately under `OpenClaw`:

```json
{
  "Security": {
    "Oidc": {
      "Authority": "https://login.microsoftonline.com/TENANT_ID/v2.0",
      "Audience": "api://GATEWAY_APP_ID",
      "RequireHttpsMetadata": true
    }
  }
}
```

An Entra backend entry under `OpenClaw.Workflows.Backends`:

```json
{
  "review": {
    "Kind": "maf-durable-http",
    "BaseUrl": "https://workflow.example.com",
    "WorkflowName": "DurableAgentReview",
    "Authentication": {
      "Mode": "entra-obo",
      "TokenEndpoint": "https://login.microsoftonline.com/TENANT_ID/oauth2/v2.0/token",
      "ClientId": "GATEWAY_APP_ID",
      "ClientSecretRef": "env:WORKFLOW_OBO_CLIENT_SECRET",
      "Scopes": ["api://workflow/access_as_user"],
      "UserAssertionProfile": "entra-delegated"
    }
  }
}
```

Clients must present an **access token for the gateway**, not an ID token or a token
for the workflow API. Register the gateway as an Entra confidential middle-tier
application, configure the downstream delegated permission, and obtain the necessary
user/admin consent. `entra-delegated` requires a nonempty original `scp` claim and
rejects `idtyp=app`; roles alone do not prove a delegated user.

For an RFC 8693 provider such as a suitably configured Keycloak realm, set the
gateway authority/audience to that provider and use a backend entry such as:

```json
{
  "review": {
    "Kind": "maf-durable-http",
    "BaseUrl": "https://workflow.example.com",
    "Authentication": {
      "Mode": "token-exchange",
      "TokenEndpoint": "https://idp.example.com/realms/workflows/protocol/openid-connect/token",
      "ClientId": "openclaw-gateway",
      "ClientSecretRef": "env:WORKFLOW_EXCHANGE_CLIENT_SECRET",
      "Scopes": ["workflow.read"],
      "Audience": "workflow-api",
      "Resource": "https://workflow.example.com",
      "UserAssertionProfile": "required-claims",
      "AccessTokenClaims": { "typ": "Bearer" },
      "UserDelegationClaims": { "openclaw_actor_kind": "user" }
    }
  }
}
```

RFC exchange uses `client_secret_basic` and requires at least one of `Scopes`,
`Audience`, or `Resource`; use only parameters your provider supports. Entra requires
nonempty `Scopes` and does not accept `Audience` or `Resource`. The assertion admission
profile is independent of the exchange protocol. `required-claims` matches **all**
configured original claim names and values exactly, before inbound claim mapping.
Both maps must be nonempty. The issuer must guarantee that **only interactive user
access tokens** receive the user marker. A generic mapper alone is not sufficient:
verify negative cases for ID tokens, client credentials, service accounts, and wrong
audiences before deployment. `typ=Bearer` alone does not distinguish a user.

### Trust boundaries, limits, and errors

Delegation is supported for authenticated OIDC HTTP integration API calls and HTTP
MCP workflow calls. Each call uses its currently validated JWT, never a request body,
metadata, MCP initialize identity, or saved session credential. All three MCP workflow
tools execute synchronously; legacy sessions reject cross-user reuse. Cookies, local
account/bootstrap tokens, unauthenticated loopback access, stdio, chat, and background
jobs cannot supply a delegated user assertion. Existing endpoint roles, scopes, CSRF,
and rate limits still apply. Missing or rejected credentials never fall back to a
static token, and credential failures never replay a POST.

In delegated mode the gateway sets outbound `SenderId`/`ActorId` to the verified
subject; payload, metadata, session IDs, and original caller objects are unchanged.
**The workflow host must authorize every start, read, and response**, validating
issuer, audience, scopes, run ownership, and approval rights. Subject normalization
is not business authorization. The sample host does not implement these production
authorization requirements.

Token acquisition has a 30-second budget, reads at most 65,536 response bytes, and
accepts integer `expires_in` values from 1 to 86,400 seconds. Redirects are disabled.
The process-local cache is bounded to 1,024 entries and 1,024 total waiters, isolated
by issuer, subject, assertion hash, backend, and exchange parameters. Effective expiry
is the earlier of assertion expiry and exchange-start time plus `expires_in`.
Refresh is fixed at expiry minus `min(60 seconds, initial usable lifetime / 2)`;
failed refreshes do not reuse an old token. Cancelling one caller does not cancel
other callers' shared acquisition. Tokens/assertions are not persisted by the gateway
or added to events, sessions, or logs; workflow hosts must likewise avoid echoing them.

| HTTP status | Error code | Meaning |
| --- | --- | --- |
| 401 | `workflow_user_auth_required` | No current eligible user access token. |
| 403 | `workflow_delegation_denied` | Provider explicitly denied user delegation. |
| 502 | `workflow_token_exchange_failed` | Credentials, protocol, transport, limits, or provider failure. |
| 401 / 403 | `workflow_backend_auth_denied` | Downstream authentication/authorization denial; status preserved. |

HTTP errors use the JSON `error` field. MCP returns `IsError=true` with the safe code
in the SDK's tool-error text, not a successful JSON payload. Provider descriptions
and credentials are not exposed. Caller cancellation remains cancellation.

The implementation adds no identity SDK or reflection-based serialization to Core.
CI gates the same real HTTPS/OIDC REST and MCP two-protocol matrix against published
Linux JIT and NativeAOT executables; it requires exactly two passing cases with no
skips. Ordinary tests explicitly skip that matrix when
`OPENCLAW_WORKFLOW_GATEWAY_EXE` is unset. Setting an invalid path fails rather than skips.

## HTTP Contract

The gateway expects these backend endpoints relative to `BaseUrl`:

| Operation | Method and path | Gateway endpoint |
| --- | --- | --- |
| Start run | `POST /api/workflows/{workflowName}/run` | `POST /api/integration/workflows/{backendId}/runs` |
| Get status | `GET /api/workflows/{workflowName}/status/{runId}` | `GET /api/integration/workflows/{backendId}/runs/{runId}` |
| Respond to input | `POST /api/workflows/{workflowName}/respond/{runId}` | `POST /api/integration/workflows/{backendId}/runs/{runId}/responses` |

MCP tools expose the same surface:

| Tool | Purpose |
| --- | --- |
| `openclaw.list_workflows` | List configured workflow backends. |
| `openclaw.run_workflow` | Start a workflow run. Requires the `operator` role. |
| `openclaw.get_workflow_run` | Read current status, events, pending inputs, and output. |
| `openclaw.respond_workflow` | Send a human or system response to a pending input port. Requires the `operator` role. |

## Status Model

Workflow runs use these statuses:

| Status | Meaning |
| --- | --- |
| `queued` | Backend accepted the run but has not started work. |
| `running` | Backend is actively processing. |
| `waiting_for_input` | Backend is blocked on a human approval or external response port. |
| `completed` | Run completed successfully. |
| `failed` | Run failed. |
| `cancelled` | Run was rejected or cancelled. |

Events and pending inputs are AOT-safe JSON DTOs from `OpenClaw.Core`. Payload fields use `JsonElement` so workflow hosts can attach structured review summaries, audit traces, or approval context without coupling the gateway to a specific durable runtime package.

## DurableAgentReview Sample

`samples/OpenClaw.DurableAgentReview` exposes a sample `maf-durable-http` host:

```bash
dotnet run --project samples/OpenClaw.DurableAgentReview
```

Then configure the gateway with a matching backend and start a workflow through the integration API or MCP. The sample models this flow:

```text
User Request
  -> Plan Executor
  -> Security, Architecture, and Cost reviewers
  -> Aggregator
  -> Human approval RequestPort
  -> Execute approved action
  -> Audit trace output
```

The sample keeps orchestration code outside the gateway. A production host can replace the in-memory sample state with Microsoft Agent Framework Durable Workflows, Azure Functions, and Durable Task storage while preserving the same gateway contract.

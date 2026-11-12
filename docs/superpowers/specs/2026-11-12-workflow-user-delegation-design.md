# Workflow HTTP Runner user delegation

## Status and intent

Design direction approved for specification on 2026-11-12. This document
requires user review before an implementation plan is prepared. It describes
planned behavior, not an available feature.

The workflow HTTP runner currently resolves one backend `ApiTokenSecret` and
uses that bearer token for every operation. The goal is to let workflow
backends authorize and audit operations as the authenticated calling user.

The agreed first release supports:

- Microsoft Entra ID on-behalf-of (OBO).
- RFC 8693 token exchange, including compatible Keycloak deployments.
- Explicit protocol selection per workflow backend.
- OIDC-authenticated integration HTTP API and HTTP MCP calls.
- Start, status, response, and runner polling operations.
- Existing static-token behavior for existing configurations.

Delegated backends reject calls without a validated user OAuth credential.
Local account cookies, gateway bootstrap/account tokens, stdio MCP, chat
channels, and detached agent jobs do not gain delegated access in this release.
No new login flow, persistent refresh token store, or background user credential
renewal is included.

## Current implementation

- [Workflow models](../../../src/OpenClaw.Core/Models/WorkflowModels.cs) define
  backend configuration and serializable request/response data.
- [Runner interface](../../../src/OpenClaw.Core/Abstractions/IAgentWorkflowRunner.cs)
  has no trusted caller context.
- [HTTP runner](../../../src/OpenClaw.Gateway/Workflows/MafDurableHttpWorkflowRunner.cs)
  captures the static token at construction and sets authorization per request.
  Streaming polls through `GetAsync`.
- [Registry](../../../src/OpenClaw.Gateway/Workflows/AgentWorkflowRegistry.cs)
  shares backend runners across calls.
- [Integration facade](../../../src/OpenClaw.Gateway/Composition/IntegrationApiFacade.cs),
  [HTTP endpoints](../../../src/OpenClaw.Gateway/Endpoints/IntegrationEndpoints.cs),
  and [MCP tools](../../../src/OpenClaw.Gateway/Mcp/OpenClawMcpTools.cs)
  expose the workflow operations.
- [Security registration](../../../src/OpenClaw.Gateway/Composition/SecurityServicesExtensions.cs)
  configures JWT bearer validation when an OIDC authority is present.

`SenderId`, `ActorId`, `SessionId`, and metadata are client-controlled business
data, not proof of identity.

## Approach and alternatives

Use lightweight HTTP protocol adapters in Gateway with a shared token provider
and explicit, non-serializable execution context. Core contains only the small
configuration/context contracts needed by the runner abstraction. Gateway
authentication, token acquisition, and workflow execution remain separate.

Vendor SDKs were considered but add dependency and trimming validation work.
An external token broker was considered but adds deployment and trust
boundaries. Neither is required for the agreed first release.

## Configuration and validation

Extend each backend with an `Authentication` section:

| Field | Meaning |
| --- | --- |
| `Mode` | `static` (default), `entra-obo`, or `token-exchange` |
| `TokenEndpoint` | Explicit, fixed HTTPS authorization-server token endpoint |
| `ClientId` | Confidential client's registered identifier |
| `ClientSecretRef` | Secret reference resolved through existing secret infrastructure |
| `Scopes` | Nonempty scope list for Entra; optional requested scopes for RFC 8693 |
| `Audience` | Optional RFC 8693 target audience |
| `Resource` | Optional RFC 8693 target resource |

Delegated mode requires an absolute HTTPS backend `BaseUrl`, an absolute HTTPS
token endpoint, client credentials, an OIDC authority, and an explicit gateway
OIDC audience. Reject endpoint userinfo, fragments, and query parameters;
OAuth parameters belong in the form body. Entra requires scopes and rejects
RFC-specific audience/resource fields. RFC mode requires at least one target
selector: scopes, audience, or resource.

Reject unknown modes, invalid values, and incompatible mode-specific settings
at startup. Reject a nonempty `ApiTokenSecret` in delegated mode rather than
leave ambiguous fallback credentials configured. Static mode retains existing
`ApiTokenSecret`, optional-token, and backend URL behavior and rejects
delegation-only settings.

Use the existing secret resolver; never add a plaintext credential field.
Client-secret authentication is the first-release client authentication method:
`client_secret` in Entra's form body and `client_secret_basic` for RFC 8693.
Certificate assertions, public clients, and alternative client authentication
methods are not claimed as supported.

Endpoints, target scopes, audience, resource, and credentials are operator
configuration, never request or model-controlled values. Do not follow HTTP
redirects from either the token endpoint or a delegated backend. This avoids
forwarding credentials to another location.

## Trusted caller context and propagation

At each HTTP entry, obtain a successful authentication result from the configured
JWT bearer scheme. Do not rely solely on an arbitrary authenticated principal
or a raw `Authorization` header. Use the same bearer token that successfully
authenticated the request, together with its validated issuer, subject, and
expiration. Require a nonempty subject and a finite expiration. Honor the
authentication handler's validation result; do not independently trust decoded
but unvalidated JWT contents.

Retain normal endpoint authorization, scope, CSRF, and rate-limit checks.
Passing those checks via loopback bypass, a local cookie, or a static token
does not establish eligibility for OAuth delegation.

Construct a non-serializable caller context and pass it explicitly through
facade, registry, and runner methods. Preserve existing context-free overloads
for source compatibility; a context-free call to a delegated backend fails
explicitly. Evolve the runner abstraction with context-aware overloads without
removing existing signatures. Default interface implementations may forward a
null context to the old method, but must reject a nonnull context if an external
runner has not implemented delegation. Never silently discard a supplied context.

HTTP MCP resolves identity from the current authenticated transport request,
not tool arguments, connection initialization metadata, or a previous request
in the MCP session. Context injection must not appear in the model-visible tool
schema. stdio MCP has no such context and is rejected for delegated backends.
No caller context is stored in singleton runners, sessions, events, or DTOs.

Start, get, respond, and stream all accept and propagate the context. Every
poll rechecks its validity and acquires a suitable token; an expired incoming
assertion terminates polling rather than extending the user's authority.
There is no new HTTP streaming endpoint in this scope.

In delegated mode, outgoing `SenderId` and response `ActorId` are populated
from the validated subject, not the client-supplied value. Issuer-qualified
identity remains authoritative through the exchanged token. Other business
metadata is not an authorization source. Static mode preserves existing data
semantics.

## Token acquisition protocols

Send form-encoded HTTPS POST requests with bounded timeouts and response size.
Deserialize with source-generated JSON metadata.

### Entra OBO

Send:

- `grant_type=urn:ietf:params:oauth:grant-type:jwt-bearer`
- `requested_token_use=on_behalf_of`
- `assertion=<validated incoming access token>`
- Configured `client_id`, resolved `client_secret`, and space-separated `scope`.

The configured Entra token endpoint and app registrations must permit OBO for
the gateway and target workflow API. An ID token is not an acceptable incoming
assertion.

### RFC 8693

Send client authentication and:

- `grant_type=urn:ietf:params:oauth:grant-type:token-exchange`
- `subject_token=<validated incoming access token>`
- `subject_token_type=urn:ietf:params:oauth:token-type:access_token`
- `requested_token_type=urn:ietf:params:oauth:token-type:access_token`
- Configured `scope`, `audience`, and `resource` when present.

Require the returned `issued_token_type` to identify an access token.
Keycloak deployment/version policies must explicitly allow the configured
exchange; RFC support is not a blanket Keycloak compatibility claim.

### Response handling

Both adapters require a nonempty access token, bearer token type, and positive,
bounded `expires_in`. Treat malformed success responses as failures. Returned
scope, when provided, must include configured required scopes. The issuer and
backend must enforce target audience and delegated permissions; the gateway
does not reinterpret an opaque outgoing token as a verified JWT.

## Token lifetime, caching, and concurrency

Cache tokens only in memory. A cache key includes validated issuer and subject,
a cryptographic fingerprint of the incoming assertion, backend ID, protocol,
client ID, token endpoint, and target scopes/audience/resource. Never key by
subject alone or retain an incoming token as a dictionary key.

Use at most 1,024 cached/in-flight entries per provider instance. At capacity,
evict expired/completed entries or reject additional distinct acquisitions
explicitly; never create an unbounded waiter/key collection. Coalesce concurrent
acquisition for the same key. Remove failed acquisitions so a later call can
retry. One waiter's cancellation must not cancel another caller's acquisition;
shared work has its own bounded timeout and application shutdown cancellation.

Usable lifetime never exceeds either incoming assertion expiration or returned
token expiration. Refresh early using a margin of the smaller of 60 seconds
and half the remaining usable lifetime. An expired incoming assertion is never
renewed implicitly, and a cached token never bypasses caller validation.

The runner attaches the acquired bearer token only to its own
`HttpRequestMessage`. Do not mutate `DefaultRequestHeaders.Authorization`.
Never replay workflow POSTs automatically after authentication failure.

## Failure semantics and authorization boundaries

Use typed delegation failures with stable, sanitized error codes:

| Condition | HTTP behavior |
| --- | --- |
| Missing or expired trusted user assertion | 401, `workflow_user_auth_required` |
| Token endpoint explicitly denies delegation or requires user interaction | 403, `workflow_delegation_denied` |
| Workflow backend denies the exchanged user token | Preserve 401/403 with a sanitized workflow authorization error |
| Token endpoint transport, timeout, malformed response, or service/client configuration failure | 502, `workflow_token_exchange_failed` |

MCP returns corresponding tool errors, not success-shaped workflow results.
Caller cancellation remains cancellation, not a generic backend failure.
Configuration validation errors fail startup. Never send the backend request
after a failed exchange and never retry with `ApiTokenSecret`.

Log backend ID, operation, status, and a safe error code using existing
logging conventions. Do not log request/response OAuth bodies, authorization
headers, secret references containing sensitive values, or raw provider errors.
Do not expose provider error descriptions or tokens to clients.

The workflow host remains responsible for authorization on every operation,
including run ownership, cross-user access, and approval permissions. The
gateway does not infer those policies from `runId`, `SenderId`, or `ActorId`.
Deployment guidance must explicitly require delegated authorization on the
host; a host accepting every exchanged token does not meet the security goal.

## Affected surfaces and documentation

Update the configuration and source-generated JSON contracts, workflow runner
interface and implementation, registry, facade, integration endpoints, HTTP MCP
context adapter, DI, and startup validation. Keep protocol logic in Gateway.
Do not change unrelated tool execution or agent reasoning code.

Update [English workflow documentation](../../workflow-backends.md) and
[Chinese workflow documentation](../../zh-CN/workflow-backends.md) with both
protocol configurations, issuer registration prerequisites, backend
authorization requirements, supported entry points, and fail-closed behavior.
Preserve existing configuration examples. The sample host must not be presented
as enforcing delegated authorization unless it gains such enforcement and tests.

JIT and NativeAOT use the same delegation implementation. No reflection-heavy
identity SDK is added to Core. New OAuth DTOs use source-generated serializers;
trimming/AOT validation must cover the new path, not just ordinary compilation.

## Verification and acceptance

Use deterministic fake token endpoints and backend HTTP handlers; no real user
credentials or external identity tenant is required for automated tests.

1. Assert exact Entra and RFC form fields and client authentication. Cover
   escaping, scopes, target selectors, and fixed configured endpoints.
2. Reject invalid configuration, absent identity, forged body/metadata identity,
   non-bearer authentication, expired assertions, and unsupported transports.
3. Exercise HTTP integration and HTTP MCP entry points end to end through
   authentication. Check that injected context is absent from MCP tool schemas
   and that two users sharing an MCP server/session cannot share identity.
4. Verify start/get/respond/every polling request receive the correct user's
   exchanged token and delegated body identity.
5. Interleave two users and two backends. Verify issuer, assertion, target, and
   backend isolation; same-key single flight; expiration; capacity bounds;
   failed acquisition removal; and independent waiter cancellation.
6. Cover denial, interaction-required responses, malformed/missing token fields,
   timeouts, redirects, provider outages, cancellation, and backend 401/403.
   Assert that no static fallback or unsafe POST replay occurs.
7. Assert tokens and OAuth error bodies are absent from logs, errors, workflow
   payloads, events, and persisted sessions.
8. Retain static-mode and context-free API compatibility tests and document
   intentional delegated-mode body identity normalization.
9. Run targeted Release tests, then the documented solution Release build and
   tests, confirming zero warnings and a nonzero test count. Run gateway
   `--doctor` smoke because configuration and DI change. Exercise the
   repository's applicable NativeAOT validation path.

Implementation planning starts only after review and approval of this written
specification. No runtime changes or implementation validation are included in
the specification-only stage.

# MCP Per-Call Delegated Credentials Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Allow explicitly configured remote MCP and MCP App HTTP tool calls to use short-lived, caller-bound credentials without sharing Authorization state across concurrent calls.

**Architecture:** Keep caller credentials in an immutable, non-serialized request/connection context and pass that context through runtime tool dispatch. For delegated HTTP calls, obtain a fresh credential using the endpoint's explicitly selected token-exchange or Gateway-signed mode, then create a one-call MCP client/transport with that Authorization value; leave existing shared clients and static headers unchanged for endpoints without delegation. Strategos remains responsible for validating the resulting credential and mapping its principal.

**Tech Stack:** .NET 10, C# 14, MCP C# SDK 2.2.0, ASP.NET Core OIDC/JWT bearer authentication, xUnit, NSubstitute, source-generated `System.Text.Json`.

## Global Constraints

- Keep Core lightweight and NativeAOT-friendly; do not add reflection-heavy or trim-unsafe dependencies to core runtime paths.
- Delegated credentials are disabled by default and enabled only by trusted Gateway configuration for a specific MCP endpoint or MCP App entry.
- Never mutate a shared `HttpClient.DefaultRequestHeaders` or shared `HttpClientTransportOptions.AdditionalHeaders` to set caller Authorization.
- Do not put raw caller or delegated tokens in Session, tool arguments, MCP `_meta`, persisted execution records, logs, traces, or error text.
- Only accept a validated OIDC bearer on Gateway HTTP requests or a validated OIDC bearer bound to a WebSocket connection; do not derive user credentials from channel identities or static Gateway tokens.
- Select token exchange or Gateway-signed delegation explicitly. Provider errors and upstream 401/403 responses must not trigger another credential mode or static Authorization fallback.
- Strategos-side signature validation, issuer/audience/expiry/scope checks, `ClaimsPrincipal` creation, and `ActionPrincipal` mapping are outside this repository and this plan.

---

### Task 1: Prove the Per-Call MCP HTTP Boundary

**Files:**

- Create: `src/OpenClaw.Gateway/Mcp/McpDelegatedHttpClientFactory.cs`
- Test: `src/OpenClaw.Tests/McpServerToolRegistryTests.cs`

**Interfaces:**

- Produces: `Task<McpClient> CreateAsync(string endpointId, Uri endpoint, IReadOnlyDictionary<string, string> headers, int requestTimeoutSeconds, string delegatedAccessToken, CancellationToken ct)` in the Gateway MCP layer. This endpoint-neutral signature supports both `McpServerConfig` and resolved MCP App manifest endpoints.
- The factory creates an HTTP transport for one tool call, copies non-Authorization static headers, applies the delegated bearer only to that transport, and initializes the MCP client before returning it. Callers own and dispose the returned client.
- This task is a technical gate. Do not proceed to Tasks 2-7 if the SDK/server session test cannot prove isolation and a complete initialize-plus-tools/call lifecycle without shared mutable headers. In that case, update the design and implementation plan before coding further.

- [x] **Step 1: Add the concurrency test against a real in-process MCP HTTP server.**

Add a test named `McpDelegatedHttpClientFactory_ConcurrentClients_KeepAuthorizationIsolated` in `McpServerToolRegistryTests.cs`. Start the existing test MCP server harness, record the Authorization header specifically on `tools/call`, create two clients concurrently with distinct delegated tokens, invoke a harmless tool through each client, and assert each recorded call has its matching bearer. Also assert a configured non-Authorization header is preserved and a configured static Authorization value is absent from both calls.

- [x] **Step 2: Run the focused test and confirm the factory is not implemented.**

Run: `dotnet test src/OpenClaw.Tests/OpenClaw.Tests.csproj --configuration Release --filter "FullyQualifiedName~McpDelegatedHttpClientFactory_ConcurrentClients_KeepAuthorizationIsolated"`

Expected: FAIL because `McpDelegatedHttpClientFactory` does not exist yet; the test must compile once the type is added.

- [x] **Step 3: Implement the one-call client factory using SDK 2.2.0 public transport APIs.**

Build transport options from the endpoint URL and copied static headers, excluding any header whose name is `Authorization` case-insensitively. Set the delegated bearer on the per-call HTTP client/transport, call `McpClient.CreateAsync`, and return the initialized client. Do not change the registry's existing shared client or shared transport options.

- [x] **Step 4: Run the focused test and verify protocol-session behavior.**

Run: `dotnet test src/OpenClaw.Tests/OpenClaw.Tests.csproj --configuration Release --filter "FullyQualifiedName~McpDelegatedHttpClientFactory_ConcurrentClients_KeepAuthorizationIsolated"`

Expected: PASS; two overlapping calls complete and the server observes the correct, distinct Authorization value on each `tools/call`. Initialization and disposal complete without leaking a client/session.

---

### Task 2: Add Trusted Per-Endpoint Delegation Configuration

**Files:**

- Modify: `src/OpenClaw.Core/Plugins/PluginModels.cs`
- Modify: `src/OpenClaw.Core/Validation/ConfigValidator.cs`
- Modify: `src/OpenClaw.Core/Models/Session.cs` (source-generated config JSON context)
- Test: `src/OpenClaw.Tests/ConfigValidatorTests.cs`
- Test: `src/OpenClaw.Tests/McpAppTests.cs`

**Interfaces:**

- Add `McpDelegatedCredentialsConfig` with `Enabled` defaulting to `false`, `Mode`, `Audience`, `Scopes`, `TokenEndpoint`, `ClientId`, `ClientSecretRef`, `Issuer`, `SigningKeyRef`, and `LifetimeSeconds`.
- Add `McpDelegatedCredentialsConfig? DelegatedCredentials` to both `McpServerConfig` and `McpAppEntryConfig`. The same policy type is used by both paths; MCP App manifest fields and manifest headers never enable or select delegated authentication.
- Supported mode names are exactly `token_exchange` and `gateway_signed`.

- [x] **Step 1: Write config validation tests for valid opt-in and invalid policy combinations.**

Add tests proving defaults remain disabled, valid HTTP MCP and MCP App policies validate, an enabled policy on a configured non-HTTP MCP transport is rejected, missing audience/scopes are rejected, and each mode requires only its own settings (`TokenEndpoint`/`ClientId`/`ClientSecretRef` for `token_exchange`; `Issuer`/`SigningKeyRef`/positive `LifetimeSeconds` for `gateway_signed`). For MCP Apps, validate an explicit non-HTTP `Entries[id].Transport` immediately and validate the resolved manifest transport during app startup when no override is configured. Include JSON round-trip coverage for both endpoint config types using the repository's generated config context.

- [x] **Step 2: Run the focused config tests and verify they fail for missing settings.**

Run: `dotnet test src/OpenClaw.Tests/OpenClaw.Tests.csproj --configuration Release --filter "FullyQualifiedName~ConfigValidatorTests|FullyQualifiedName~McpAppTests"`

Expected: FAIL on the new properties and validation behavior.

- [x] **Step 3: Add the shared policy model and attach it to both trusted config entries.**

Add the model to `PluginModels.cs`, add the two nullable properties, and include the new type in the source-generated JSON context. Keep secrets as references; do not add plaintext signing keys or client secrets to configuration.

- [x] **Step 4: Validate opt-in policies at Gateway startup.**

Extend `ConfigValidator.Validate` to reject unsupported modes, non-HTTP endpoints, blank audience/scopes, invalid mode-specific required fields, and non-positive signed-token lifetimes. Preserve all existing defaults and validation behavior when `Enabled` is false.

- [x] **Step 5: Run config validation and MCP App tests.**

Run: `dotnet test src/OpenClaw.Tests/OpenClaw.Tests.csproj --configuration Release --filter "FullyQualifiedName~ConfigValidatorTests|FullyQualifiedName~McpAppTests"`

Expected: PASS; disabled and legacy configurations remain valid, and manifests alone cannot enable delegation.

---

### Task 3: Define Credential Types and Core Contracts

**Files:**

- Create: `src/OpenClaw.Core/Security/McpDelegatedCredentials.cs`
- Test: `src/OpenClaw.Tests/McpDelegatedCredentialProviderTests.cs`

**Interfaces:**

- `McpCallerCredentialContext` is a sealed class with `OidcAccessToken`, `Subject`, and `ExpiresAtUtc` properties; `McpDelegatedCredential` is a sealed class with `AccessToken` and `ExpiresAtUtc` properties.
- Mark both bearer-token properties `[JsonIgnore]` and override both `ToString()` methods with fixed redacted text; do not use positional records for values containing tokens.
- `public interface IMcpDelegatedCredentialProvider { Task<McpDelegatedCredential> GetCredentialAsync(McpDelegatedCredentialsConfig policy, McpCallerCredentialContext caller, CancellationToken ct); }`
- Define `IMcpDelegatedToolInvoker` in Core, alongside `McpDelegatedToolCallRequest` and `McpDelegatedToolCallResult`, so both Agent and MCP App can consume the capability without either project referencing Gateway or each other. The request contains endpoint ID/URI, static headers, timeout, trusted policy, remote tool name, JSON arguments, optional caller context, and the structured-content suppression flag; the result contains response text and the MCP `IsError` flag.

- [x] **Step 1: Add tests for credential-value redaction and serialization boundaries.**

Add caller and delegated credential values with unique token markers. Assert `JsonSerializer.Serialize` and `ToString()` for both values do not contain either marker. Assert the Core tool-call request/result contracts carry only the fields declared in the interface block and do not define serialization of the credential context.

- [x] **Step 2: Run the focused contract tests and confirm they fail before implementation.**

Run: `dotnet test src/OpenClaw.Tests/OpenClaw.Tests.csproj --configuration Release --filter "FullyQualifiedName~McpDelegatedCredentialProviderTests"`

Expected: FAIL because the credential and invoker types do not exist yet.

- [x] **Step 3: Add sealed Core credential and call-contract types.**

Keep these types free of HTTP and JWT implementation dependencies. Add explicit constructors/properties, `[JsonIgnore]` on token properties, and fixed redacted `ToString()` implementations. Do not add JSON conversion for the invoker request or add credentials to Session/durable messages.

- [x] **Step 4: Run the focused contract tests.**

Run: `dotnet test src/OpenClaw.Tests/OpenClaw.Tests.csproj --configuration Release --filter "FullyQualifiedName~McpDelegatedCredentialProviderTests"`

Expected: PASS; serialized and formatted credential values contain no token marker.

---

### Task 4: Implement Explicit OAuth Token Exchange

**Files:**

- Create: `src/OpenClaw.Gateway/Security/McpDelegatedCredentialProviders.cs`
- Modify: `src/OpenClaw.Gateway/Composition/SecurityServicesExtensions.cs`
- Test: `src/OpenClaw.Tests/McpDelegatedCredentialProviderTests.cs`

**Interfaces:**

- Implement `IMcpDelegatedCredentialProvider.GetCredentialAsync` for `policy.Mode == "token_exchange"` only. A request for any other mode is rejected by this provider.

- [x] **Step 1: Add focused token-exchange success and failure tests.**

Use a controlled `HttpMessageHandler`. Assert the standard token-exchange request carries the configured endpoint, audience, scopes, client ID/secret, and caller subject token. Cover malformed responses, missing access token, expired response, non-success status, and cancellation. Assert failures do not return a credential and response bodies are not logged.

- [x] **Step 2: Run the focused tests and confirm they fail.**

Run: `dotnet test src/OpenClaw.Tests/OpenClaw.Tests.csproj --configuration Release --filter "FullyQualifiedName~McpDelegatedCredentialProviderTests"`

Expected: FAIL because the token-exchange provider is not registered or implemented.

- [x] **Step 3: Implement the token-exchange provider.**

POST the OAuth token-exchange grant to the configured token endpoint, resolve `ClientSecretRef` with `OpenClaw.Core.Security.SecretResolver.Resolve`, request only configured audience/scopes, and require a non-empty access token with a future expiry. Do not cache the result or log request/response bodies.

- [x] **Step 4: Register the provider and rerun the focused tests.**

Register the provider through `AddOpenClawSecurityServices`; preserve startup and runtime behavior when no endpoint opts in. Run the focused test command from Step 2.

Expected: PASS; the request matches the trusted policy, failures are fail-closed, and the provider does not serve signed-mode requests.

---

### Task 5: Implement Explicit Gateway-Signed Delegation

**Files:**

- Modify: `src/OpenClaw.Gateway/Security/McpDelegatedCredentialProviders.cs`
- Modify: `src/OpenClaw.Gateway/Composition/SecurityServicesExtensions.cs`
- Test: `src/OpenClaw.Tests/McpDelegatedCredentialProviderTests.cs`

**Interfaces:**

- Implement the `gateway_signed` branch of the Gateway credential provider; keep it mutually exclusive with the token-exchange branch.

- [x] **Step 1: Add signed-token tests for claims, lifetime, and mode isolation.**

Use an ephemeral signing key. Assert the token contains the configured issuer, caller subject, audience, configured scopes, and an expiry no later than `LifetimeSeconds`. Assert missing key, invalid lifetime, and unknown mode fail without returning a credential; assert token-exchange is not called for signed mode and signing is not attempted for exchange mode.

- [x] **Step 2: Run the focused signed-token tests and confirm they fail.**

Run: `dotnet test src/OpenClaw.Tests/OpenClaw.Tests.csproj --configuration Release --filter "FullyQualifiedName~McpDelegatedCredentialProviderTests"`

Expected: FAIL until the signed provider implementation is added.

- [x] **Step 3: Implement signed delegation with a centrally managed, AOT-compatible signing dependency.**

Resolve `SigningKeyRef` with `OpenClaw.Core.Security.SecretResolver.Resolve`. Reuse an already available JWT implementation if possible; if a package is required, add one fixed central package version and verify its trim/AOT behavior before adopting it. Emit only issuer, subject, audience, configured scopes, issued-at, and bounded expiry claims. Do not cache issued tokens.

Validation: the Gateway trim-analyzer build succeeded without warnings, and an isolated win-x64 NativeAOT probe using IdentityModel 8.23.0 generated and parsed an HS256 JWT. Full Gateway NativeAOT publish remains unverified because its ILC compile did not complete.

- [x] **Step 4: Register explicit mode dispatch and rerun provider tests.**

Dispatch solely from `policy.Mode`, fail on unsupported values, and do not retry the alternate provider. Run the focused test command from Step 2.

Expected: PASS; both providers are selected only by configuration and neither falls back to the other.

---

### Task 6: Propagate Caller Context Through Native and MAF Runtime Dispatch

**Files:**

- Modify: `src/OpenClaw.Core/Abstractions/IToolWithContext.cs`
- Modify: `src/OpenClaw.Agent/IAgentRuntime.cs`
- Modify: `src/OpenClaw.Agent/AgentRuntime.cs`
- Modify: `src/OpenClaw.Agent/Runtime/AgentToolCallLoop.cs`
- Modify: `src/OpenClaw.Agent/OpenClawToolExecutor.cs`
- Modify: `src/OpenClaw.Agent/AgentExecutionContext.cs`
- Modify: `src/OpenClaw.MicrosoftAgentFrameworkAdapter/MafAgentRuntime.cs`
- Modify: `src/OpenClaw.MicrosoftAgentFrameworkAdapter/MafToolAdapter.cs`
- Test: `src/OpenClaw.Tests/AgentRuntimeTests.cs`
- Test: `src/OpenClaw.Tests/MafAdapterTests.cs`

**Interfaces:**

- Add `McpCallerCredentialContext? McpCallerCredentialContext` to `ToolExecutionContext`.
- Add an optional trailing `McpCallerCredentialContext? callerCredentialContext = null` parameter to `IAgentRuntime.RunAsync`, `RunTurnAsync`, and `RunStreamingAsync`, and implement it in both runtime implementations.

- [x] **Step 1: Add failing native and MAF runtime propagation tests.**

Assert that the exact `McpCallerCredentialContext` passed to each runtime entry point reaches `ToolExecutionContext` in a normal tool call and MetaSkill parallel wave. Exercise both `AgentRuntime` and `MafAgentRuntime`; verify two concurrent MAF steps receive the same immutable turn context without adding it to shared runtime state.

- [x] **Step 2: Run the runtime tests and confirm the new assertions fail.**

Run: `dotnet test src/OpenClaw.Tests/OpenClaw.Tests.csproj --configuration Release --filter "FullyQualifiedName~AgentRuntimeTests|FullyQualifiedName~MafAdapterTests"`

Expected: FAIL on missing context properties/parameters or missing tool-context propagation.

- [x] **Step 3: Thread the context through native runtime dispatch.**

Pass the optional context through the runtime entry methods, `AgentToolCallLoop`, and `OpenClawToolExecutor` into the created `ToolExecutionContext`. Do not store it on the shared executor or runtime instance.

- [x] **Step 4: Thread the context through MAF tool dispatch and parallel steps.**

Carry it only in that turn's existing scoped `AgentExecutionContext`, then copy it into each MAF-created `ToolExecutionContext`. Each parallel/fan-out task must capture the immutable value from its own turn; never add it to a shared runtime field.

- [x] **Step 5: Run focused native and MAF tests.**

Run: `dotnet test src/OpenClaw.Tests/OpenClaw.Tests.csproj --configuration Release --filter "FullyQualifiedName~AgentRuntimeTests|FullyQualifiedName~MafAdapterTests"`

Expected: PASS; native and MAF calls, including parallel steps, preserve the exact caller context and runtime objects retain no per-caller state after the turn.

---

### Task 7: Bind Direct HTTP Runtime Calls to Validated OIDC Requests

**Files:**

- Modify: `src/OpenClaw.Gateway/Endpoints/EndpointHelpers.cs`
- Modify: `src/OpenClaw.Gateway/Endpoints/OpenAiEndpoints.ChatCompletions.cs`
- Modify: `src/OpenClaw.Gateway/Endpoints/OpenAiEndpoints.Responses.cs`
- Modify: `src/OpenClaw.Gateway/Endpoints/AppsEndpoints.cs`
- Test: `src/OpenClaw.Tests/EndpointHelpersAuthenticationTests.cs`
- Test: `src/OpenClaw.Tests/AppsEndpointsTests.cs`

**Interfaces:**

- Add `EndpointHelpers.ResolveMcpCallerCredentialContext(HttpContext ctx, GatewayStartupContext startup)` returning `McpCallerCredentialContext?`.
- Create a context only when OIDC Authority is configured, `ctx.User.Identity.IsAuthenticated` is true, the request has an `Authorization: Bearer` value validated by the authentication middleware, the validated principal has a subject, and its validated `exp` claim is in the future.

- [x] **Step 1: Add tests distinguishing validated OIDC from other Gateway auth modes.**

Cover a validated OIDC bearer with subject and future `exp`, expired/missing `exp`, absent bearer, unauthenticated principal, static Gateway token, account token, and browser session. Assert only the first case yields a caller context and assert the original bearer value is preserved exactly.

- [x] **Step 2: Run focused endpoint-helper and Apps tests.**

Run: `dotnet test src/OpenClaw.Tests/OpenClaw.Tests.csproj --configuration Release --filter "FullyQualifiedName~EndpointHelpersAuthenticationTests|FullyQualifiedName~AppsEndpointsTests"`

Expected: FAIL because the helper does not exist.

- [x] **Step 3: Implement the OIDC-only context resolver and pass its result into direct runtime calls.**

Use only the validated `HttpContext.User` claims plus the bearer from the current request header. Do not accept query tokens, Gateway bootstrap tokens, account tokens, or cookie credentials as MCP caller credentials. Pass the context from Chat Completions, Responses, and Apps endpoints through the new optional runtime parameter.

- [x] **Step 4: Run focused HTTP endpoint tests.**

Run the test command from Step 2.

Expected: PASS; all other authentication modes remain authorized as before but produce no delegated caller context.

---

### Task 8: Bind WebSocket Tool Calls to Their Authenticated Connection

**Files:**

- Modify: `src/OpenClaw.Gateway/Endpoints/WebSocketEndpoints.cs`
- Modify: `src/OpenClaw.Channels/WebSocketChannel.cs`
- Modify: `src/OpenClaw.Core/Models/Messages.cs`
- Test: `src/OpenClaw.Tests/WebSocketEndpointsTests.cs`

**Interfaces:**

- Add `[JsonIgnore] public McpCallerCredentialContext? McpCallerCredentialContext { get; init; }` to `InboundMessage`.
- Extend `WebSocketChannel.HandleConnectionAsync` with an optional connection-scoped caller context; copy it to each in-memory message created on that connection.

- [x] **Step 1: Add tests for OIDC connection binding and non-serialization.**

Connect with an authenticated OIDC principal, send multiple frames, and assert each resulting inbound message carries the same connection context. Assert an unauthenticated or non-OIDC authenticated connection carries none. Serialize `InboundMessage` with the generated Core JSON context and assert the token marker is absent.

- [x] **Step 2: Run the focused WebSocket tests and confirm they fail.**

Run: `dotnet test src/OpenClaw.Tests/OpenClaw.Tests.csproj --configuration Release --filter "FullyQualifiedName~WebSocketEndpointsTests"`

Expected: FAIL on missing connection-context support or serialization guard.

- [x] **Step 3: Capture context at handshake and retain it only in the live connection/message objects.**

Resolve the context once after WebSocket authentication, pass it to `HandleConnectionAsync`, and copy it into each `InboundMessage`. Do not add it to connection IDs, sessions, or serialized websocket envelopes.

- [x] **Step 4: Run WebSocket tests.**

Run the test command from Step 2.

Expected: PASS; messages are bound to the handshake identity and their credential context is not serialized.

Note: TestServer frame-delivery tests timed out before reaching the channel callback, so OIDC extraction and per-frame context identity are covered in deterministic resolver/channel tests instead.

---

### Task 9: Preserve Caller Context Only for the Original In-Memory Integration Message

**Files:**

- Modify: `src/OpenClaw.Gateway/Endpoints/IntegrationEndpoints.cs`
- Modify: `src/OpenClaw.Gateway/Composition/IntegrationApiFacade.cs`
- Modify: `src/OpenClaw.Gateway/Extensions/GatewayInboundMessageWorker.cs`
- Modify: `src/OpenClaw.Core/Models/Messages.cs`
- Test: `src/OpenClaw.Tests/GatewayWorkersTests.cs`
- Test: `src/OpenClaw.Tests/GatewayAdminEndpointTests.cs`

**Interfaces:**

- Add an optional trailing caller-context parameter to `IntegrationApiFacade.QueueMessageAsync`; assign it only to the current in-memory `InboundMessage`.
- Pass `InboundMessage.McpCallerCredentialContext` into the runtime only while the original queued object is being processed.

- [x] **Step 1: Add queue propagation and restart-boundary tests.**

Assert an authenticated OIDC integration request attaches the context to its in-memory queued message and the worker passes it to `IAgentRuntime`. Assert the serialized message omits the token and a deserialized/recovered message has no context. Include expired-context execution and assert no MCP request can be made after expiry.

- [x] **Step 2: Run focused integration queue tests and confirm they fail.**

Run: `dotnet test src/OpenClaw.Tests/OpenClaw.Tests.csproj --configuration Release --filter "FullyQualifiedName~GatewayWorkersTests|FullyQualifiedName~GatewayAdminEndpointTests"`

Expected: FAIL because queueing and worker dispatch do not carry an ephemeral caller context.

- [x] **Step 3: Attach context to the live request object and pass it through the worker.**

Pass the HTTP helper result into `QueueMessageAsync`, set the `[JsonIgnore]` `InboundMessage` property, and forward it to `RunTurnAsync` only for the original in-memory message. Do not put it in Session, retry records, or durable continuation state.

- [x] **Step 4: Run focused queue tests.**

Run the test command from Step 2.

Expected: PASS; the original in-memory message can use its valid context and deserialized/recovered messages cannot.

Note: The worker drops caller contexts that expire while queued. The wire-level assertion that an expired context produces zero MCP `tools/call` requests is covered with the real invoker and local MCP server in Task 10.

---

### Task 10: Implement the Gateway MCP Delegated Tool Invoker

**Files:**

- Create: `src/OpenClaw.Gateway/Mcp/McpDelegatedToolInvoker.cs`
- Modify: `src/OpenClaw.Gateway/Composition/ToolServicesExtensions.cs`
- Test: `src/OpenClaw.Tests/McpDelegatedToolInvokerTests.cs`

**Interfaces:**

- Implement Core's `IMcpDelegatedToolInvoker.InvokeAsync(McpDelegatedToolCallRequest request, CancellationToken ct)` in `McpDelegatedToolInvoker` in Gateway. The Gateway implementation owns SDK-specific request parsing, per-call client creation, MCP response formatting, and disposal.
- Register the invoker against the Core interface with `IMcpDelegatedCredentialProvider` and `McpDelegatedHttpClientFactory` dependencies; do not make either Agent or MCP App reference Gateway.

- [x] **Step 1: Add invoker tests for per-call authorization and fail-closed behavior.**

Use a local MCP HTTP server and a fake `IMcpDelegatedCredentialProvider`. Assert one delegated call uses the returned token, retains non-Authorization headers, excludes static Authorization, and preserves structured-content and `IsError` behavior. Assert missing/expired context or provider failure sends zero `tools/call` requests.

- [x] **Step 2: Run the invoker tests and confirm the implementation is absent.**

Run: `dotnet test src/OpenClaw.Tests/OpenClaw.Tests.csproj --configuration Release --filter "FullyQualifiedName~McpDelegatedToolInvokerTests"`

Expected: FAIL because Gateway has no implementation of `IMcpDelegatedToolInvoker`.

- [x] **Step 3: Implement the invoker and register it in Gateway composition.**

Resolve a fresh credential for each invocation, reject missing/expired inputs before network I/O, create the one-call MCP client using Task 1's factory, issue one `tools/call`, format the response into `McpDelegatedToolCallResult`, and dispose the client. Do not cache credentials or include provider response bodies in failures.

- [x] **Step 4: Run focused invoker tests.**

Run: `dotnet test src/OpenClaw.Tests/OpenClaw.Tests.csproj --configuration Release --filter "FullyQualifiedName~McpDelegatedToolInvokerTests"`

Expected: PASS; each invocation obtains a fresh credential, sends only the delegated Authorization, and fails before `tools/call` when no usable credential exists.

---

### Task 11: Route Opted-In Ordinary MCP Tools Through the Invoker

**Files:**

- Modify: `src/OpenClaw.Agent/Tools/McpNativeTool.cs`
- Modify: `src/OpenClaw.Agent/Plugins/McpServerToolRegistry.cs`
- Modify: `src/OpenClaw.Gateway/Composition/ToolServicesExtensions.cs`
- Test: `src/OpenClaw.Tests/McpServerToolRegistryTests.cs`

**Interfaces:**

- Inject the Core `IMcpDelegatedToolInvoker` into `McpServerToolRegistry` and provide the trusted `McpServerConfig.DelegatedCredentials` policy to each registered `McpNativeTool`.
- When delegation is disabled, keep the current shared client path byte-for-byte compatible in behavior.

- [x] **Step 1: Add ordinary MCP routing and concurrency tests.**

Configure a local HTTP MCP server and two caller contexts. Assert opted-in calls use each caller's own token when executed concurrently through the same registered tools. Assert non-opted-in tools preserve their configured static headers and never receive either caller token.

- [x] **Step 2: Run ordinary MCP tests and confirm routing assertions fail.**

Run: `dotnet test src/OpenClaw.Tests/OpenClaw.Tests.csproj --configuration Release --filter "FullyQualifiedName~McpServerToolRegistryTests"`

Expected: FAIL because registered MCP tools still call only their shared client.

- [x] **Step 3: Route only opted-in HTTP server tools through `IMcpDelegatedToolInvoker`.**

Attach the endpoint ID, URL, trusted policy, resolved static headers, and timeout when constructing the tool. For opted-in endpoints, pass the call's `ToolExecutionContext.McpCallerCredentialContext` to the invoker. Do not send static Authorization on this path. Keep stdio/SSE and unconfigured HTTP tools on their current paths.

- [x] **Step 4: Run ordinary MCP tests.**

Run the test command from Step 2.

Expected: PASS; concurrent opted-in calls remain isolated and legacy static-header behavior is unchanged.

---

### Task 12: Route Opted-In MCP App Tools Through the Invoker

**Files:**

- Modify: `src/mcpapp/OpenClaw.McpApp/McpAppServer.cs`
- Modify: `src/mcpapp/OpenClaw.McpApp/shared/McpAppInfoProvider.cs`
- Modify: `src/mcpapp/OpenClaw.McpApp/McpAppToolProvider.cs`
- Modify: `src/OpenClaw.Gateway/Mcp/McpAppToolRegistrationExtensions.cs`
- Modify: `src/OpenClaw.Gateway/Composition/RuntimeInitializationExtensions.cs`
- Test: `src/OpenClaw.Tests/McpAppTests.cs`

**Interfaces:**

- Extend `IMcpAppInfoProvider` with the effective resolved transport, HTTP endpoint URI, resolved static headers, and request timeout so Gateway can make a per-call HTTP client without using the app's shared client.
- Inject `IMcpDelegatedToolInvoker` and the trusted `McpApps.Entries[appId].DelegatedCredentials` policy into each `McpAppNativeTool`; policy must never come from the manifest.

- [x] **Step 1: Add MCP App endpoint propagation and isolation tests.**

Configure an HTTP MCP App with a trusted delegation policy and non-Authorization manifest headers. Assert the resolved endpoint, headers, and timeout are available to the registered tool; concurrent calls with different caller contexts use different bearer tokens. Assert stdio apps and apps without trusted opt-in continue using the existing shared client.

- [x] **Step 2: Run the focused MCP App tests and confirm they fail.**

Run: `dotnet test src/OpenClaw.Tests/OpenClaw.Tests.csproj --configuration Release --filter "FullyQualifiedName~McpAppTests"`

Expected: FAIL because resolved endpoint metadata and delegated invocation are not wired to `McpAppNativeTool`.

- [x] **Step 3: Expose resolved connection options and route only opted-in HTTP App calls.**

Have `McpAppServer` provide its effective HTTP transport, endpoint override, manifest headers, and request timeout through `McpAppInfoProvider`. Pass trusted entry configuration and the Core invoker from `RuntimeInitializationExtensions` through `McpAppToolRegistrationExtensions`. The delegated path must replace manifest Authorization and preserve other headers; never use the app's shared client for an opted-in delegated call.

- [x] **Step 4: Run MCP App tests.**

Run the test command from Step 2.

Expected: PASS; trusted config alone enables delegation, manifest content cannot enable it, and non-opted-in apps keep current behavior.

---

### Task 13: Verify Expiry, Authorization Failures, and Secret Non-Disclosure

**Files:**

- Modify: `src/OpenClaw.Gateway/Mcp/McpDelegatedToolInvoker.cs`
- Modify: `src/OpenClaw.Agent/Tools/McpNativeTool.cs`
- Modify: `src/mcpapp/OpenClaw.McpApp/McpAppToolProvider.cs`
- Test: `src/OpenClaw.Tests/McpDelegatedCredentialProviderTests.cs`
- Test: `src/OpenClaw.Tests/McpDelegatedToolInvokerTests.cs`
- Test: `src/OpenClaw.Tests/McpServerToolRegistryTests.cs`
- Test: `src/OpenClaw.Tests/McpAppTests.cs`
- Test: `src/OpenClaw.Tests/GatewayWorkersTests.cs`

**Interfaces:**

- Delegated call failures use a stable failure category without token contents. Expired caller/delegated credentials and provider/client failures must cause zero upstream `tools/call` requests.

- [x] **Step 1: Add failing tests for expiry, 401/403, and secret non-disclosure.**

Assert caller/delegated expiry causes zero upstream calls. Configure an MCP server to return 401 and 403 and assert exactly one request, with no static Authorization retry. Capture logs and serialized Session, `InboundMessage`, tool arguments, and MCP request `_meta`; assert none contains caller or delegated token markers.

- [x] **Step 2: Run focused security tests and confirm failures.**

Run: `dotnet test src/OpenClaw.Tests/OpenClaw.Tests.csproj --configuration Release --filter "FullyQualifiedName~McpDelegatedCredentialProviderTests|FullyQualifiedName~McpDelegatedToolInvokerTests|FullyQualifiedName~McpServerToolRegistryTests|FullyQualifiedName~McpAppTests|FullyQualifiedName~GatewayWorkersTests"`

Expected: FAIL until expiry checks, no-retry semantics, and all redaction assertions pass.

- [x] **Step 3: Enforce expiry immediately before use and preserve upstream authorization failures.**

Check caller and delegated expiry immediately before creating/sending `tools/call`. Do not retry static Authorization or another provider mode for any response, including 401/403. Return stable failure codes without exception bodies or tokens.

- [x] **Step 4: Run the focused security tests.**

Run the test command from Step 2.

Expected: PASS; expired/provider-failed calls send zero tool requests, 401/403 never retry, and token markers are absent from serialized/logged surfaces.

---

### Task 14: Document Configuration and Run Repository Gates

**Files:**

- Modify: `docs/TOOLS_GUIDE.md`
- Modify: `docs/superpowers/specs/2026-10-01-mcp-per-call-delegated-credentials-design.md` only if implementation evidence changes an approved detail
- Test: `src/OpenClaw.Tests/OpenClaw.Tests.csproj`
- Build: `OpenClaw.Net.slnx` and the Gateway NativeAOT publish target

**Interfaces:**

- Documentation shows both explicit modes, required trusted Gateway config, defaults, expiry/re-auth behavior, fail-closed responses, and the unchanged behavior of endpoints without opt-in.
- Documentation states that Strategos must independently trust and validate Gateway-signed credentials when that mode is selected. Do not claim the OpenClaw implementation validates Strategos credentials.

- [x] **Step 1: Document the operator configuration and security contract.**

Add a concise MCP delegated credentials section to `docs/TOOLS_GUIDE.md` with one minimal example for each mode, per-server/per-MCP-App trusted configuration placement, secret-reference fields, and the no-fallback behavior. State that manifest data cannot opt an app into delegation and that delayed work requires a fresh authenticated caller context.

- [x] **Step 2: Run configuration, provider, runtime, transport, and app-focused tests together.**

Run: `dotnet test src/OpenClaw.Tests/OpenClaw.Tests.csproj --configuration Release --no-restore --filter "FullyQualifiedName~ConfigValidatorTests|FullyQualifiedName~McpDelegatedCredentialProviderTests|FullyQualifiedName~McpDelegatedToolInvokerTests|FullyQualifiedName~McpServerToolRegistryTests|FullyQualifiedName~McpAppTests|FullyQualifiedName~EndpointHelpersAuthenticationTests|FullyQualifiedName~AppsEndpointsTests|FullyQualifiedName~WebSocketEndpointsTests|FullyQualifiedName~GatewayWorkersTests|FullyQualifiedName~AgentRuntimeTests|FullyQualifiedName~MafAdapterTests"`

Expected: PASS with no warnings introduced by the changed projects.

- [x] **Step 3: Run the full Release build and test suite.**

Run: `dotnet build OpenClaw.Net.slnx --configuration Release --no-restore`

Expected: Build succeeds with no new warnings.

Run: `dotnet test OpenClaw.Net.slnx --configuration Release --no-build`

Expected: All repository tests pass.

Completed: the Release solution build succeeded in 180.7 seconds with the isolated temporary JWT probe present. After removing its non-portable absolute-path solution reference, a final Release build also succeeded in 35 seconds. The solution test suite passed with 3,342 passed, 12 skipped, and 0 failed; the skipped tests require Unix permissions or external live services.

- [x] **Step 4: Publish Gateway with NativeAOT and verify trimming.**

Run: `dotnet publish src/OpenClaw.Gateway/OpenClaw.Gateway.csproj --configuration Release --runtime win-x64 --self-contained true -p:PublishAot=true`

Expected: Publish succeeds without new trimming/AOT warnings attributable to delegated credential configuration, token providers, or signing. If a newly added JWT dependency produces warnings, replace it with an AOT-compatible implementation or revise the dependency before declaring completion.

Completed: after two canceled attempts, the Gateway win-x64 NativeAOT publish succeeded in 578.9 seconds with no warnings. The `OpenClaw.Gateway.exe` artifact was verified in the publish directory.

- [x] **Step 5: Run final documentation and diff checks.**

Run: `git diff --check`

Expected: no whitespace errors; configuration examples match the tested model names and defaults. Do not stage or commit unless separately requested.

Product code and tests were changed as listed above; this file records the implementation plan and validation results.

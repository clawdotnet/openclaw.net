# Task 2 Report — Unified Gateway integration execute endpoint

## Scope completed
Implemented the unified Gateway integration execute path that reuses `ActionExecuteTool`:
- `POST /api/integration/connector-actions/execute`
- `IntegrationApiFacade.ExecuteConnectorActionAsync(...)`
- `OpenClawHttpClient.ExecuteConnectorActionAsync(...)`
- focused gateway endpoint tests for unknown connector and missing approval payload cases

## Commit
- `efa1475462ce9a954bc1ad41d6c3ba9b498ce56c` — `feat: add integration connector action execute endpoint`

## TDD evidence

### RED
Command:
```bash
dotnet test src/OpenClaw.Tests/OpenClaw.Tests.csproj --filter "FullyQualifiedName~Integration_ExecuteConnectorAction" -v minimal
```

Observed failure (expected):
- `OpenClawHttpClient` did not contain `ExecuteConnectorActionAsync`
- new gateway test helpers failed to compile against current `ActionCall.Args` type until implementation work was completed
- endpoint/facade path was not yet available through the client call chain

Representative compiler errors:
- `CS1061: OpenClawHttpClient does not contain a definition for ExecuteConnectorActionAsync`
- `CS0266: cannot implicitly convert Dictionary<string,string> to IReadOnlyDictionary<string, JsonElement>`

### GREEN
Command:
```bash
dotnet test src/OpenClaw.Tests/OpenClaw.Tests.csproj --filter "FullyQualifiedName~Integration_ExecuteConnectorAction" -v minimal
```

Result:
- Passed: 2 tests
- Failed: 0
- Skipped: 0

### Additional focused verification
Command:
```bash
dotnet test src/OpenClaw.Tests/OpenClaw.Tests.csproj --filter "FullyQualifiedName~ConnectorActionContractTests|FullyQualifiedName~ActionExecuteToolTests|FullyQualifiedName~Integration_ExecuteConnectorAction" -v minimal
```

Result:
- Passed: 13 tests
- Failed: 0
- Skipped: 0

## Files changed
- `src/OpenClaw.Client/OpenClawHttpClient.cs`
- `src/OpenClaw.Core/Models/ConnectorActionContractModels.cs`
- `src/OpenClaw.Core/Models/Session.cs`
- `src/OpenClaw.Gateway/Composition/IntegrationApiFacade.cs`
- `src/OpenClaw.Gateway/Endpoints/IntegrationEndpoints.cs`
- `src/OpenClaw.Tests/GatewayAdminEndpointTests.cs`

## Implementation summary
- Added gateway endpoint wiring for `/api/integration/connector-actions/execute` with integration mutate auth scope and JSON body parsing.
- Added facade execution method that:
  - normalizes integration request payloads into `ConnectorActionExecuteRequest`
  - validates via `ConnectorActionContractValidator.ValidateForExecution(...)`
  - builds tool arguments with `Utf8JsonWriter` + source-generated `ActionProposal` metadata (avoids reflection-heavy anonymous serialization)
  - calls `ActionExecuteTool.ExecuteAsync(...)`
  - maps the tool JSON into `IntegrationConnectorActionExecuteResponse`
- Added client helper method for the new integration endpoint.
- Expanded integration DTOs to carry endpoint/tool semantics (`status`, `failureCode`, `message`, governance mapping, etc.) while also tolerating either flat request payloads or wrapped `Request` payloads.
- Registered the new governance mapping DTO in `CoreJsonContext` to keep the path source-generated / NativeAOT friendly.
- Added focused endpoint tests covering:
  - unknown connector -> `failed` / `policy_denied`
  - require approval with missing ticket -> `failed` / `approval_denied`

## Test output summary
- Filter `Integration_ExecuteConnectorAction`: PASS, 2 tests, ~1s
- Coupled connector suites (`ConnectorActionContractTests`, `ActionExecuteToolTests`, integration execute tests): PASS, 13 tests, ~1s

## Self-review
- Stayed within Task 2 delivery surface: gateway facade, endpoint, client, and focused gateway tests.
- Reused the existing `ActionExecuteTool` path exactly as planned instead of introducing a parallel execution implementation.
- Preserved NativeAOT/source-gen discipline by avoiding reflection-based request serialization in the gateway facade.
- Kept behavior fail-fast for malformed integration requests and contract validation failures.
- Limited model adjustments to the integration-specific DTOs needed to represent actual tool output semantics.

## AOT / JIT implications
- AOT: safe. The new gateway path uses source-generated `CoreJsonContext` metadata and manual JSON writing for the tool request payload.
- JIT: no special handling required; behavior is identical.

## Concerns
- The integration request DTO now accepts both a flat payload (`proposal`/`decision`) and a wrapped payload (`request`) to absorb plan drift between Task 1 artifacts and later task expectations. This is intentional for compatibility, but follow-on tasks should standardize on a single public request shape.
- The working tree still contains unrelated untracked planning/report artifacts under `.superpowers\sdd\` and `docs\superpowers\plans\`; they were intentionally left out of the code commit.

## Reviewer follow-up fixes (2026-07-15)
- Removed the alternate wrapped execute request shape from the public integration wire path. The endpoint, client, and source-generated contract now accept only `ConnectorActionExecuteRequest`, which is the same top-level shape exported by `ConnectorActionSchemaExporter`.
- Updated the integration execute flow to forward `decision`, `riskLevel`, and `approval` into `ActionExecuteTool` instead of dropping them at the facade boundary.
- Extended `ActionExecuteTool` to honor caller contract semantics for known connectors while preserving the existing unknown-connector `policy_denied` behavior:
  - `proceed` -> `execution_started`
  - `require_approval` with validated approval -> `execution_started`
  - `reject` -> `failed` / `policy_denied`
  - `escalate` -> `proposal_only`
- Kept the path synchronous and source-generated/AOT-friendly; the gateway still writes the tool payload via `Utf8JsonWriter`, and the tool only uses source-generated JSON for approval payload parsing.

### Added / updated focused tests
- `GatewayAdminEndpointTests.Integration_ExecuteConnectorAction_RequireApprovalKnownConnector_StartsExecutionAfterApprovalValidation`
- `ActionExecuteToolTests.ExecuteAsync_CallerRequireApprovalWithValidatedApproval_StartsExecution`
- `ConnectorActionContractTests.ExecuteRequest_WireShape_AlignsWithExportedSchema`

### Focused verification after reviewer fixes
Command:
```bash
dotnet test src/OpenClaw.Tests/OpenClaw.Tests.csproj --filter "FullyQualifiedName~Integration_ExecuteConnectorAction" -v minimal
```
Result:
- Passed: 3
- Failed: 0
- Skipped: 0

Command:
```bash
dotnet test src/OpenClaw.Tests/OpenClaw.Tests.csproj --filter "FullyQualifiedName~ConnectorActionContractTests|FullyQualifiedName~ActionExecuteToolTests" -v minimal
```
Result:
- Passed: 13
- Failed: 0
- Skipped: 0

### Notes
- The public request wire shape now matches schema-exported properties exactly: `proposal`, `decision`, optional `riskLevel`, optional `approval`.
- AOT: safe. No reflection-only serializer path was added.
- JIT: no behavior difference beyond the intended contract-semantic fixes.

## Reviewer second-round fixes (2026-07-15)
- Prevented caller-provided contract decisions from weakening policy outcomes in `ActionExecuteTool`.
  - `policy_denied` still short-circuits unknown connectors.
  - policy `proposal_only` now stays `proposal_only` even if caller sends `proceed`.
  - policy `require_approval` now stays `pending_approval` even if caller sends validated approval.
  - caller `reject` remains fail-closed and caller `escalate` can still further restrict to `proposal_only`.
- Hardened contract validation so approval payloads with reject-style `decisionType` values (`reject`, `rejected`, `deny`, `denied`, `decline`, `declined`) return `approval_denied`.

### Added / updated focused tests
- `ActionExecuteToolTests.ExecuteAsync_CallerApprovalCannotOverrideRequireApprovalPolicyIntoExecution`
- `ActionExecuteToolTests.ExecuteAsync_CallerProceedCannotOverrideProposalOnlyPolicyIntoExecution`
- `ActionExecuteToolTests.ExecuteAsync_RejectedApprovalPayload_ReturnsApprovalDenied`
- `ConnectorActionContractTests.ValidateForExecution_RequireApprovalRejectedDecisionType_FailsClosed`
- `GatewayAdminEndpointTests.Integration_ExecuteConnectorAction_ApprovalRejectPayload_ReturnsApprovalDenied`
- `GatewayAdminEndpointTests.Integration_ExecuteConnectorAction_CallerApprovalCannotOverrideRequireApprovalPolicy`

### Focused verification after second-round fixes
Command:
```bash
dotnet test src/OpenClaw.Tests/OpenClaw.Tests.csproj --filter "FullyQualifiedName~Integration_ExecuteConnectorAction|FullyQualifiedName~ActionExecuteToolTests|FullyQualifiedName~ConnectorActionContractTests" -v minimal
```
Result:
- Before changes: Passed 16, Failed 0, Skipped 0
- After changes: Passed 22, Failed 0, Skipped 0

### AOT / JIT implications
- AOT: safe. Changes stay within existing source-generated JSON and simple string-based policy/approval checks.
- JIT: no special handling; behavior change is limited to stricter fail-closed execution semantics.

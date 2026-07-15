# Task 1 Report — Unified Connector Contract and schema exporter

## Implemented
- Added `ConnectorActionExecuteRequest`, `ConnectorActionExecuteResponse`, and `ConnectorApprovalPayload`.
- Added integration-compatible mirror DTOs:
  - `IntegrationConnectorActionExecuteRequest`
  - `IntegrationConnectorActionExecuteResponse`
- Added `ConnectorActionContractValidator.ValidateForExecution(...)`:
  - returns `approval_denied` when `require_approval` requests have incomplete approval payloads
  - validates UTC ISO-8601 approval timestamps
- Added `ConnectorActionSchemaExporter.ExportV1()` with a JSON Schema payload covering `decision`, `approval`, and `ticketRef`.
- Registered the new contract types in `CoreJsonContext` for AOT/source-gen serialization.
- Added targeted contract tests for validation and schema export.

## TDD evidence
### RED
Command:
`dotnet test src/OpenClaw.Tests/OpenClaw.Tests.csproj --filter "FullyQualifiedName~ConnectorActionContractTests" -v minimal`

Result:
- Failed as expected with missing type/exporter compile errors:
  - `ConnectorActionExecuteRequest` not found
  - `ConnectorApprovalPayload` not found
  - `ConnectorActionContractValidator` not found
  - `ConnectorActionSchemaExporter` not found

### GREEN
Command:
`dotnet test src/OpenClaw.Tests/OpenClaw.Tests.csproj --filter "FullyQualifiedName~ConnectorActionContractTests" -v minimal`

Result:
- Passed: 2 tests, 0 failed, 0 skipped

## Files changed
- `src/OpenClaw.Core/Models/ConnectorActionContractModels.cs`
- `src/OpenClaw.Core/ConnectorActions/ConnectorActionSchemaExporter.cs`
- `src/OpenClaw.Core/Models/Session.cs`
- `src/OpenClaw.Tests/ConnectorActionContractTests.cs`

## Self-review
- Kept the change AOT-friendly; no reflection-based runtime schema generation was introduced.
- Contract DTOs stay small and explicit.
- JSON source generation was updated for the new model types.
- Validation path matches the task brief and existing approval semantics.

## Concerns
- None blocking.

## Reviewer-finding fixes
- Updated the exported schema so `approval` fields are only required in the `if/then` branch when `decision == require_approval`.
- Updated runtime validation to reject unsupported decision values with `unsupported_decision`.
- Strengthened tests to parse the JSON schema and assert conditional structure instead of checking substrings.

## Verification
- Command: `dotnet test src/OpenClaw.Tests/OpenClaw.Tests.csproj --filter "FullyQualifiedName~ConnectorActionContractTests" -v minimal`
- Result: Passed (3 tests, 0 failed)
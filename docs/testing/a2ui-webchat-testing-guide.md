# A2UI WebChat Testing Technical Guide

## 1. Document Objective

This document provides guidance for testing A2UI functionality in OpenClaw using WebChat or the Companion Canvas tab, covering:

- A2UI v0.8 JSONL push rendering
- A2UI v0.9 structured surface lifecycle
- Cross-surface isolation and regression checks
- Troubleshooting and fixes for common failure scenarios

## 2. Session Summary

### 2.1 Completed

- Clarified the A2UI test entry points and execution path in WebChat.
- Provided executable step-by-step scripts for v0.8 + v0.9 + regression checks.
- Successfully verified that `a2ui_update_data_model` and `canvas_snapshot` work on the `details` surface.

### 2.2 Key Issue

`a2ui_create_surface` failed. The root cause was an incorrect `components` parameter format.

Incorrect example:

["Text","Button"]

Required format:

- `components` must be a string array
- Every item in the array must be a valid JSON string
- Each component object should include at least `type` and `id`

### 2.3 Observed Impact

Because surface creation failed:

- `dataModelJson` in the `details` snapshot could still update to `{"status":"approved"}`
- but `components` remained empty
- and `frameCount` was 0

This matches what was observed in the session and confirms that data model updates do not imply that the component tree was created.

## 3. Test Scope

### 3.1 Covered

- `canvas_present`
- `a2ui_push`
- `a2ui_create_surface`
- `a2ui_update_data_model`
- `a2ui_update_components`
- `a2ui_sync_ui_to_data`
- `a2ui_delete_surface`
- `a2ui_reset`
- `canvas_snapshot`

### 3.2 Out of Scope

- Remote webpage canvas navigation (`http/https`)
- Remote page script execution

## 4. Prerequisites

1. Start the gateway: `dotnet run --project src/OpenClaw.Gateway`
2. Open WebChat: `/chat`
3. Ensure the current session is a websocket session
4. Ensure Canvas is enabled (`OpenClaw:Canvas:Enabled=true`)

It is recommended to run one basic tool call in WebChat first to verify the toolchain is working.


## 5. Standard Test Flow

This section provides a “dialog-style minimum smoke script (12 steps)” that you can paste directly into WebChat or the Companion Canvas tab, one step at a time. Wait for each response before sending the next step.

---
User: Please open Canvas with `surfaceId=main`.

Assistant: Call `canvas_present`
```json
{
	"surfaceId": "main"
}
```
(Expected: `main` surface opens successfully, without errors)

User: Please push a v0.8 text frame to the `main` surface.

Assistant: Call `a2ui_push`
```json
{
	"surfaceId": "main",
	"contentType": "application/x-a2ui+jsonl;version=0.8",
	"frames": "{\"type\":\"text\",\"id\":\"hello\",\"text\":\"ok\"}"
}
```
(Expected: one text block is rendered on `main`)

User: Please snapshot the `main` surface.

Assistant: Call `canvas_snapshot`
```json
{
	"surfaceId": "main"
}
```
(Expected: `components` is non-empty, `frameCount=1`)

User: Please create a v0.9 surface with `surfaceId=details`.

Assistant: Call `a2ui_create_surface`
```json
{
	"surfaceId": "details",
	"components": [
		"{\"type\":\"Text\",\"id\":\"title\",\"text\":\"ok\"}"
	]
}
```
(Expected: `details` surface is created and tab becomes visible)

User: Please snapshot the `details` surface.

Assistant: Call `canvas_snapshot`
```json
{
	"surfaceId": "details"
}
```
(Expected: `components` is non-empty, `frameCount=1`)

User: Please change `details` data model status to `approved`.

Assistant: Call `a2ui_update_data_model`
```json
{
	"surfaceId": "details",
	"dataModelJson": "{\"status\":\"approved\"}"
}
```
(Expected: no error; snapshot `dataModelJson` changes to `approved`)

User: Please change the `details` surface content to `updated`.

Assistant: Call `a2ui_update_components`
```json
{
	"surfaceId": "details",
	"components": [
		"{\"type\":\"Text\",\"id\":\"title\",\"text\":\"updated\"}"
	]
}
```
(Expected: rendered content on `details` changes to `updated`)

User: Please snapshot the `details` surface.

Assistant: Call `canvas_snapshot`
```json
{
	"surfaceId": "details"
}
```
(Expected: only 1 component remains and its content is `updated`)

User: Please sync UI and data for the `details` surface.

Assistant: Call `a2ui_sync_ui_to_data`
```json
{
	"surfaceId": "details",
	"syncMode": "full"
}
```
(Expected: current UI/data state is returned)

User: Please delete the `details` surface.

Assistant: Call `a2ui_delete_surface`
```json
{
	"surfaceId": "details"
}
```
(Expected: `details` surface is removed and the tab disappears)

User: Please snapshot the `details` surface.

Assistant: Call `canvas_snapshot`
```json
{
	"surfaceId": "details"
}
```
(Expected: empty result or surface not found)

User: Please reset the `main` surface.

Assistant: Call `a2ui_reset`
```json
{
	"surfaceId": "main"
}
```
(Expected: `main` surface is cleared)

---

## 6. Result Criteria Matrix

- `createSurface` success: `details.components` is non-empty and `frameCount > 0`
- `updateDataModel` success: `dataModelJson` is updated correctly
- `updateComponents` success: component tree is replaced and reflected in snapshot
- Surface isolation correctness: `main` and `details` states are independent
- `delete`/`reset` correctness: target surface state is cleared

## 7. Troubleshooting

### 7.1 createSurface Parse Failure

Check in this order:

1. Whether `components` is a string array
2. Whether each string is valid JSON
3. Whether each component includes `type` and `id`

### 7.2 Snapshot Has dataModel but Empty components

This usually means:

- `createSurface` did not succeed
- only `updateDataModel` was executed

Fix: correct `createSurface` first, then continue with subsequent steps.

### 7.3 catalog-Related Failures

- If `catalogId` mismatches client negotiation, omit `catalogId` first and use automatic negotiation
- Keep catalog consistent within a single surface lifecycle

## 8. Implemented Automated Test Coverage

The following capabilities are already implemented in the test project and validated via `CanvasToolTests`:

- v0.9 lifecycle WebSocket-level integration test
	- Test: `A2UiV09Lifecycle_WebSocketRoundTrip_CoversCreateUpdateSnapshotSyncAndDelete`
	- Coverage: `createSurface -> snapshot -> updateDataModel -> updateComponents -> snapshot -> syncUIToData -> deleteSurface`
- Negative validation for invalid `components` parameters
	- Test: `A2UiCreateSurface_RejectsNonStringComponentArray`
	- Test: `A2UiUpdateComponents_RejectsNonStringComponentArray`
	- Coverage: rejects non-string-array component elements and prevents send
- Unified snapshot assertion template
	- File: `CanvasSnapshotAssertions.cs`
	- Method: `AssertV09Snapshot`
	- Unified checks: `dataModelJson`, `components`, `frameCount`, `diagnostics`

## 9. Support Matrix and Test Evidence

### 9.1 Capability Support Matrix

- WebChat / Companion Canvas tab
	- Verified: A2UI v0.8 push rendering, A2UI v0.9 surface lifecycle, snapshot regression checks
	- Known limitation: `a2ui_eval` is capability-restricted by default (first-party clients do not declare this capability by default)
- catalog negotiation
	- Recommended strategy: omit `catalogId` first and rely on automatic negotiation
	- Fallback strategy: if the session only exposes a v0.8 catalog, run v0.8 scripts for rendering and regression

### 9.2 Automated Test Evidence

- Executed command
	- `dotnet test src/OpenClaw.Tests/OpenClaw.Tests.csproj --filter "FullyQualifiedName~CanvasToolTests"`
- Latest result
	- Total: 25
	- Passed: 25
	- Failed: 0
	- Skipped: 0

### 9.3 Final Conclusion

- Confirmed core pitfall: `a2ui_create_surface.components` must be a JSON string array.
- Established a closed loop of manual scripts + WebSocket-level automated tests + unified snapshot assertions.
- This document is directly usable for end-to-end validation and regression execution in both WebChat and the Companion Canvas tab.
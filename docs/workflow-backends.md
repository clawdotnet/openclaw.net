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
| `openclaw.run_workflow` | Start a workflow run. |
| `openclaw.get_workflow_run` | Read current status, events, pending inputs, and output. |
| `openclaw.respond_workflow` | Send a human or system response to a pending input port. |

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

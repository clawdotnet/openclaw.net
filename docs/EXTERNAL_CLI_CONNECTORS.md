# External CLI Connectors

External CLI connectors let OpenClaw.NET wrap official platform CLIs through a governed native tool named `external_cli`. The feature is disabled by default and is intentionally not a general-purpose shell.

Official CLIs provide platform depth. OpenClaw.NET provides the runtime controls: named command allowlists, risk scoring, previews, approvals, redaction, timeouts, audit records, and runtime events.

## Security Model

External CLIs can operate under powerful user, bot, cloud, cluster, or payment identities. Treat mutating commands as high-risk. Use least privilege, dry-run previews, approvals, and audit logs.

The connector does not accept arbitrary command strings. Agents call a configured connector and command name with named parameters:

```json
{
  "action": "execute",
  "connector": "gh",
  "command": "issue_list",
  "parameters": {
    "repo": "clawdotnet/openclaw.net"
  }
}
```

The runtime expands configured argument templates directly into `ProcessStartInfo.ArgumentList`. It does not pass through a shell and it rejects missing or unknown parameters unless the specific command allows them.

## Configuration

`OpenClaw:ExternalCli:Enabled` must be true before the `external_cli` tool is registered. Each connector and each command must also be explicitly configured.

```json
{
  "OpenClaw": {
    "ExternalCli": {
      "Enabled": true,
      "DefaultTimeoutSeconds": 60,
      "MaxStdoutBytes": 262144,
      "MaxStderrBytes": 65536,
      "RedactSecrets": true,
      "AllowFreeformCommands": false,
      "RequireApprovalForMutatingCommands": true,
      "Connectors": {
        "gh": {
          "Enabled": true,
          "DisplayName": "GitHub CLI",
          "Executable": "gh",
          "DefaultOutputFormat": "json",
          "StatusCommand": {
            "Args": [ "auth", "status" ],
            "TimeoutSeconds": 20
          },
          "VersionCommand": {
            "Args": [ "--version" ],
            "TimeoutSeconds": 10
          },
          "Commands": {
            "repo_view": {
              "Description": "View repository metadata",
              "ArgsTemplate": [ "repo", "view", "{{repo}}", "--json", "name,owner,description,url,isPrivate" ],
              "RiskLevel": "low",
              "ReadOnly": true,
              "StructuredOutput": "json",
              "Parameters": {
                "repo": {
                  "Required": true,
                  "Pattern": "^[A-Za-z0-9_.-]+/[A-Za-z0-9_.-]+$"
                }
              }
            },
            "issue_create": {
              "Description": "Create a GitHub issue",
              "ArgsTemplate": [ "issue", "create", "--repo", "{{repo}}", "--title", "{{title}}", "--body", "{{body}}" ],
              "RiskLevel": "medium",
              "ReadOnly": false,
              "RequiresApproval": true,
              "StructuredOutput": "text",
              "Parameters": {
                "repo": { "Required": true },
                "title": { "Required": true, "MaxLength": 200 },
                "body": { "Required": true, "MaxLength": 16000 }
              }
            }
          }
        }
      }
    }
  }
}
```

Important defaults:

- `Enabled=false`: no tool registration.
- `AllowFreeformCommands=false`: raw commands are rejected.
- `RequireApprovalForMutatingCommands=true`: non-read-only commands require approval unless policy is changed.
- `RiskLevel=high`: always approval-required.
- `ReadOnly=false`: treated as mutating.

## Preview And Approval

Use preview before execution:

```bash
openclaw external preview gh repo_view --param repo=clawdotnet/openclaw.net
```

Preview returns:

- resolved executable and argument list
- redacted command-line preview
- risk level
- read-only or mutating classification
- whether approval is required
- output format
- required identity or scopes, when configured
- a stable approval fingerprint

Preview does not execute the command unless `--dry-run` is supplied and the command has an explicit `DryRunArgsTemplate`. The runtime never guesses dry-run flags.

Approval-required admin execution must include the matching preview fingerprint. The CLI does this when `--yes` is supplied after preview review:

```bash
openclaw external execute gh issue_create \
  --param repo=clawdotnet/openclaw.net \
  --param title="Example" \
  --param body="Example body" \
  --yes
```

Agent tool calls use the existing OpenClaw tool approval path. If the command template, resolved args, or policy changes between approval and execution, execution is blocked by fingerprint mismatch.

## Audit, Events, And Redaction

External CLI execution writes an append-only audit record with:

- connector and command
- executable
- redacted argument preview
- argument and parameter hashes
- actor, session, channel, and sender where available
- approval fingerprint where available
- exit code, duration, timeout flag
- stdout and stderr truncation flags
- risk level and working directory

Runtime events are emitted for status checks, previews, dry-runs, execution, failures, timeouts, truncation, redaction, and policy blocks. Raw secrets are not stored in audit records.

Redaction applies to argument previews, stdout, stderr, audit records, runtime events, and error messages. Add connector or command `RedactionRules` for platform-specific token formats.

## Output Parsing

Set `StructuredOutput` per command:

- `json`: parse stdout as JSON and return parsed JSON alongside redacted stdout.
- `ndjson`: parse newline-delimited JSON into a JSON array.
- `csv`, `table`, `text`: returned as redacted text in the initial implementation.

The connector does not inject global `--json` flags. Put output flags in each command template.

## Conservative Presets

Keep presets disabled by default and enable only the commands needed for the operator surface.

### GitHub CLI

Read-only examples:

- `auth status`
- `repo view`
- `issue list`
- `pr list`
- `pr view`
- `release list`

Approval-required examples:

- `issue create`
- `issue comment`
- `pr review`
- `pr merge`
- `release create`

### Azure CLI

Read-only examples:

- `account show`
- `group list`
- `resource list`
- `webapp list`

Approval-required examples:

- resource create, update, or delete
- deployment create
- role assignment changes

### kubectl

Read-only examples:

- `config current-context`
- `get pods -o json`
- `get services -o json`
- `get deployments -o json`
- `describe` as text when approved by policy

High-risk approval-required examples:

- `apply`
- `delete`
- `scale`
- `rollout restart`
- `exec`
- `port-forward`

Logs can expose secrets and should be at least medium risk.

### Stripe CLI

Read-only examples:

- listen status, if safe for your environment
- fixtures/list, if applicable
- customers/list, only with least-privilege credentials

High-risk approval-required examples:

- payment mutations
- refunds
- customer and subscription mutations
- webhook trigger
- event replay

### Lark / Feishu CLI

Read-only examples:

- `auth status`
- schema inspection
- calendar agenda
- docs search/read
- sheets read
- mail search/read
- meeting minutes query

Approval-required examples:

- send messages
- write docs
- write sheets
- send email
- approve or reject workflows
- create or update OKRs
- raw API calls

## Admin API

The gateway exposes:

- `GET /admin/external-cli/connectors`
- `GET /admin/external-cli/connectors/{connector}`
- `GET /admin/external-cli/connectors/{connector}/commands`
- `POST /admin/external-cli/preview`
- `POST /admin/external-cli/execute`

POST endpoints require CSRF for browser-session auth. Execute requires operator role and matching approval metadata for approval-required commands.

## CLI

```bash
openclaw external list
openclaw external status gh
openclaw external commands gh
openclaw external preview gh repo_view --param repo=clawdotnet/openclaw.net
openclaw external execute gh repo_view --param repo=clawdotnet/openclaw.net
```

Use `--json` for machine-readable responses.

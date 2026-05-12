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

`OpenClaw:ExternalCli:Enabled` must be true before the `external_cli` tool is registered. Connectors can be configured directly or imported from built-in presets with `Presets`, but preset connectors remain disabled until the operator explicitly enables them.

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
      "Presets": [],
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

Preset opt-in keeps config short while preserving the same approval, preview, redaction, and audit behavior:

```json
{
  "OpenClaw": {
    "ExternalCli": {
      "Enabled": true,
      "Presets": [ "gh", "github-copilot", "codex", "gemini", "lark" ],
      "Connectors": {
        "gh": { "Enabled": true },
        "github-copilot": { "Enabled": false },
        "codex": { "Enabled": false },
        "gemini": { "Enabled": false },
        "lark": {
          "Enabled": false,
          "Executable": "lark-cli"
        }
      }
    }
  }
}
```

Use `openclaw external presets` to inspect the built-in catalog. Presets only provide command templates and conservative metadata; they do not install CLIs, grant credentials, or bypass platform authentication.

Important defaults:

- `Enabled=false`: no tool registration.
- `AllowFreeformCommands=false`: raw commands are rejected.
- `RequireApprovalForMutatingCommands=true`: non-read-only commands require approval unless policy is changed.
- `Presets=[]`: no built-in connector templates are imported.
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

## Built-In Presets

Built-in presets are conservative templates for common CLIs. They are disabled by default, are imported only when listed under `ExternalCli:Presets`, and still require a connector override such as `"gh": { "Enabled": true }` before execution.

Keep enabled presets narrow. AI-agent CLIs such as GitHub Copilot CLI, Codex CLI, and Gemini CLI are approval-required by default because a prompt can send repository context to an external service and may use tools under the operator's account.

### GitHub CLI

Preset id: `gh`

Read-only commands:

- `repo_view`
- `issue_list`
- `pr_list`
- `pr_view`
- `release_list`

Approval-required examples:

- `issue_comment`

### Azure CLI

Preset id: `az`

Read-only commands:

- `account_show`
- `group_list`
- `resource_list`

Approval-required examples:

- resource create, update, or delete
- deployment create
- role assignment changes

### kubectl

Preset id: `kubectl`

Read-only commands:

- `current_context`
- `get_pods`
- `get_pods_all`
- `get_services_all`
- `get_deployments_all`

High-risk approval-required examples:

- `apply`
- `delete`
- `scale`
- `rollout restart`
- `exec`
- `port-forward`

Logs can expose secrets and should be at least medium risk.

### Stripe CLI

Preset id: `stripe`

Read-only commands:

- `version`

Approval-required read commands:

- `customers_list`, because customer data can contain PII

High-risk approval-required examples:

- payment mutations
- refunds
- customer and subscription mutations
- webhook trigger
- event replay

### Lark / Feishu CLI

Preset id: `lark`

The Lark preset assumes a `lark-cli` compatible executable. If your internal or vendor-provided CLI uses different subcommands, keep the preset disabled and override `Executable` or command templates in config.

Read-only commands:

- `auth_status`
- `docs_search`
- `docs_read`
- `sheets_read`

Approval-required examples:

- `message_send`

### GitHub Copilot CLI

Preset id: `github-copilot`

This preset targets the `copilot` executable documented by GitHub Copilot CLI. The `prompt` command uses non-interactive prompt mode and is high-risk approval-required by default.

Commands:

- `version`
- `prompt`

Reference: [GitHub Copilot CLI command reference](https://docs.github.com/en/copilot/reference/copilot-cli-reference/cli-command-reference)

### Codex CLI

Preset id: `codex`

This preset targets `codex exec`. Read-only exec uses an explicit `--sandbox read-only`; workspace-write exec is high-risk approval-required.

Commands:

- `version`
- `exec_readonly`
- `exec_readonly_json`
- `exec_workspace_write`

Reference: [Codex non-interactive mode](https://developers.openai.com/codex/noninteractive)

### Gemini CLI

Preset id: `gemini`

This preset targets `gemini -p` / `gemini --prompt` non-interactive execution. The prompt command is high-risk approval-required by default because Gemini CLI can use local context and tools depending on local policy.

Commands:

- `version`
- `prompt`

Reference: [Gemini CLI reference](https://github.com/google-gemini/gemini-cli/blob/main/docs/cli/cli-reference.md)

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
openclaw external presets
openclaw external list
openclaw external status gh
openclaw external commands gh
openclaw external preview gh repo_view --param repo=clawdotnet/openclaw.net
openclaw external execute gh repo_view --param repo=clawdotnet/openclaw.net
```

Use `--json` for machine-readable responses.

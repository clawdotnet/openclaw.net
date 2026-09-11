# Companion configuration through chat

The Companion now has a real configuration review/apply flow. Open **Settings** below the chat composer to load the gateway settings, or select **Configure via chat** and describe a change. `/configure set the session timeout to 45 minutes` enters the same flow. The assistant proposes values; the review card lets you edit them before **Apply changes** saves them. **Cancel** discards the draft.

Examples:

- Set the session timeout to 45 minutes.
- Enable read-only mode and require tool approval.
- Turn on memory compaction and keep the most recent 20 turns.
- Show token usage after each response.

The manual setting picker uses the same API and works without a functioning language model, provided the gateway is reachable. Model interpretation is a separate request with no tools. It receives setting names and types, not existing configuration values. Configuration instructions are not added to the conversation history. Recognized credential patterns are redacted before interpretation; enter credentials only in the dedicated masked setup fields.

## First-time setup

Type `/setup` or choose **Set up my assistant**. The inline provider/model/API-key form calls the existing local setup and startup service. **Advanced setup** exposes workspace, model presets, and embedded-model installation. A local CLI/runtime installation is still required to start a managed gateway. You can also connect to an existing gateway in Setup & runtime.

`use dark mode` and `use light mode` apply the Companion theme locally without a gateway or model.

## Settings covered

The configuration API exposes the scalar admin settings for sessions, tool approval and autonomy, shell/browser access, history/compaction, retention, and messaging-channel enablement, signature validation, direct-message policies, and WhatsApp connection metadata/secret references. It also supports the base model name, token limit, temperature, and selection of an already configured default model profile.

Provider credentials and WhatsApp worker-account objects use their dedicated setup forms. A base model edit does not overwrite named model profiles. A model controlled by `MODEL_PROVIDER_MODEL` must be changed in that environment variable. Automations, workflows, memory, approvals, and payment operations continue to use their existing runtime tools and management APIs; they are not treated as scalar settings patches.

## API and persistence

- `GET /admin/configuration` returns allowed current values and an opaque revision.
- `POST /admin/configuration/preview` accepts `{ "revision": "…", "instruction": "…" }` or an explicit `changes` object. Nothing is saved.
- `POST /admin/configuration/apply` accepts `{ "revision": "…", "changes": { "sessionTimeoutMinutes": 45 } }`.

All three routes require admin access; browser-session POST requests require CSRF. POST requests are rate limited and body-size limited. The server rejects protected/unknown fields, invalid types, invalid configuration, and stale revisions. Apply revalidates the edited values under the same lock used by existing admin settings updates, so a GUI edit cannot be silently overwritten by an older chat proposal. Revisions also change across gateway instances.

The override file is written atomically before live configuration is changed. Unix files are created with owner-only read/write permissions. Save failures leave runtime state unchanged. Saved overrides are loaded on startup; malformed persisted data causes an explicit startup error. Reset uses the original configuration defaults rather than the loaded override. Restart warnings accumulate relative to the configuration with which the gateway started and are returned until the relevant settings are restored or the process restarts. Restart the managed gateway through Setup & runtime; remote gateways must be restarted by their operator.

The API does not automatically restart the gateway or execute model-suggested mutations. Audit entries record the action outcome without recording settings values or secrets.

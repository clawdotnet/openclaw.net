# OpenClaw Companion: chat-first prototype

Prepared from the current Companion AXAML and view-model surface on September 10, 2026. The Companion now implements reviewed scalar admin configuration and inline local setup; see [the implemented scope](../companion-chat-configuration.md). Broader conversational actions in this brief remain proposals. The scope is all current Companion features, not every CLI or gateway capability.

## Recommended implementation direction

Keep Avalonia and build a small, branded component system on the existing Fluent theme. The app already uses Avalonia 12.0.4, Inter, and CommunityToolkit.Mvvm. Prioritize navigation, information hierarchy, reusable cards, readable conversation rendering, and progressive disclosure before changing libraries.

Options:

| Approach | Best use | Tradeoff |
| --- | --- | --- |
| Existing Avalonia Fluent theme + custom control themes | Recommended foundation; preserve the current stack and build a distinctive cross-platform identity | Requires deliberate component and interaction design |
| FluentAvalonia and its controls gallery | WinUI-inspired navigation and richer desktop controls | Verify Avalonia 12 compatibility before adoption; its README currently documents Avalonia 11 requirements |
| Semi.Avalonia + Ursa | Clean application styling and additional business UI controls | Validate exact packages against Avalonia 12 and assess theme migration in a small spike |
| Figma design-system kit or commissioned custom design | Establish typography, tokens, component states, and interaction patterns before implementation | A design kit is a visual starting point, not an Avalonia implementation |
| Figma Make | Test realistic conversations, forms, and transitions quickly | Its generated web prototype must be translated into Avalonia views and commands |

Reference sources: [Avalonia themes](https://docs.avaloniaui.net/docs/styling/themes), [FluentAvalonia](https://github.com/amwx/FluentAvalonia), [Semi.Avalonia](https://github.com/irihitech/Semi.Avalonia), [Ursa](https://github.com/irihitech/Ursa.Avalonia), [Figma Make introduction](https://developers.figma.com/docs/code/intro-to-figma-make/).

## Copy everything below into Figma Make

Build a polished, interactive desktop application prototype called **OpenClaw Companion**. It is a personal AI assistant and an optional control center for its runtime, models, connected channels, workflows, automations, memory, and approvals.

The product should feel approachable to someone who has never configured an AI provider, while retaining the control a developer or administrator needs. Make chat the default home. Users should be able to initiate every configuration task in natural language, complete it with concise interactive cards, and optionally open the equivalent graphical settings. Hide technical detail behind contextual disclosure, while keeping failures and decisions visible.

Create a functional prototype using synthetic local data and deterministic scripted conversations. Do not call real services, request real credentials, install software, or execute payments. Include a subtle Prototype indicator and a demo scenario selector. Provide a reset-demo action.

### Visual direction

Create a calm, distinctive desktop workspace with strong typography and disciplined spacing. Use warm off-white backgrounds, white panels, dark ink text, muted teal accents, and restrained amber for attention. Include a polished dark theme with charcoal surfaces and readable muted text. Avoid excessive gradients, giant dashboard statistics, decorative robot illustrations, neon colors, and borders around every message.

Use Inter or a comparable readable sans serif. Use 15px body text, 13px secondary labels, and 24–28px page titles. Reserve monospace for technical details. Use an 8px spacing rhythm, 10–14px corner radii, subtle separators, and soft shadows only for elevated surfaces. Design a simple abstract brand mark and a consistent outline icon set.

Target a 1440×960 desktop window, adapt gracefully to 1024×768, and provide a compact 768px layout. Use a roughly 232px sidebar, a flexible central workspace, and an optional 360–420px context panel. At narrower widths, the context panel becomes a dismissible overlay. Conversations should remain comfortably readable rather than stretching edge to edge.

Specify light/dark semantic color tokens, spacing, typography, and component states. Use accessible contrast, visible keyboard focus, labeled controls, keyboard navigation, reduced motion, and status labels alongside color. Long content must scroll without hiding the composer or critical actions.

### Navigation and information architecture

Sidebar primary destinations:

- **Chat**: new conversation, recent conversations, search history.
- **Activity**: current work, workflow runs, automation runs, and approval requests; show a badge only when action is needed.
- **Connections**: channels, WhatsApp setup, plugins, compatibility, and readiness.
- **Library**: workflows, automations, templates, memory, and profiles.

Sidebar footer: **Settings**, user identity, and one compact connection-status control. Place advanced runtime diagnostics and the experimental Payment Lab under Settings. Preserve easy direct navigation for users who prefer forms. Provide global search and a Cmd/Ctrl+K command palette with plain-language actions and technical aliases.

Keep the current runtime and workspace identifiable, but do not suggest multi-workspace switching is implemented. Display a small model/context indicator near the composer. “Show technical details” is a persistent presentation preference, not a permissions switch. Respect viewer/operator/admin restrictions independently of presentation preferences.

### Core interaction contract

Every setting can be discovered through chat. The assistant asks only for missing information, then renders a structured card with editable fields. Each card offers **Edit in settings**, preserving the draft and context. Settings pages offer **Ask assistant**, supplying the relevant context to chat.

Chat and GUI must use the same prototype state: a change applied in one is immediately reflected in the other. Distinguish drafts, validated changes, applying, success, partial failure, and failure. Do not mark an operation complete until its simulated result arrives. Show a concise before/after summary for configuration changes; require deliberate confirmation for destructive changes, access changes, externally visible actions, and financial actions. Routine reads should run immediately. Provide undo only for operations the prototype explicitly models as reversible.

Credentials belong in masked secure-entry cards, never ordinary chat messages or history. Display only credential status or a masked reference after submission. Do not let the assistant invent successful connections. Role restrictions, missing integrations, and unsupported operations need understandable explanations and a concrete next step.

Natural-language scalar admin configuration, editable review/apply, and inline local setup are implemented in Companion. In the prototype's design notes, distinguish current feature actions from proposed additions. Proposed enhancements beyond the implemented scalar settings editor include universal chat configuration, cross-surface synchronized configuration drafts, automation creation/editing through conversation, and new editing controls for surfaces currently limited to inspection. Do not silently imply those operations already exist in the desktop app.

### Screens and complete current-feature coverage

1. **First launch and assisted setup.** Start with “What would you like help with?” and suggestions such as “Set up my assistant,” “Connect WhatsApp,” and “Use a model on this computer.” Offer guided chat and a conventional setup form. Explain online providers versus Ollama versus the embedded runtime in plain language. Preserve provider/model/preset selection, workspace path, provider API key, and Set Up and Start. Represent the existing provider choices: OpenAI, Anthropic, Gemini, Ollama, Embedded. Use illustrative model names without making claims about current availability or price. Local setup must remain usable before an AI provider or gateway works: use a deterministic guided wizard styled as conversation. Show local-model status, install, verify, remove, model-file selection for .gguf/.litertlm, unavailable-package states, and the experimental adapter qualification where applicable. A local model must not imply all connected tools operate offline.

2. **Chat home and active conversation.** Show a welcoming empty state, recent work, a multiline composer, suggested requests, streaming responses, formatted content, collapsible tool activity, errors with recovery, and inline configuration cards. Enter sends; Shift+Enter inserts a newline. Demonstrate reconnecting without losing the draft. Keep technical event payloads out of normal assistant prose.

3. **Runtime overview.** Reimagine the current Home dashboard as a compact health overview reachable from the status control and Activity. Preserve active sessions, pending approvals, provider routes, plugin counts, runtime summary, channel readiness, recent approval decisions, deployment information, refresh timestamps, and current quick actions. Lead with actionable issues and use metrics as supporting detail.

4. **Runtime and connection settings.** Preserve gateway URL, operator username/token, connect/disconnect, local runtime start/stop/refresh, auto-start, configuration location, remember-token behavior, explicit plaintext fallback, debug mode, and health/availability. Put technical storage and transport details behind Advanced; explain insecure fallback clearly when exposed. Keep local process state separate from remote connection state. Support unavailable runtime, authentication failure, and permission-denied examples.

5. **Conversation history.** Reimagine Sessions with searchable conversation rows, channel and sender filters, state, starred-only filter, tag filter, pagination, and selected-session detail. Prefer human-readable names, snippets, and dates. Keep IDs accessible in details. Filtering by starred status does not imply a new star-editing capability.

6. **Canvas and outputs.** Integrate the existing Canvas as an optional workspace beside chat, with an expand-to-full-view control. Show generated A2UI surfaces, frames, interactive fields and actions, clear, and export snapshot. Include empty, loading, unsupported-content, and rendering-error states. Do not imply unrestricted HTML or JavaScript support. Label exports accurately as snapshots of the represented canvas state.

7. **Approval inbox.** Place Approvals in Activity and surface urgent requests inline in the relevant chat. Each card explains the requested action, tool, origin, affected resource, and consequence; provide Approve and Deny. Preserve decision history. Put arguments and identifiers behind details. After a decision, update both inbox and conversation. Include expired/already-resolved and unauthorized states; do not present an approval as proof the underlying action completed.

8. **Workflows.** In Library, show available workflows with readable descriptions and a detail panel. Preserve workflow input, Run, run lookup by ID, status/output/errors, run-event timeline, pending input, and Send Response. Demonstrate a workflow pausing for user input and resuming after the response. Use clear step states: waiting, running, needs input, complete, failed.

9. **Automations.** Preserve automation list/detail, templates, run history, dry run, live run, replay run, clear quarantine, and delete. Explain quarantine as “Paused after a problem,” with technical wording in details. A dry run needs a distinct simulation label. Live run and replay must explain whether work will execute again. Add a proposed chat-based automation builder with an editable trigger/schedule/action summary and a graphical alternative; mark this as an extension in design notes. Show timezone and next run for the proposed schedule flow. Quarantine recovery and delete require contextual confirmation.

10. **Connections and plugins.** Reimagine Plugins & Channels as searchable cards with installed/detected status, health, channel readiness, and compatibility detail. Preserve the compatibility catalog and explain supported/partial/unsupported states. Use a separate detail inspector for technical diagnostics. Do not invent one-click plugin installation or connection wizards for channels whose setup is not represented in this brief.

11. **WhatsApp setup.** Provide a guided choice between Official Cloud API and available bridge modes. Preserve enabled state, mode, reload/save/restart, active backend, status, authentication state, QR pairing when relevant, warnings, and validation errors. Advanced Cloud API fields include webhook path/public base URL, verify-token value/reference, signature validation, app-secret value/reference, Cloud API token, phone-number ID, and business-account ID. Bridge details include detected plugin, plugin ID, built-in bridge URL/token/reference, and send-exception suppression. Only show fields relevant to the selected mode. Do not show QR pairing as an Official Cloud API flow. Model QR expiry, waiting, paired, and reconnect states using a clearly nonfunctional demo QR.

12. **Memory and profiles.** Under Library, preserve profile list and detail, searchable memory notes, and learning proposals. Make remembered information easy to inspect and search. Use plain language and reveal provenance only where the fixture supplies it. Current Companion controls here are primarily inspection/search: do not imply editing, forgetting, or accepting proposals already exists. Optional future actions must be marked as proposed in design notes.

13. **Models and providers.** Under Settings, show provider route health, model/profile, circuit state, request/error/retry counts, validation issues, tool presets, autonomy/approval information, allowed-tool counts, and model-profile summary. Translate health into readable labels while preserving exact details. Keep setup selection separate from the existing inspection surface. Any new conversational route editor is a proposed capability. Do not invent live prices, spend totals, or guaranteed privacy/performance comparisons.

14. **Administration and notifications.** Preserve operator-token issuance with username/password/token label, current identity/role/authentication mode, status reload, deployment status, approval notifications, and notify-only-when-unfocused preference. Group these under Access, Runtime, and Notifications. A viewer sees appropriate read-only states and explanations. Generated tokens use masked demo values and are not copied into chat.

15. **Diagnostics.** Reimagine Runtime Events as an advanced Activity inspector with session, channel, sender, component, and action filters; readable event rows; and detailed payload inspection. Show exact diagnostic information only on demand. Provide an “Explain this error” chat handoff with sensitive fields removed.

16. **Experimental Payment Lab.** Keep this discoverable under Settings → Labs, visibly experimental and disabled for execution outside simulation. Preserve setup load, provider, test/live environment, funding sources, virtual-card issuance, payment execution, and payment-status lookup. Preserve merchant name/URL, amount, currency, funding-source ID, and payment ID where relevant. Let users enter readable currency amounts and expose conversion to minor units in technical detail. Show merchant, amount, currency, funding source, and environment in a deliberate confirmation step. Never expose full card credentials in chat. All prototype actions return clearly simulated outcomes; no real financial calls. Do not offer undo for an executed payment.

### Required clickable journeys

Build these as connected, stateful flows with realistic sample content:

- **First-time user:** welcome → “Set up my assistant” → online/local choice → guided credential or local-model setup → simulated verification → first conversation. Include failed verification and recovery.
- **Chat-to-GUI continuity:** “Help me change my model” → editable configuration card → Edit in settings → revise the same draft → review → apply → see the updated model in chat. Treat universal editing as proposed.
- **WhatsApp pairing:** Connections → WhatsApp → bridge mode → QR → simulated pairing event → connected status reflected in chat and overview. Also provide the distinct Cloud API setup path.
- **Workflow assistance:** choose a workflow → enter input → run timeline → answer a pending question → output opens in Canvas.
- **Automation:** inspect an existing automation → dry run → inspect results → confirm live run → history updates. Include a quarantined example and recovery flow. Provide a separate proposed “Create a daily briefing” chat demo.
- **Approval:** a tool request appears in chat and inbox → inspect impact → approve or deny → decision history updates → show the separate execution result if approved.
- **Troubleshooting:** provider unavailable → plain-language explanation → diagnostic details → guided correction → retry. Preserve the conversation draft.
- **Power user:** command palette → runtime settings → Advanced → inspect route and tool policy details → switch back to the uncluttered chat view.
- **Payment simulation:** Labs → test setup → choose funding source → enter payment → explicit review → simulated pending/success/failed status lookup.

For each major destination, include populated, empty, loading, error, disconnected, and permission-limited states where relevant. Give every visible primary action a meaningful behavior; secondary actions may open a truthful detail panel. Unsupported free-text requests should explain the demo's limits and suggest supported scenarios rather than fake completion.

### Components and output quality

Build reusable navigation items, conversation messages, composer, suggestion chips, configuration cards, masked credential cards, review summaries, status indicators, approval cards, list/detail panels, workflow timelines, filters, form rows, advanced accordions, confirmation dialogs, toasts, and empty/error states. Use consistent interaction states across light and dark themes.

Add an internal prototype-guide page with scenario shortcuts, a current-feature coverage checklist, proposed-capability notes, and component/token reference. Keep developer annotations out of ordinary user journeys. No lorem ipsum, disconnected mock screens, or decorative buttons without behavior.

The design should be straightforward to translate into Avalonia using grids, split panes, list controls, templates, and MVVM commands. Deliver the working prototype, not just a landing page or a static dashboard.

### Build sequence if the prototype is too large for one pass

First establish the shell, shared state, design tokens, onboarding, chat, configuration card, GUI handoff, and approval flow. Then add the remaining destinations and journeys using the same components. Finally verify feature coverage, failure states, keyboard access, dark theme, and compact layouts. Preserve previous working flows during each pass.

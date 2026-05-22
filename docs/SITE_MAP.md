# Documentation Site Map

Use this map when turning the Markdown docs into a documentation website. It keeps the first-run path short, separates operator material from contributor material, and gives every sidebar item a stable source file.

## Primary Navigation

| Section | Label | Source |
| --- | --- | --- |
| Overview | Overview | [README.md](../README.md) |
| Overview | Start Here | [START_HERE.md](START_HERE.md) |
| Overview | Quickstart | [QUICKSTART.md](QUICKSTART.md) |
| Overview | Getting Started | [GETTING_STARTED.md](GETTING_STARTED.md) |
| Guides | User Guide | [USER_GUIDE.md](USER_GUIDE.md) |
| Guides | Tools Guide | [TOOLS_GUIDE.md](TOOLS_GUIDE.md) |
| Guides | Embedded Local Models | [LOCAL_MODELS.md](LOCAL_MODELS.md) |
| Guides | External CLI Connectors | [EXTERNAL_CLI_CONNECTORS.md](EXTERNAL_CLI_CONNECTORS.md) |
| Guides | Model Profiles | [MODEL_PROFILES.md](MODEL_PROFILES.md) |
| Guides | Prompt Caching | [PROMPT_CACHING.md](PROMPT_CACHING.md) |
| Guides | Agent Testing Harness | [testing/agent-testing-harness.md](testing/agent-testing-harness.md) |
| Guides | AI-Assisted Testing Playbook | [testing/ai-assisted-testing-playbook.md](testing/ai-assisted-testing-playbook.md) |
| Guides | Harness Regression Suite | [HARNESS_REGRESSION.md](HARNESS_REGRESSION.md) |
| Guides | Plan-Execute-Verify Mode | [PLAN_EXECUTE_VERIFY.md](PLAN_EXECUTE_VERIFY.md) |
| Reference | Compatibility | [COMPATIBILITY.md](COMPATIBILITY.md) |
| Reference | Sessions | [SESSIONS.md](SESSIONS.md) |
| Reference | Canvas and A2UI | [CANVAS_A2UI.md](CANVAS_A2UI.md) |
| Reference | Glossary | [GLOSSARY.md](GLOSSARY.md) |
| Integrations | Semantic Kernel | [SEMANTIC_KERNEL.md](SEMANTIC_KERNEL.md) |
| Integrations | Microsoft Agent Framework | [integrations/microsoft-agent-framework.md](integrations/microsoft-agent-framework.md) |
| Integrations | Workflow Backends | [workflow-backends.md](workflow-backends.md) |
| Integrations | Microsoft Teams | [TEAMS_SETUP.md](TEAMS_SETUP.md) |
| Integrations | WhatsApp | [WHATSAPP_SETUP.md](WHATSAPP_SETUP.md) |
| Integrations | A2A | [a2a.md](a2a.md) |
| Integrations | External Coding Backends | [external-coding-backends.md](external-coding-backends.md) |
| Integrations | Tailscale Deployment | [deployment/TAILSCALE.md](deployment/TAILSCALE.md) |
| Operations | Security | [SECURITY.md](../SECURITY.md) |
| Operations | Payment Security | [security/payments.md](security/payments.md) |
| Operations | Tool Governance Sidecar | [governance/sidecar-pattern.md](governance/sidecar-pattern.md) |
| Operations | Microsoft Agent Governance | [governance/microsoft-agent-governance.md](governance/microsoft-agent-governance.md) |
| Operations | Releases | [RELEASES.md](RELEASES.md) |
| Operations | Docker Hub | [DOCKERHUB.md](DOCKERHUB.md) |
| Operations | Optional Sandboxing | [sandboxing.md](sandboxing.md) |
| Project | Contributing | [CONTRIBUTING.md](../CONTRIBUTING.md) |
| Project | Roadmap | [ROADMAP.md](ROADMAP.md) |
| Project | AI Contributor Guide | [ai-contributor-guide.md](ai-contributor-guide.md) |

## Suggested Landing Path

1. Start with [README.md](../README.md) for the product-level overview.
2. Send evaluators to [START_HERE.md](START_HERE.md) when they need the shortest technical orientation.
3. Send hands-on users to [QUICKSTART.md](QUICKSTART.md), then [GETTING_STARTED.md](GETTING_STARTED.md) if they need repository context.
4. Link operators to [SECURITY.md](../SECURITY.md), [COMPATIBILITY.md](COMPATIBILITY.md), and [RELEASES.md](RELEASES.md) before any public deployment.
5. Link contributors to [CONTRIBUTING.md](../CONTRIBUTING.md), [ROADMAP.md](ROADMAP.md), and [ai-contributor-guide.md](ai-contributor-guide.md).

## Sidebar Grouping

```text
Overview
  Overview
  Start Here
  Quickstart
  Getting Started

Guides
  User Guide
  Tools Guide
  Embedded Local Models
  External CLI Connectors
  Model Profiles
  Prompt Caching
  Agent Testing Harness
  AI-Assisted Testing Playbook
  Harness Regression Suite
  Plan-Execute-Verify Mode

Reference
  Compatibility
  Sessions
  Canvas and A2UI
  Glossary

Integrations
  Semantic Kernel
  Microsoft Agent Framework
  Workflow Backends
  Microsoft Teams
  WhatsApp
  A2A
  External Coding Backends
  Tailscale Deployment

Operations
  Security
  Payment Security
  Tool Governance Sidecar
  Microsoft Agent Governance
  Releases
  Docker Hub
  Optional Sandboxing

Project
  Contributing
  Roadmap
  AI Contributor Guide
```

## Build Notes

- Preserve the existing relative links when importing pages into a static site generator.
- Treat files under `docs/experiments/` as historical research notes, not primary navigation.
- Keep [docs/README.md](README.md) as the source-index page for GitHub browsing.
- Add redirects or aliases for lower-case route names if the website framework normalizes slugs.
- Keep security and contribution pages at the repository root in source control even if the website renders them under an operations or project section.

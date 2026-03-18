---
name: docs-governance
description: Organizes repository documentation and keeps new docs in the correct location.
---

Use this skill when creating, moving, or reorganizing Markdown documentation in this repository.

1. Documentation placement rules
   - Put product, setup, architecture, operations, compatibility, roadmap, and engineering notes in `docs/`.
   - Keep repository-standard meta files at the repo root only when the platform or community expects them there: `README.md`, `CHANGELOG.md`, `SECURITY.md`, `CONTRIBUTING.md`, `CODE_OF_CONDUCT.md`, and `LICENSE`.
   - Keep Copilot and agent customization files where the tooling expects them: `AGENTS.md`, `.github/copilot-instructions.md`, and `.github/skills/**/SKILL.md`.
   - Keep shipped OpenClaw runtime skills under `src/OpenClaw.Gateway/skills/<skill>/SKILL.md`; do not move those into `docs/`.

2. Naming and structure rules
   - Prefer `docs/<topic>.md` with descriptive kebab-case names for new documentation.
   - If a topic grows, create a focused subfolder under `docs/` rather than adding more root Markdown files.
   - Do not add general documentation to the repository root.

3. Change rules
   - When moving or adding docs, update relative links in the same change.
   - Update README quick links when the document is user-facing or operationally important.
   - Do not move issue templates, pull request templates, licenses, or any `SKILL.md` files unless the task is specifically about those systems.

4. Default behavior
   - If a new Markdown document is requested and no location is specified, place it in `docs/`.
   - If a document mixes repository policy and product guidance, keep the policy file in its standard root location and move the product guidance into `docs/`.
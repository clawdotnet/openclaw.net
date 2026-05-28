# OpenClaw SkillKit

OpenClaw SkillKit is a local-first, file-based authoring workflow for reusable OpenClaw skills. It helps users define clear intent, required inputs, output expectations, tool policy, guardrails, human approval points, and validation checks before an agent executes work.

SkillKit is local-first and file-based. It is not a hosted skill marketplace or governance dashboard. The goal is to help OpenClaw.NET users define reusable agent skills with clear intent, tool policies, guardrails, human approval points, and validation checks.

## What It Solves

SkillKit turns rough human goals into inspectable skill packages. It is useful for developer and non-developer tasks such as meeting summaries, donor proposal drafts, compliance review, government service request triage, research insight extraction, implementation plans, and Codex build prompts.

The first version is CLI-first and deterministic. It does not call an LLM during skill creation, critique, validation, packaging, or dry-run planning.

## Package Structure

By default, generated skills live under `.openclaw/skills/{skill-id}/`.

```text
.openclaw/skills/{skill-id}/
  skill.yaml
  intent.md
  expectations.md
  workflow.yaml
  tools.yaml
  guardrails.md
  validation.md
  examples.md
  trace.md
```

`skill.yaml` is the canonical machine-readable manifest. The Markdown and support YAML files make the skill easy for humans to review and edit.

## CLI Commands

```bash
openclaw skill new "Community Research Insight Extractor" --category research
openclaw skill list
openclaw skill validate community.research_insight
openclaw skill critique community.research_insight
openclaw skill generate community.research_insight
openclaw skill package community.research_insight
openclaw skill run community.research_insight --input transcript.md --dry-run
```

Use `--output <path>` to choose a skills root. The default is `.openclaw/skills`. Packages are written to `.openclaw/packages` unless `--package-output <path>` is supplied.

## Create A Research Skill

```bash
openclaw skill new "Community Research Insight Extractor" --category research --template research
openclaw skill validate community.research_insight
openclaw skill critique community.research_insight
```

The research template includes grounded output expectations, community-engaged research guardrails, forbidden external actions, and human review points for final recommendations, external publication, and named attribution.

## Create A Proposal Skill

```bash
openclaw skill new "Donor Proposal Concept Note Builder" --category proposal --template proposal
openclaw skill run donor.proposal_concept_note_builder --input project-brief.md --dry-run
```

The proposal template focuses on a concept summary, need statement, proposed activities, outcomes, risks, and review questions.

## Validation Rules

`openclaw skill validate` reports pass, warn, and fail checks. Errors return a non-zero exit code; warnings do not.

Validation checks include:

- required package files exist
- manifest ID matches the folder name or declared alias
- name, version, category, and intent outcome are present
- required inputs and outputs are defined
- allowed and forbidden tools do not overlap
- approval-required tools are not forbidden
- workflow has at least one step
- validation checks and guardrails are defined
- trace file exists

## Dry-Run Execution

`openclaw skill run` is a dry-run planner in this MVP. It reads the skill, validates input file presence, prints workflow steps, and shows tool and approval policy. It does not execute tools, call models, or mutate files.

Full runtime execution can be added later once SkillKit packages are connected to governed OpenClaw runtime flows.

## Current Limitations

- no LLM-powered skill generation or critique
- no web dashboard
- no hosted marketplace
- no database-backed registry
- no autonomous skill execution
- no distributed execution
- YAML parsing supports the SkillKit-generated subset rather than arbitrary YAML

## Future Direction

SkillKit is intentionally lightweight in OpenClaw.NET. It can grow toward AgentQi-style governed skill workflows by linking skill packages to Harness Contracts, Evidence Bundles, Governance Ledger entries, Harness Regression checks, and Plan-Execute-Verify runs.

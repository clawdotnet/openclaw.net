# Digital Employee Package Best Practices

This document summarizes the lessons learned from the refactor of examples/skills/customer-quality-data-engineer, with the goal of giving digital employee package authors a reusable, maintainable, and reliably consumable writing approach for OpenClaw.NET.

Scope:

- Digital employee packages based on the manifest.json + config/ + skills/ + ontology/ + evaluation.* structure
- Packages that include both standard Skills and MetaSkills
- Packages that need to be uploaded through the Gateway, injected at runtime, used for evaluation material generation, and reviewed for completeness

## 1. Clarify the real runtime constraints first

Before writing a package, use the OpenClaw.NET implementation as the source of truth rather than relying only on template intuition.

The most important constraints from this refactor are:

- The upload endpoint only recognizes fixed config file names: AGENTS.md, SOUL.md, IDENTITY.md, MEMORY.md
- These four files are placed at the workspace root and read by the system prompt
- When AGENTS.md or SOUL.md becomes too large, it can consume too much of the prompt budget
- MetaSkill routing stability depends on structured outputs from child Skills, not on polished prose

Principles:

- Read the runtime code before defining the package structure
- Read the actual field contracts before writing documentation
- Do not promise capabilities that the code does not actually support

## 2. The four config files must have single, clear responsibilities

The most common problem in digital employee packages is not missing files, but duplicated or drifting content across the four config files.

Recommended division of responsibilities:

- SOUL.md: the single source of truth for rules. Only include hard constraints, guardrails, downgrade policies, and non-negotiable business rules.
- IDENTITY.md: voice and style. Only define language, tone, forbidden wording, and output style.
- MEMORY.md: state model. Only define what to remember, when it may be released, and which fields must be preserved.
- AGENTS.md: orchestration contract. Only define entry points, child-skill chains, field-level input/output contracts, and gate-routing principles.

Avoid this:

- Repeating rule redlines in IDENTITY.md
- Repeating full business rules in MEMORY.md
- Duplicating SOUL.md guidance in AGENTS.md

Recommended approach:

- Centralize policy descriptions in SOUL.md
- Centralize state descriptions in MEMORY.md
- Centralize orchestration descriptions in AGENTS.md

## 3. Keep SOUL short, hard, and executable

SOUL.md is not a product overview and not a long SOP. It should behave like a runtime safety boundary file.

Recommended contents:

- Core mission
- Responsibility boundaries
- Absolute red lines
- Exception and fallback strategies
- Delivery gates

Recommended style:

- Every rule should directly influence behavior
- Less explanation, more constraint
- Avoid repeated background narrative

Examples of effective phrasing from this practice include:

- Do not alter the rules
- Do not skip review
- Do not leak secrets
- Do not drift (for example, the 29-column rule is the only current rule)

## 4. IDENTITY should only define how to speak

IDENTITY.md is easy to let drift into a second SOUL.md.

Keep it limited to:

- Default language
- Tone and style
- Forbidden vocabulary
- User-facing output habits

Example:

- Use Simplified Chinese
- Be professional and precise
- Be objective and restrained
- Avoid vague wording

If a line contains words such as must, must not, block, allow, or pass, it probably belongs in SOUL.md rather than IDENTITY.md.

## 5. MEMORY should only contain the minimum usable state model

MEMORY.md should not become an interface manual or database design document.

Recommended minimal structure:

- L1 Session Memory: state fields that must be preserved for the current batch
- L2 Product Memory: stable facts across batches
- L3 Knowledge Memory: reusable rules and experience
- Minimal delivery gate checklist

What is genuinely valuable at runtime:

- reviewStatus
- validationResults
- analysisResults
- anomalies
- Current template version and source file information

Do not stuff full TypeScript interfaces, long examples, or exhaustive field explanations into MEMORY.md. These add prompt overhead without improving runtime decision quality.

## 6. AGENTS should be written as field-level orchestration contracts

AGENTS.md should not stay at the level of “do A, then B” flowcharts.

Better writing style:

- Who is the default entry point
- How child skills are chained
- Which fields each gate reads
- How PASS / FAIL / NEEDS_INPUT is determined
- Which output fields become downstream inputs

The most important improvement in this refactor was not “adding the MetaSkill name,” but turning the collaboration contract into field-level structure:

- oqc-file-precheck: pass, message, missing, extra, order_errors
- oqc-lot-structure-check: pass, failed_lots, details, total_rows_check, function_lots, cosmetic_lots
- oqc-data-logic-check: pass, issue_count, issues, dppm_table
- oqc-report-generation: template_file, checked_file, generated_at, rules, function_lots, cosmetic_lots

Principles:

- Write fields before prose
- If structured fields and natural language conflict, the structured fields win

## 7. Make MetaSkill the default entry point and let child Skills own specific capabilities

When the business naturally follows a fixed multi-step pipeline, the recommended pattern is not to expose four Skills as flat entry points, but to use:

- One MetaSkill as the default entry point
- Multiple standard Skills as sub-steps

Recommended layering:

- MetaSkill: handles entry trigger, gate routing, blocking responses, and final responses
- Standard Skill: handles single business capabilities and does not manage global orchestration

The model used in this package is:

```text
oqc-metaskill
  -> oqc-file-precheck
  -> oqc-lot-structure-check
  -> oqc-data-logic-check
  -> oqc-report-generation
```

Benefits:

- Users only need to express intents such as “run the OQC full workflow” or “generate the OQC validation report”
- The runtime has a single default entry point
- Child Skills can be tested, maintained, and reused independently

## 8. MetaSkill triggers and descriptions must be user-intent words

The most common anti-pattern for MetaSkill is writing triggers using internal terminology.

Do not write things like:

- four skill
- four SKILL
- OQC MetaSkill

Better alternatives are phrases users would actually say:

- OQC Summary CSV Validation
- OQC shipment inspection data validation
- OQC one-click validation
- Run the OQC full workflow
- Generate an OQC validation report

The description should describe when to use the entry point, not the internal implementation.

## 9. Gates should not rely mainly on natural language; prefer structured fields first

If a MetaSkill gate depends mainly on reading the previous step’s natural-language output, then any small wording change in a child Skill can destabilize the whole DAG.

Recommended order:

1. Prefer structured fields first
2. Use natural-language results as a supplement
3. If they conflict, trust the structured fields

For example:

- precheck_gate should prefer pass
- lot_structure_gate should prefer failed_lots and total_rows_check.pass
- data_logic_gate should prefer issue_count, issues, and pass

This was one of the most important stability improvements in this refactor.

## 10. final_response and blocked_response should be fixed status receipts

The MetaSkill exit should not be written as a long summary. A better approach is a fixed structure:

- Current stage
- Result status
- Next step

Blocked responses should follow the same pattern:

- Blocked stage
- Block reason
- Next step

Benefits:

- This aligns with the IDENTITY.md style of being objective, restrained, and structured
- It is easier for operators and reviewers to assess quickly
- It is easier to compare across evals and regressions

## 11. manifest, README, describe, evaluation, and review must be updated together

The most hidden problem in digital employee packages is not a bad Skill, but outdated surrounding documentation that still reflects the old world.

Typical drift seen in this refactor included:

- manifest already switched to oqc-metaskill, but README still described four independent skills
- Rule wording had already changed to 29 columns, but evaluation and archived test cases still referenced 24 columns
- Credentials boundary rules had been added, but the review report still said “secret boundary missing”

Recommended synchronization points:

- manifest.json
- README.md
- describe.md
- evaluation.md
- evaluation/testcases.json
- testcases/evaluation-test-cases.json
- reports/package-completeness-review.md

Principles:

- If the entry skill changed, the main documentation must change too
- If the rule wording changed, the evaluation and test cases must change too
- If the security boundary changed, the review report must change too

## 12. Evaluation material should be denoised; do not treat long logs as policy

Files such as evaluation/testcases.json often get polluted with long tool outputs, old packaging logs, or early draft summaries. These make later review and automation harder.

Recommended approach:

- context.transcript should only keep key event summaries
- transcript_digest should only keep short traceability information
- Remove historical warnings that clearly no longer match the package state
- Keep the main test_cases content unchanged

Principle:

- Evaluation material should be test input, not a junk drawer of logs

## 13. Pay attention to uploader and directory structure constraints

Even if a package is written well, uploader constraints can still cause it to lose content at runtime.

Real risks that remained in this package included:

- ontology/hiring-session/ subdirectory
- ontology/projections/ subdirectory

If the uploader only accepts top-level ontology files, then even if these paths exist in the repository, they may not be installed into runtime.

Principle:

- Verify which paths and extensions the uploader supports first
- Either promote optional directories or explicitly document that they will not be installed

## 14. Recommended improvement order

When taking over an existing digital employee package, the recommended repair order is:

1. Fix manifest and the default entry point first
2. Fix SOUL.md as the single source of truth for rules
3. Separate the responsibilities of IDENTITY.md, MEMORY.md, and AGENTS.md clearly
4. Add MetaSkill and child Skill field-level contracts
5. Align README / describe / evaluation / review content
6. Finally handle uploader compatibility and optional directory issues

This prioritizes fixing what will actually break at runtime rather than spending time on superficial wording.

## 15. Final self-checklist

Before submitting, at minimum answer the following questions:

- Does manifest.json entry_skill actually point to the default entry point?
- Is SOUL.md the single authoritative rule source?
- Does IDENTITY.md only define expression style?
- Does MEMORY.md only preserve the minimum state model?
- Is AGENTS.md written as a field-level orchestration contract?
- Are MetaSkill triggers truly phrased as user would say them?
- Do gates prefer structured fields over natural language?
- Are final_response / blocked_response fixed status receipts?
- Do README / describe / evaluation / review all align with the current entry skill?
- Do ontology/skills/config paths match the uploader’s actual supported range?

If any of the above is unclear, the package is not yet fully settled.

## Reference cases

This best-practice guide is based directly on the following case:

- examples/skills/customer-quality-data-engineer/

It is also recommended to read:

- [Meta-Skill Authoring Guide](meta-skills.md)
- [Meta-Skills Overview](../meta-skills.md)
- examples/skills/customer-quality-data-engineer/config/
- examples/skills/customer-quality-data-engineer/skills/oqc-metaskill/SKILL.md

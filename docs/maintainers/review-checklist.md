# Maintainer Review Checklist

Use this checklist when reviewing pull requests. Not every item applies to every change, but reviewers should explicitly consider the relevant sections.

## Universal PR Review Checklist

- Is the change correct for the stated problem?
- Is the scope focused?
- Are docs and tests updated when behavior changes?
- Is a docs-only rationale clear when no tests are needed?
- Does the change preserve current default behavior unless the PR explicitly states otherwise?
- Are failure modes clear and actionable?
- Does the PR disclose whether it directly supports a company or customer use case?
- Have required checks completed successfully on the exact PR head or merge-queue commit?

## Runtime/Core PR Checklist

- Does this belong in core rather than an optional extension?
- Does it preserve stable runtime contracts?
- Does it avoid product-specific workflows?
- Does it keep diagnostics explicit?
- Does it preserve cancellation, streaming, sessions, memory, and tool execution semantics?
- Does it avoid increasing core dependency weight?

## Gateway PR Checklist

- Does this preserve local-first and self-hosted defaults?
- Does it preserve public-bind hardening?
- Does it keep auth, secret handling, channel signatures, and unsafe tools guarded?
- Does it fail closed when configuration is unsafe?
- Are health, admin, OpenAI-compatible, MCP, websocket, or diagnostics surfaces documented when changed?
- Are operator-facing errors actionable?

## Extension PR Checklist

- Does this belong in an extension rather than core?
- Is the extension boundary explicit?
- Does it avoid hidden provider or vendor defaults?
- Does it fail fast when unsupported?
- Does it avoid loading dynamic or JIT-only behavior in the AOT lane?
- Are setup and compatibility docs updated?
- If plugin loading, dependency resolution, or the compatibility catalog changed, does the pinned public compatibility smoke pass?

## Industrial PR Checklist

- Is this reusable infrastructure, adapter work, documentation, or example code?
- Does it avoid customer-specific factory logic?
- Does it avoid proprietary digital employee workflows?
- Does it avoid vendor-exclusive defaults?
- Is any company or customer-driven contribution disclosed?
- Does it preserve vendor neutrality?
- Does it fit the Industrial Pack boundary described in [../ARCHITECTURE_BOUNDARIES.md](../ARCHITECTURE_BOUNDARIES.md)?

## Documentation PR Checklist

- Is the doc in the right location?
- Are relative links correct?
- Does the doc distinguish supported behavior from proposals or experiments?
- Does it avoid overstating roadmap commitments?
- Does it keep setup steps current and concrete?
- Does it link to canonical docs instead of duplicating long instructions?
- Does it distinguish the latest release from behavior available only on `main`?

## Security-Sensitive PR Checklist

- Does this change authentication, authorization, secrets, public-bind behavior, approvals, tool execution, plugin loading, or network access?
- Does it fail closed?
- Are unsafe operations approval-gated?
- Are secrets redacted in logs, diagnostics, traces, and exports?
- Are user-controlled paths, URLs, headers, and payloads validated?
- Are new trust assumptions documented?
- Does this require core maintainer review?

## AOT Compatibility Checklist

- Does this preserve NativeAOT compatibility in the core path?
- Does it introduce reflection, dynamic loading, runtime code generation, or hidden JIT requirements?
- Does it use source-generated JSON serialization where needed?
- Are JIT-only surfaces isolated and documented?
- Does AOT fail fast before loading unsupported dynamic behavior?
- Does this increase trimming risk?

## Sponsorship/Commercial-Contributor Checklist

- Does the PR disclose when it directly supports a company or customer use case?
- Does the change preserve vendor neutrality?
- Does it avoid granting roadmap control, release authority, maintainer status, exclusive rights, or ownership of an ecosystem direction?
- Does it keep commercial or customer-specific logic out of core?
- Is the contribution intentionally submitted under the project license?

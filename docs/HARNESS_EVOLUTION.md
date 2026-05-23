# Harness Evolution Proposals

OpenClaw.NET can propose improvements to its own harness, but durable runtime behavior remains review-first.

A Harness Evolution Proposal is a `learning_proposal` with kind `harness_change`. It describes a possible change to policies, routing, memory retrieval, verification rules, context budgets, pulse behavior, tool governance, or related harness behavior. Operators approve or reject the proposal. Approval records acceptance for manual application; it does not silently mutate runtime configuration.

Harness Evolution Proposals are designed for:

- maintainers
- operators
- CI and release preparation
- review-first harness changes
- future Harness Evolution Proposal workflows
- future Plan-Execute-Verify and regression review loops

They help answer:

- What failed or could improve?
- What component is affected?
- What change is proposed?
- What evidence supports the change?
- What invariants must stay true?
- What regression checks should run?
- How would an operator roll back the change?

## Proposal Shape

The harness-specific body includes:

- `component`: `memory`, `retrieval`, `tools`, `approvals`, `verification`, `routing`, `prompt`, `model_profile`, `pulse`, `security`, `governance`, `context_budget`, `channel`, `sandbox`, or `unknown`
- `failureMode`
- `proposedChange`
- `predictedImprovement`
- `invariantsToPreserve`
- `falsificationTests`
- `evaluationPlan`
- `canaryPlan`
- `rollbackPlan`
- related Harness Contract, Evidence Bundle, Governance Ledger, regression report, runtime event, and session IDs
- `riskLevel`
- `applyMode`
- `requiresRegression`
- `regressionCategories`

Example:

```json
{
  "kind": "harness_change",
  "harnessEvolution": {
    "component": "memory",
    "failureMode": "Pulse runs include too much session history",
    "proposedChange": "Use compact memory export for pulse context",
    "predictedImprovement": "Lower token use and reduce context overload",
    "invariantsToPreserve": [
      "Do not include secrets",
      "Do not auto-write memory",
      "Keep durable changes review-first"
    ],
    "falsificationTests": [
      "harness regression: memory",
      "pulse test: compact context under budget"
    ],
    "rollbackPlan": "Disable compact pulse context mode",
    "applyMode": "manual_only",
    "requiresRegression": true,
    "regressionCategories": ["memory"]
  }
}
```

## Review Flow

Harness Evolution Proposals use the existing Learning Proposal review queue:

- `GET /admin/learning/proposals?kind=harness_change`
- `GET /admin/learning/proposals/{id}`
- `POST /admin/learning/proposals/{id}/approve`
- `POST /admin/learning/proposals/{id}/reject`

Additional operator endpoints:

- `POST /admin/learning/proposals/harness-change`
- `POST /admin/learning/proposals/harness-change/detect`

Creation and detection are gated by `Learning.HarnessEvolutionEnabled`. The default is `false`, so existing deployments do not expose harness-change proposal generation until an operator enables it. Listing and inspecting existing learning proposals remains available through the normal Learning Queue.

The detection endpoint is explicit. It scans recent warning/error runtime events, groups repeated signals, and creates review-first proposals only when an operator invokes it.

Approving a `manual_only` proposal:

- marks the learning proposal approved
- records a Governance Ledger decision
- links the ledger entry back to the proposal
- does not mutate config, memory, skills, tools, routing, approvals, or provider behavior

Rejecting a proposal records the rejection and linked Governance Ledger decision.

## Risk And Validation

Risk defaults are conservative:

- `security`: critical
- `approvals`, `tools`, `sandbox`, `governance`, `unknown`: high
- `memory`, `retrieval`, `verification`, `routing`, `prompt`, `model_profile`, `context_budget`, `channel`: medium
- `pulse`: low unless it affects notification or channel scope

Validation blocks missing components, missing proposed changes, unsupported apply modes, and auto-apply requests. Warnings surface missing rollback plans, missing falsification tests, vague predicted improvements, security/approval impact, and high-risk proposals without regression categories.

## Relationship To Other Harness Primitives

- Learning Proposals provide the review queue and approve/reject workflow.
- Harness Contracts describe intended governed work that may support a proposal.
- Evidence Bundles can link supporting evidence and untested areas.
- Governance Ledger records human review decisions.
- Harness Regression Suite supplies recommended checks before applying a proposal.
- Plan-Execute-Verify Mode can create contracts, evidence, and verification results that later support proposals.

## What This Does Not Do

- No silent self-modification by default.
- No automatic high-risk config mutation.
- No replacement for human review.
- No guarantee that every proposal is correct.
- No automatic regression run when a proposal is approved.
- No automatic rollback.

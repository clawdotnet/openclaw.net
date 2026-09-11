# Main Branch Protection

This page documents the recommended repository settings for protecting `main`. It is a governance reference, not a workflow implementation file.

## Goals

Branch protection should keep `main` stable without blocking focused contributor work.

Recommended goals:

- require pull requests before merging
- require CI validation before merging
- require review from the relevant CODEOWNERS entries
- keep direct pushes to `main` restricted
- prevent bypass except for explicitly trusted repository administrators
- preserve a clear audit trail for release, security, and runtime changes

## Recommended Settings For `main`

Configure a branch protection rule for `main` with:

- Require a pull request before merging.
- Require at least one approving review.
- Require review from Code Owners.
- Dismiss stale approvals when new commits are pushed.
- Require status checks to pass before merging.
- Require branches to be up to date before merging when practical for the queue.
- Restrict who can push to matching branches.
- Do not allow force pushes.
- Do not allow deletions.

If the repository uses a merge queue, apply the same required checks through the queue settings.

## Required Checks

Use the existing CI workflow as the primary required check. At minimum, `main` protection should require the build/test job that covers:

- solution restore
- Release build
- Release tests
- deterministic `samples/OpenClaw.HelloAgent` smoke run
- the pinned `public-compatibility-smoke` job

Required checks must complete on the exact PR head (or merge-queue commit) before merge. A queued or still-running check is not evidence that the reviewed revision passed. Do not merge through an administrator bypass while an exact-head required check is pending or red.

The latest-package compatibility canary is intentionally advisory because it tracks moving upstream releases. The pinned compatibility job is deterministic and should be required.

NativeAOT publish checks are valuable, but they can remain separate required or advisory checks depending on runtime reliability and release needs. If they become required, keep their failure modes documented in release or compatibility docs.

## CODEOWNERS Relationship

The repository uses [.github/CODEOWNERS](../../.github/CODEOWNERS) to route review requests for:

- runtime core
- gateway and security-sensitive surfaces
- security and governance docs
- build, release, and CI files
- industrial proposal and community documentation areas

Only add area maintainers after repo access, scope, and governance expectations are formalized.

## What This Does Not Grant

Branch protection and CODEOWNERS do not grant maintainer status by themselves.

Maintainer status remains governed by sustained contribution, trust, technical judgment, community conduct, and the role definitions in [maintainers.md](maintainers.md).

Sponsorship does not grant roadmap control, release authority, maintainer status, exclusive ownership, or CODEOWNERS authority.

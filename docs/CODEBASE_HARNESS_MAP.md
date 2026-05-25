# Codebase Harness Map

The Codebase Harness Map gives OpenClaw.NET a structured view of a repository as an agent harness environment.

It maps:

- solutions and projects
- modules
- endpoints
- tools
- providers
- channels
- config surfaces
- tests
- docs
- recent changes
- links to contracts, evidence, and shared harness state where available

Agents need more than file search. They need a bounded, inspectable map of the environment they are acting in before they plan work, delegate tasks, or verify changes.

## Relationship To Harness Primitives

- Harness Contracts describe intended work.
- Evidence Bundles record what happened.
- Shared Harness State tracks active multi-agent work.
- Fractal Memory stores durable project state.
- Codebase Harness Map describes the code environment.

## Commands

```bash
openclaw harness map
openclaw harness map --json
openclaw harness map --root ./src
openclaw harness map --category endpoints
openclaw harness map --output ./codebase-map.json
```

Categories:

- `all`
- `projects`
- `endpoints`
- `tools`
- `providers`
- `channels`
- `config`
- `tests`

## Admin API

Operators can request a map from the gateway:

```text
GET /admin/harness/codebase-map
GET /admin/harness/codebase-map?category=endpoints
GET /admin/harness/codebase-map?root=/path/under/workspace
```

The admin endpoint is workspace-root restricted. It rejects requested roots outside the configured workspace.

## Security Notes

- The scanner does not execute repository code.
- The admin endpoint restricts `root` to the configured workspace root.
- The scanner does not follow symlinked or reparse-point directories.
- Scanning is conservative and static.
- Recent-change tags are based on filesystem modification time in this MVP.
- Config values are not emitted.
- Keys whose names include `key`, `token`, `secret`, `password`, or `credential` are marked sensitive.
- Hashing is off by default and must be requested with `--include-hashes`.

## What This Does Not Do Yet

- It is not a full Roslyn semantic graph.
- It is not a full call graph.
- It is not a test coverage map.
- It is not a dynamic runtime trace graph.
- It is not an IDE replacement.
- It does not write Fractal Memory nodes directly.

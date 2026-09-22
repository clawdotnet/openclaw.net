# Full-instance offline backup and restore

> **Release availability:** Included in v0.3.0 and later; not included in v0.2.0. See the [roadmap](ROADMAP.md).

`openclaw backup` snapshots a declared inventory of instance directories, including file and SQLite state. It never starts a gateway, scheduler, plugin, model, or tool. Restoration always targets a new isolated directory and never replaces an existing deployment.

Stop the gateway, Companion-managed gateway, and all other writers before creating a backup. `--offline` acknowledges this prerequisite; the CLI cannot prove that arbitrary external processes have stopped. It rechecks the source inventory and hashes and rejects observed changes, but this is not a live transactional database backup.

Create a JSON plan outside the source directories:

```json
{
  "Roots": { "instance": "/srv/openclaw-instance", "memory": "/srv/openclaw-memory" },
  "Categories": {
    "configuration": "instance",
    "sessions": "memory",
    "goals": "memory",
    "schedules": "memory",
    "governance": "memory"
  },
  "SecretReferences": ["env:MODEL_API_KEY", "env:OPENCLAW_AUTH_TOKEN"]
}
```

The category declarations are an operator inventory, not automatic configuration discovery. Include every configured persistence directory: `Memory.StoragePath`, external SQLite/MemPalace paths, feature/automation stores, governance ledgers, managed skills, channel-worker state, and any state outside those roots. Goals, session checkpoints, action journals, and audit data under the memory root are included recursively. Configuration-defined cron schedules require the configuration root. Add other named roots as necessary; relative roots resolve against the plan file. Empty directories are not preserved except declared roots.

```sh
openclaw backup create backup-plan.json /backups/openclaw-20260910 --offline
openclaw backup validate /backups/openclaw-20260910
openclaw backup restore /backups/openclaw-20260910 /srv/openclaw-restore-review
```

The snapshot contains a versioned manifest, per-file SHA-256 digests, lengths, root mappings, and the explicit secret-reference list. Credential providers are never queried. Files in declared roots are copied verbatim: configuration with inline credentials, session histories, and channel-worker state remain sensitive. This format is not encrypted; secure the backup storage. Unix files/directories are created with 0600/0700 permissions. The source and destination must not traverse symlinks/reparse points; use canonical paths (for example `/private/tmp` rather than the macOS `/tmp` alias).

Validation rejects traversal, duplicate payload paths (case-distinct paths are supported; restore rejects collisions on case-insensitive destinations), unlisted payload files, unsupported manifests, and checksum mismatches. Limits are 100,000 files and 100 GiB. These checks detect corruption, not malicious replacement of both payload and manifest; store backups in a trusted location.

Restore checks copied bytes and SQLite `quick_check` for `.db`, `.sqlite`, and `.sqlite3` files in the isolated copy, then publishes the destination atomically. A failure removes staging data. Review `restore-manifest.json` and `RESTORE-REQUIRES-REVIEW.txt`, supply secret references through the destination's normal secret provider, and remap configuration paths before choosing to start an instance. Original absolute configuration paths are deliberately preserved and can still reference production. Nothing automatically activates restored schedules or actions; starting the gateway later is an explicit operational step. Binary/plugin compatibility and external-provider state need separate review.

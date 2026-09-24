# Audience controls, managed updates, local demo and device enrollment

## Audience controls

Audience controls are opt-in to preserve existing deployments. They apply to both
native and Microsoft Agent Framework runtime tool declarations **and dispatch**.
They narrow existing tool presets and routing permissions; a route cannot widen them.
Unknown audience names fail closed. No trust is inferred from a direct message.

Add this under `OpenClaw.Tooling` in the gateway configuration:

```json
{
  "Audiences": {
    "Enabled": true,
    "DefaultAudience": "public",
    "ChannelBindings": { "cli": "personal", "slack": "team" },
    "SessionBindings": { "your-exact-public-room-session-id": "public" },
    "Profiles": {
      "public": { "AllowedTools": [], "IncludePrivateContext": false, "AllowAttachments": false },
      "team": { "AllowedTools": ["web_search", "web_fetch"], "IncludePrivateContext": false, "AllowAttachments": false },
      "personal": { "AllowAllTools": true, "IncludePrivateContext": true, "AllowAttachments": true }
    }
  }
}
```

Session bindings take precedence over channel bindings. Use the session IDs shown
in the operator session list to distinguish rooms on the same transport. The
built-in public profile has no tools; team has web search/fetch; personal leaves
existing tool policy intact. MCP tools must be explicitly named in restrictive
profiles, using their registered tool names.

`IncludePrivateContext: false` excludes workspace prompt files, skill indexes,
route prompt overrides, automatic note/structured/profile recall from model
context. Conversation history remains local to its session. Policy changes require
a fresh session when history already exists; the old history is preserved rather
than silently redacted or deleted. Existing sessions created before enabling this
feature also require a fresh session. `AllowAttachments: false` rejects media
markers before a model turn. Tool allowlists should not include private-memory,
session-history, filesystem or delegation tools unless that audience is intended
to access them. Existing global filesystem roots remain enforced; these profiles
do not introduce a separate filesystem sandbox or per-audience path roots.

## Managed updates

The updater installs complete CLI/gateway/Companion ZIP bundles in
`~/.openclaw/updates/bundles`. Configuration, secrets and memory are not replaced.
`active.json` atomically selects a bundle; the previous managed bundle remains for rollback.
The first managed installation has no previous managed bundle. Your original
installation remains untouched and can still be launched using its original path.
An exclusive update lock prevents simultaneous installations. ZIP traversal,
symlinks, duplicate paths and oversized downloads/archives are rejected.

First obtain the publisher's RSA public key through an independently verified
channel. Keys must be at least 3072 bits. Then configure trust:

```sh
openclaw update trust --manifest https://publisher.example/update-manifest.json --key publisher.pem
openclaw update check --channel stable
openclaw update install --channel stable --version 1.2.3 --yes
openclaw update launch companion
openclaw update rollback --yes
```

All commands accept `--root <path>`. Use `--channel beta` for beta feed entries.
Explicit version pins must exist in the selected channel; no silent fallback.
The feed orders releases newest first. Manifests expire; expired feeds fail closed.
Automatic selection rejects an older numeric release than the active one; an
an explicit `--allow-downgrade` flag or rollback permits an intentional downgrade.

Installation verifies RSA/SHA-256 signatures on exact manifest bytes, then bundle
SHA-256 and size. It smoke-checks CLI `version` and gateway `--doctor` with isolated
configuration before activation. These are startup checks, not migration or live
provider acceptance tests. If a startup check fails, activation remains unchanged.
A failed first activation can leave a verified bundle on disk; no bundle is deleted
implicitly. Reinstalling an existing bundle fails rather than replacing its files.

On Unix, `~/.openclaw/updates/launch companion` (or `cli` / `gateway`) reads the
active pointer each time. Windows users can use `launch.ps1`. Existing shortcuts
and manually installed binaries still launch their original version; point them
at the managed launcher. Stop a manually operated old gateway before starting the
new one. The Companion Setup page includes trust, check, install, rollback and
restart actions. Restart stops only Companion's managed gateway and launches the
active Companion bundle. External gateway services remain operator-managed.
Rollback selects old binaries; it does not downgrade persisted data schemas.

Leave the version field empty to check for the latest release on the selected
channel. A successful check keeps that field empty and retains the verified
version separately for installation. Enter a version only to pin it explicitly;
changing the channel or pin requires a new check before installing.

### Publishing a signed feed

The release workflow signs `update-manifest.json` and its detached
`update-manifest.json.sig` when the GitHub secret `UPDATE_SIGNING_KEY` contains the
publisher RSA private PEM. **No publisher key is bundled or generated automatically.**
Without this secret, release archives still publish, but no verified-update feed
is produced. Distribute the matching public key independently.

Re-running publication for an existing tag refreshes assets and the prerelease
flag while preserving its current title and draft/published state.

The publisher can also run:

```sh
python3 eng/create-update-manifest.py --assets release-assets --version 1.2.3 \
  --channel stable --base-url https://github.com/OWNER/REPO/releases/download/v1.2.3 \
  --key /secure/publisher-private.pem
```

The generated feed contains that release and expires in 90 days. A stable GitHub
feed can use `/releases/latest/download/update-manifest.json`. For beta releases,
configure the prerelease's explicit manifest URL, or host a curated feed containing
multiple release/channel entries. Keep feeds refreshed and manage key rotation
through explicit trust reconfiguration. Manifest signing is separate from Windows
Authenticode and macOS notarization.

## Remote device enrollment

In Companion, open Setup → Advanced connection settings → Pair another device.
As an administrator, supply the target operator account ID and a device name,
then generate a code. On the new device, set the gateway URL, paste the code and
choose **Enroll this device**. The token goes through Companion's existing token
storage/settings flow. Connect afterward.

Codes are random 128-bit capabilities, expire after five minutes, are stored only
as hashes in process memory, and can be redeemed once. Restart invalidates pending
codes. Exchanges are globally limited to 30 attempts/minute with at most 100 pending
codes. Disabled/changed accounts invalidate outstanding codes. Device tokens
inherit the selected account role, expire after 30 days, and appear with a
`device:` label in existing operator-account token management, where they can be
revoked. Use a least-privilege account for the intended device.

API:

- `POST /auth/devices/enroll`: administrator auth and browser CSRF checks; JSON
  `{ "AccountId": "opacct_...", "DeviceName": "Laptop" }`.
- `POST /auth/devices/exchange`: JSON `{ "Code": "..." }`; returns the existing
  account-token response shape. No prior device credentials are required.

Remote enrollment requires HTTPS; direct loopback HTTP is permitted. A TLS
terminating proxy must be configured using the gateway's trusted forwarding setup.
Organization policy must permit account tokens. Neither codes nor token values
are included in enrollment audit records. Bootstrap/loopback administrators select
an existing named account; enrollment never creates an implicit administrator.

## Account-free local demo

```sh
python3 samples/OpenClaw.LocalDemo/run.py
```

See [demo prerequisites and lifecycle](../samples/OpenClaw.LocalDemo/README.md).
The runner uses a real local model, isolated gateway state and a synthetic project
note. It does not change existing gateway installations or require provider keys.

## Verification

The implementation was checked with 450 selected runtime, gateway, Companion,
Microsoft Agent Framework, audience, enrollment and updater tests. The live HTTP
enrollment smoke verified administrator-only issuance, single-use exchange,
account-role preservation and revocation. Packaged setup and a deterministic
Ollama-compatible tool round trip passed. The publisher script's detached signature
and bundle digest were verified with OpenSSL. Demo config generation and gateway
`--doctor` passed; the full real-model demo was not run because Docker was stopped.

To repeat the live enrollment check after a Debug build:

```sh
python3 eng/verify-device-enrollment.py
```

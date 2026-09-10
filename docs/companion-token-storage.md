# Companion token storage and legacy migration

Companion stores remembered gateway tokens and provider API keys through the operating system:

- macOS: Keychain.
- Windows: DPAPI scoped to the current user.
- Linux: Secret Service through `secret-tool`, when available.

Plaintext fallback remains disabled by default and requires the existing explicit setting. Gateway tokens are not written into new `settings.json` files.

## Existing profiles

When Remember token is enabled, loading a profile automatically attempts to move a legacy `authToken`/`AuthToken` field or `token.txt` fallback into protected storage. This also supports old PascalCase settings. Provider-key fallback files use the same migration logic when the stored key is loaded.

Migration reads the protected token back and compares it before removing any plaintext copy. JSON cleanup preserves unrelated settings, including unknown fields. Settings, fallback token, and DPAPI ciphertext writes use a temporary file, flush it, and replace the destination atomically; newly written files have owner-only read/write permissions on Unix. Windows DPAPI ciphertext writes also use atomic replacement.

If secure storage is unavailable, locked, fails verification, or contains a different credential, existing copies remain available for recovery and Companion displays a warning. Conflicting legacy fields are not guessed at or deleted. If settings change during migration, cleanup is deferred and retried on the next load.

**Compatibility change:** legacy plaintext fields now obey the plaintext fallback setting. If protected storage is unavailable and fallback is disabled, the legacy token remains on disk but is not used to authenticate. Unlock/enable the OS store and reload Companion, re-enter the token once protected storage is available, or explicitly enable plaintext fallback if that is your intended policy.

A failed secure save no longer deletes an existing fallback token. Failed provider-key replacements retain the previous key's marker. When credentials cannot be loaded, saving other settings will not silently strip the only legacy copy; Companion reports that the settings update was deferred.

## Remembering and forgetting

With Remember token off, Companion does not load or migrate a saved gateway token. Saving with Remember token off removes the legacy JSON field and attempts to clear both protected and fallback gateway-token storage. A failure to clear or verify storage is reported.

An empty token field with Remember token still enabled retains existing stored credentials and defers settings changes. This prevents a temporarily locked store from turning an unrelated settings save into credential deletion. Turn Remember token off and save to explicitly forget the gateway token. Provider API keys have their existing separate clear action.

## Limits

Migration does not rotate tokens, alter gateway accounts, or move credentials between profiles or machines. It does not change existing OS store names or account scoping. Protected storage availability depends on the operating system and an unlocked user session. A preserved conflicting plaintext copy requires operator review; it is never silently preferred over an existing protected credential.

A `token-update.pending` marker is written before replacing a remembered credential. It is cleared only after credential verification and the settings write succeed. If an update is interrupted or fails, Companion does not load saved gateway tokens until the intended token is re-entered and saved, or Remember token is turned off. This prevents pairing a changed credential with an old server URL. The marker contains no secret.

Atomic file replacement protects individual writes; settings files and OS credential stores are not a cross-store transaction. Use one Companion writer per profile. If the process stops between a verified protected save and JSON cleanup, the next load can finish cleanup without needing to recover a lost token.

Native migration has been smoke-tested on macOS. Windows DPAPI and Linux Secret Service behavior have deterministic fake-store coverage; native end-to-end migration on those platforms remains unverified.

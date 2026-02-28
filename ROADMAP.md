# Roadmap

## Security Hardening (Likely Breaking)

These are worthwhile changes, but they can break existing deployments or require new configuration.
Recommend implementing behind flags first, then enabling by default in a major release.

1. **Require auth on loopback for control/admin surfaces**
   - Scope: `/ws`, `/v1/*`, `/allowlists/*`, `/tools/approve`, `/webhooks/*`
   - Goal: reduce “local process / local browser” attack surface.

2. **Default allowlist semantics to `strict`**
   - Current: `legacy` makes empty allowlist behave as allow-all for some channels.
   - Target: `strict` should be the default for safer out-of-the-box behavior.

3. **Encrypt Companion token storage**
   - Store the auth token using OS-provided secure storage (Keychain/DPAPI/etc).
   - Include migration from existing plaintext settings.

4. **Default Telegram webhook signature validation to `true`**
   - Requires `WebhookSecretToken`/`WebhookSecretTokenRef` to be configured.
   - Improves default webhook authenticity guarantees.


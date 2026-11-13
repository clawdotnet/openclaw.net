# Payment Security

OpenClaw.NET implements payments as a native runtime/security capability, not as a standalone bridge plugin. The default posture is fail-closed:

- payments are disabled unless `Payments:Enabled=true`
- live money movement is denied unless policy and approval allow it
- payment secrets are stored behind opaque handles
- raw secrets never cross tool result, audit, log, trace, memory, or session boundaries

## Approval Model

Critical approval is required for live money-moving operations:

- live virtual card issuance
- machine payment execution
- browser checkout field filling through payment sentinels
- provider retry or confirmation flows that move money

The approval summary includes merchant, amount, currency, funding source display, provider, environment, and session identity when available.

Stripe Link commands use the gateway's configured `Payments:Environment`. Each request must authorize that same environment. A mismatch is rejected before the CLI is started. Live spend limits and approvals apply to both virtual-card issuance and machine payments.

Browser-session requests to the payment mutation endpoints require the session's `X-CSRF-Token`. Account and bootstrap bearer tokens retain their existing authentication flow.

Sentinel authorization takes its provider and environment from the stored payment handle. Caller-supplied test mode cannot downgrade a live handle. Concurrent retrieval of a one-time authorization secret has a single successful reader.

## Machine-payment retry safety

Machine payments require a stable idempotency key. Companion Payment Lab exposes this as a required payment reference. Reuse the same reference after an uncertain response, including after restarting Companion. The gateway scopes keys to authenticated caller, provider, environment, and workspace. It fingerprints every request field and rejects conflicting reuse. Query-string sender overrides do not change the authenticated caller's key scope. Current policy and approval checks still apply to retries.

Before dispatch, the gateway atomically reserves the key in `Memory:StoragePath/payment-idempotency`. Completed safe results are persisted using source-generated JSON and atomic file replacement. Processes sharing the same local storage directory coordinate through atomic file creation. Completed records are retained for seven days and removed by a best-effort hourly sweep; pending or unreadable records never expire automatically because their provider outcome may be uncertain. Keep the directory in backups and preserve it across upgrades. Separate gateways with separate storage do not share retry protection.

A reservation is retained after cancellation, timeout, process termination, or an unreadable record. This blocks duplicate dispatch when the provider outcome is uncertain. Reconciliation is an operator task against provider transaction records. This implementation does not claim provider-side exactly-once execution or automatically retry uncertain Stripe Link commands. The existing CLI adapter contract has no verified provider idempotency flag. Current upstream [Link CLI documentation](https://github.com/stripe/link-cli) describes a different command surface, so acceptance of the configured adapter remains deployment-specific.

Successful replay returns metadata only and does not restore an expired or consumed scoped authorization token. The HTTP-402 handler uses an explicit `PaymentAwareHttpHandler.IdempotencyKeyOption`, or derives a stable key from the request method, resource URL, structured challenge fields, and request-body hash. Challenges without an ID remain retry-safe under that deterministic key. Virtual-card issuance is outside the machine-payment idempotency contract.

Embedded runtimes default to a process-local store. Applications requiring restart protection must inject `FilePaymentIdempotencyStore`, or pass `idempotencyStoragePath` to `AddOpenClawPaymentCore`. No new dependencies or reflection-based serialization are introduced for AOT or JIT execution.

## Redaction

Payment redaction runs through the core redaction pipeline before persistence and export surfaces. It covers:

- Luhn-valid PAN candidates, including separated numbers
- CVV/CVC/security-code contexts
- `Authorization: Payment ...`
- shared payment token shapes
- obvious payment secret JSON fields

`PaymentSecret` has a throwing JSON converter so accidental serialization fails.

## Sentinel Boundary

Payment browser sentinels are safe for the model transcript:

```text
{{payment.vcard:<handleId>:pan}}
{{payment.vcard:<handleId>:cvv}}
{{payment.vcard:<handleId>:postal_code}}
```

Resolution is allowed only inside browser fill execution after critical approval. The executed browser payload receives the raw value, but persisted arguments keep the sentinel string. Resolved values are redacted if they appear in any returned text.

## Providers

The deterministic mock provider is for tests and local development. Stripe Link uses a safe `ProcessStartInfo.ArgumentList` runner, no shell execution, timeout/cancellation support, and redacted process output. If `link-cli` is absent, setup status reports `not_installed`.

Production secret backends are implemented as follows:

- HashiCorp Vault / OpenBao (KV v2 read): see `docs/security/vault.md`. Implementation in `OpenClaw.Security.Vault` using VaultSharp.
- Future: AWS Secrets Manager, Azure Key Vault, DPAPI (planned v2).

Future provider adapters can target x402, Ramp, Mercury, Payrica/mobile money, and other rails without changing the public tool boundary.

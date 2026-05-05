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

Production vault adapters are intentionally extension points for DPAPI, Azure Key Vault, HashiCorp Vault, and AWS Secrets Manager.

Future provider adapters can target x402, Ramp, Mercury, Payrica/mobile money, and other rails without changing the public tool boundary.

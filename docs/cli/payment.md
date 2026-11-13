# Payment CLI

Payment CLI commands talk to the running gateway so issued handles stay in the shared runtime vault.

```bash
openclaw payment setup [--provider <id>] [--json]
openclaw payment funding list [--provider <id>] [--test|--environment live] [--json]
openclaw payment virtual-card issue --merchant <name> --amount-minor <n> [--currency USD] [--funding-source <id>] [--test|--environment live] [--yes] [--json]
openclaw payment execute --idempotency-key <key> --amount-minor <n> [--merchant <name>] [--resource-url <url>] [--test|--environment live] [--yes] [--json]
openclaw payment status --id <payment-or-handle-id> [--provider <id>] [--json]
```

Money-moving commands default to `test`. Use `--environment live --yes` for live attempts; policy checks still run. Test mode can run deterministically when policy allows it. With Stripe Link, the requested environment must match the gateway configuration. A test request against a live-configured gateway fails before invoking the CLI.

CLI output is safe metadata only. It never prints raw PAN, CVV, authorization headers, shared payment tokens, or provider secret responses.

Machine payments require `--idempotency-key`. Choose one stable key per purchase, up to 200 characters, and reuse the same key and request fields for retries. The HTTP API uses the `idempotencyKey` request property, and the payment tool uses `idempotency_key`. Reusing a key for different payment details returns HTTP 409. Missing keys return HTTP 400.

Completed requests replay the saved result without invoking the provider again. A request that is still running, timed out, or lost its response returns `payment_outcome_unknown` on retry. Check the provider's transaction records and use `payment status --id <provider-payment-id>` when that ID is known. Do not change the key to force another attempt until the original outcome has been reconciled. Scoped authorization tokens remain one-time and are not replenished by replay.

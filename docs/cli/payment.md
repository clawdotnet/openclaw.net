# Payment CLI

Payment CLI commands talk to the running gateway so issued handles stay in the shared runtime vault.

```bash
openclaw payment setup [--provider <id>] [--json]
openclaw payment funding list [--provider <id>] [--test] [--json]
openclaw payment virtual-card issue --merchant <name> --amount-minor <n> [--currency USD] [--funding-source <id>] [--test|--environment live] [--yes] [--json]
openclaw payment execute --amount-minor <n> [--merchant <name>] [--resource-url <url>] [--test|--environment live] [--yes] [--json]
openclaw payment status --id <payment-or-handle-id> [--provider <id>] [--json]
```

Live money-moving commands require `--yes`; policy checks still run. Test mode can run deterministically when policy allows it.

CLI output is safe metadata only. It never prints raw PAN, CVV, authorization headers, shared payment tokens, or provider secret responses.

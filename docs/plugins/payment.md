# Payment Plugin

The bundled `payment` tool is a thin first-party tool over the native payment runtime. Payments are disabled by default:

```json
{
  "Payments": {
    "Enabled": true,
    "Provider": "mock",
    "Environment": "test"
  }
}
```

Agent actions:

- `setup_status`
- `list_funding_sources`
- `issue_virtual_card`
- `execute_machine_payment`
- `get_payment_status`

Tool results are allow-listed safe metadata only: provider, handle id, last4, merchant, timestamps, status, and provider references. The tool never returns PAN, CVV, cardholder details, authorization headers, shared payment tokens, provider secrets, or raw provider JSON.

Virtual card browser fills use sentinels such as:

```text
{{payment.vcard:<handleId>:pan}}
{{payment.vcard:<handleId>:cvv}}
{{payment.vcard:<handleId>:exp_mm_yyyy}}
```

The model may emit those strings, but raw values resolve only inside the approved browser fill boundary.

The mock provider is deterministic for local development and tests. The Stripe Link provider wraps `link-cli` when available and reports clean setup status when it is missing.

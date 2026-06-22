# 支付插件

内置的 `payment` 工具是基于原生支付运行时的一个轻量级第一方工具。支付功能默认禁用：

```json
{
  "Payments": {
    "Enabled": true,
    "Provider": "mock",
    "Environment": "test"
  }
}
```

Agent 操作：

- `setup_status` — 查询支付设置状态
- `list_funding_sources` — 列出资金来源
- `issue_virtual_card` — 签发虚拟卡
- `execute_machine_payment` — 执行机器支付
- `get_payment_status` — 获取支付状态

工具结果仅返回白名单中的安全元数据：provider、handle id、卡号后四位（last4）、商户、时间戳、状态和 provider 引用。工具绝不返回 PAN、CVV、持卡人详细信息、授权头部、共享支付令牌、provider 密钥或原始 provider JSON。

虚拟卡的浏览器填充使用哨兵（sentinel）模式：

```text
{{payment.vcard:<handleId>:pan}}
{{payment.vcard:<handleId>:cvv}}
{{payment.vcard:<handleId>:exp_mm_yyyy}}
```

模型可以输出这些字符串，但原始值仅在被批准的浏览器填充边界内解析。

Mock Provider 为本地开发和测试提供确定性的行为。Stripe Link Provider 在可用时封装 `link-cli`，在缺失时报告干净的设置状态。

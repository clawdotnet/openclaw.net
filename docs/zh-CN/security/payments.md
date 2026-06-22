# 支付安全

OpenClaw.NET 将支付实现为原生运行时/安全能力，而非独立的桥接插件。默认态势是故障关闭（fail-closed）：

- 除非设置 `Payments:Enabled=true`，否则支付功能禁用
- 除非策略和审批允许，否则拒绝真实资金移动
- 支付密钥存储在不透明句柄之后
- 原始密钥绝不跨越工具结果、审计、日志、追踪、记忆或会话边界

## 审批模型

真实的资金移动操作需要关键审批（critical approval）：

- 真实虚拟卡签发
- 机器支付执行
- 通过支付哨兵（sentinel）的浏览器结账字段填充
- 涉及资金移动的 Provider 重试或确认流程

审批摘要包含商户、金额、币种、资金来源显示名称、Provider、环境和会话标识（可用时）。

## 脱敏

支付脱敏通过核心脱敏管道在持久化和导出接口之前运行。覆盖内容包括：

- Luhn 校验合法的 PAN 候选值，包括分隔的数字
- CVV/CVC/安全码上下文
- `Authorization: Payment ...` 头部
- 共享支付令牌模式
- 明显的支付密钥 JSON 字段

`PaymentSecret` 具有一个会抛出异常的 JSON 转换器，确保意外序列化会失败。

## 哨兵边界

支付浏览器哨兵对模型对话记录是安全的：

```text
{{payment.vcard:<handleId>:pan}}
{{payment.vcard:<handleId>:cvv}}
{{payment.vcard:<handleId>:postal_code}}
```

解析仅在关键审批通过后的浏览器填充执行边界内被允许。执行的浏览器负载接收原始值，但持久化参数保留哨兵字符串。解析后的值如果出现在任何返回文本中会被脱敏。

## Provider

确定性的 Mock Provider 用于测试和本地开发。Stripe Link 使用安全的 `ProcessStartInfo.ArgumentList` 运行器，无 shell 执行，支持超时/取消，并对进程输出脱敏。如果 `link-cli` 缺失，设置状态报告 `not_installed`。

生产环境密钥库适配器是有意保留的扩展点，用于 DPAPI、Azure Key Vault、HashiCorp Vault 和 AWS Secrets Manager。

未来的 Provider 适配器可以面向 x402、Ramp、Mercury、Payrica/移动支付等支付渠道，而无需更改公共工具边界。

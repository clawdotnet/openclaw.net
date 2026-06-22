# Microsoft Agent Governance 集成

OpenClaw.NET 通过通用的 `IToolGovernanceService` 边界与 Microsoft Agent Governance 集成。默认实现是一个 AOT 安全的 HTTP sidecar 适配器，因此网关无需引用 Microsoft 预览包，也不将治理作为必需的运行时依赖。

Microsoft 公开参考资料目前展示了操作拦截和 .NET `GovernanceKernel.EvaluateToolCall` API。因此 OpenClaw.NET 的 sidecar 适配器保持端点可配置，而非将 `/api/v1/execute` 视为永久不变的 Microsoft 契约。

相关上游参考资料：

- [microsoft/agent-governance-toolkit](https://github.com/microsoft/agent-governance-toolkit)
- [Agent OS README](https://github.com/microsoft/agent-governance-toolkit/blob/main/packages/agent-os/README.md)
- [Quickstart](https://github.com/microsoft/agent-governance-toolkit/blob/main/QUICKSTART.md)
- [FAQ](https://github.com/microsoft/agent-governance-toolkit/blob/main/FAQ.md)

## 本地配置

```json
{
  "OpenClaw": {
    "Governance": {
      "Enabled": true,
      "Provider": "http_sidecar",
      "SidecarBaseUrl": "http://127.0.0.1:8088",
      "DecisionEndpoint": "/api/v1/execute",
      "TimeoutMs": 300,
      "AuditResults": true,
      "FailClosed": true,
      "RequireGovernanceForHighRiskTools": true
    }
  }
}
```

日常本地开发时保持治理功能禁用，除非你正在运行一个兼容的 sidecar。当治理启用时，启动阶段要求 `SidecarBaseUrl` 必须是一个绝对 URL。

## Kubernetes 模式

将 OpenClaw.NET 和治理 sidecar 运行在同一个 Pod 中，通过 localhost 通信。将策略挂载为 ConfigMap，使 sidecar 能够在无需外部策略服务的情况下执行策略。

示例清单位于：

```text
deploy/kubernetes/governance-sidecar/
```

## OWASP Agentic Top 10 映射

治理决策有助于缓解工具滥用、过度代理、不安全代码执行、数据泄露和审计不足等问题。不要声称完全覆盖 OWASP Agentic Top 10，除非策略集、测试和部署控制在目标环境中已完成映射和验证。

首次部署的推荐策略覆盖范围：

| 能力 | 建议默认值 |
| --- | --- |
| `process.execute` / `code.execute` | 要求审批或拒绝。 |
| `filesystem.write` | 要求审批、限定路径范围并审计。 |
| `external.http` / `data.export` | 对低信任 Agent 拒绝；审计所有访问。 |
| `message.send` / `email.send` | 要求审批，除非通道已严格限定范围。 |
| 支付和家庭自动化写操作 | 要求审批并故障关闭（fail closed）。 |

## 未来的直接适配器

未来的可选包（如 `OpenClaw.Governance.Microsoft`）可以直接封装 Microsoft .NET `GovernanceKernel`。该包应保持可选，且不得被默认的 NativeAOT 网关目标引用。

# 工具治理 Sidecar 模式

OpenClaw.NET 在一个中心节点执行可选的工具治理：`OpenClawToolExecutor`。已注册且被预设允许的工具调用在执行前会经过评估——在审批、哨兵替换、沙箱路由或实际执行之前。未知工具和被预设阻止的工具将在本地直接失败，不会调用治理 sidecar。

## 运行时流程

```text
client
  -> OpenClaw.NET gateway / agent runtime
  -> OpenClawToolExecutor
  -> IToolGovernanceService
  -> 可选 HTTP sidecar 决策端点
  -> 现有审批 / hooks / 沙箱 / 执行路径
  -> 可选 sidecar 结果审计端点
```

治理默认禁用。启用后，运行时仅向 sidecar 发送脱敏后的工具参数和工具元数据。sidecar 的决策会附加到存储的 `ToolInvocation`、工具审计条目、活动标签和日志中。

## 配置

```json
{
  "OpenClaw": {
    "Governance": {
      "Enabled": true,
      "Provider": "http_sidecar",
      "SidecarBaseUrl": "http://127.0.0.1:8088",
      "DecisionEndpoint": "/api/v1/execute",
      "ResultEndpoint": "/api/v1/result",
      "TimeoutMs": 300,
      "AuditResults": true,
      "FailClosed": true,
      "FailOpenReadOnlyLowRisk": false,
      "RequireGovernanceForHighRiskTools": true
    }
  }
}
```

`DecisionEndpoint` 默认为 `/api/v1/execute`，但这是适配器设置而非 OpenClaw.NET 的硬性契约。如果部署的 sidecar 使用其他路由或负载格式，可修改此项。

## 决策行为

支持的操作：

| 操作 | 运行时行为 |
| --- | --- |
| `allow` | 继续走正常的 OpenClaw.NET 工具执行路径。 |
| `deny` | 返回被阻止的工具结果；工具不执行。 |
| `require_approval` | 使用现有的 OpenClaw.NET 审批回调。如果没有附加可审批的交互界面，则拒绝执行。 |
| `redact` | 记录脱敏的治理元数据。仅当 sidecar 返回显式的替换参数时，执行参数才会改变。 |
| `audit_only` | 继续执行，并将治理决策附加到审计/追踪数据中。 |

Sidecar 故障和超时默认采用故障关闭（fail closed）策略。当 `RequireGovernanceForHighRiskTools` 启用时，高风险、可写、数据导出、网络写入、shell、进程和代码执行类工具采用故障关闭。只读低风险工具仅在 `FailOpenReadOnlyLowRisk` 显式启用时才会故障开放（fail open）。

## 工具元数据

OpenClaw.NET 为内置和原生工具名称维护一个中心化的描述符目录。描述符包含类别、能力、风险级别、审批提示、文件系统/网络/代码执行标志以及数据导出标志。这些元数据会发送给 sidecar，使策略能够基于能力进行推理，而不必硬编码每个工具名称。

不在目录中的插件和动态工具会收到保守的兜底描述符：中等风险、非只读、能力标记为 `plugin.invoke`。

## 审计字段

工具调用和审计条目包含以下字段：

- `GovernanceAllowed`
- `GovernanceAction`
- `GovernanceReason`
- `GovernancePolicyId`
- `GovernanceRuleId`
- `GovernanceTrustScore`
- `GovernanceEvaluationMs`

OpenClaw.NET 不会将原始工具结果发送到 sidecar 的结果端点。结果审计负载包含状态、失败码/消息、耗时、超时/失败标志以及结果字节数。

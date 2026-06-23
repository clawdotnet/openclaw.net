# Runtime Pulse

定时心跳回合，用于提示需要关注的事项，而不创建后台任务记录。

Runtime Pulse 给 OpenClaw.NET 一种安静的方式来检查是否有任何事项需要关注。

Pulse 是一个定时的 Agent 回合。它可以读取一个小的 `HEARTBEAT.md` 检查清单，检查轻量级运行时上下文，显示紧急告警，并推动"先审查"的学习系统创建提案。如果没有任何事项需要关注，Agent 回复 `HEARTBEAT_OK`，OpenClaw.NET 默认抑制该消息。

Pulse 不同于 cron 或自动化。Cron 创建分离的定时工作。Pulse 作为主会话或配置的会话唤醒运行。它用于意识、签入、维护信号和可审查的建议，而不是静默启动后台任务。

## 默认行为

- 启用时每 30 分钟运行一次。
- 如果存在，从工作区根目录读取 `HEARTBEAT.md`。
- 当结果为 `HEARTBEAT_OK` 时不发送可见消息。
- 默认保持告警对运维可见，`Target=none`。
- 可以限制在活动时间。
- 可以使用轻量上下文和隔离会话以减少令牌成本。
- 当其他运行时通道繁忙时推迟。
- 持久化学习更改保持"先审查"。

## 配置示例

```json
{
  "OpenClaw": {
    "Pulse": {
      "Enabled": true,
      "Every": "30m",
      "Target": "none",
      "DirectPolicy": "allow",
      "LightContext": true,
      "IsolatedSession": true,
      "SkipWhenBusy": true,
      "Prompt": "Read HEARTBEAT.md if it exists in the workspace context. Follow it strictly. Do not infer or repeat old tasks from prior chats. If nothing needs attention, reply HEARTBEAT_OK.",
      "AckMaxChars": 300
    }
  }
}
```

将 `Every` 设置为 `0m` 以禁用定时的 Pulse 运行。

## HEARTBEAT.md

在工作区根目录创建 `HEARTBEAT.md`：

```markdown
# Heartbeat checklist

- Check whether any learning proposals need review.
- Check whether recent sessions produced repeated failures.
- Check whether a pending automation or skill draft is blocked.
- If nothing needs attention, reply HEARTBEAT_OK.

tasks:
- name: proposal-review
  interval: 2h
  prompt: "Check pending learning proposals and surface only high-risk or stale items."
- name: runtime-health
  interval: 1h
  prompt: "Check recent runtime warnings and provider/tool failures."
```

保持心跳提示简短。不要在 `HEARTBEAT.md` 中放入密钥；它是提示上下文。

## 响应合约

- `HEARTBEAT_OK` 单独出现被视为 OK 且默认抑制。
- 仅 `HEARTBEAT_OK` 的精确匹配（去除首尾空格后）会抑制交付。
  以 `HEARTBEAT_OK` 开头或结尾但包含其他文字的回复不会被抑制。
- `HEARTBEAT_OK` 在较长回复的中间不会被特殊处理。
- 告警不应包含 `HEARTBEAT_OK`。

## 交付和可见性

`Target=none` 在没有外部交付的情况下运行 Pulse，并为运维人员记录告警。`ShowOk=false`、`ShowAlerts=true` 和 `UseIndicator=true` 是低噪音默认值。

如果 `ShowOk`、`ShowAlerts` 和 `UseIndicator` 都是 false，OpenClaw.NET 跳过模型调用。

对内部检查使用 `target=none`。对面向人类的通知使用活动时间。在群组频道中保持 `IncludeReasoning` 禁用。

第一个 Runtime Pulse 切片保持告警对运维可见。`target=last` 和显式频道 id 的直接交付保留给频道路由的后续版本，因此 Pulse 不能在路由策略完成之前通过发送外部消息让运维感到意外。

## 手动唤醒

```bash
openclaw pulse status
openclaw pulse run --text "Check for urgent follow-ups"
openclaw pulse run --text "Check the release checklist" --mode next-heartbeat
openclaw pulse events
```

`openclaw heartbeat run` 作为熟悉心跳术语的用户的别名被接受。

## 学习循环

Runtime Pulse 可能呈现待处理或过期的学习提案，并建议配置文件更新、自动化建议、技能草稿或未来的心跳检查清单更新。它不会自动批准或静默应用持久化学习更改。

## 故障排除

- `disabled`：`OpenClaw:Pulse:Enabled=false` 或 `Every=0m`。
- `outside-active-hours`：当前本地时间在配置的活动时间之外。
- `busy`：`SkipWhenBusy=true` 且另一个运行时通道处于活动状态。
- `empty-heartbeat-file`：`HEARTBEAT.md` 存在但没有可操作的文本或到期任务。
- `visibility-disabled`：所有可见性控制都是 false。
- `model-unavailable`：配置的模型/提供商路由不可用。

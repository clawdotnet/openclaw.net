# MetaSkill 调用 API

`POST /api/integration/meta-invocations` 会在现有会话中直接执行明确指定的 MetaSkill DAG。它不会提交聊天消息，也不会启动外部 workflow。

## 身份验证

使用 Gateway 标准 operator 身份验证，并确保调用方具有 `integration.mutate` 权限。Bearer operator token 通过 `Authorization` header 传入。使用浏览器会话时，还必须发送登录时返回的 `X-CSRF-Token` header。endpoint 会先执行现有的 operator 授权和速率限制检查，然后才读取幂等键或请求 body。

调用方必须有权写入请求中的现有会话。系统会在查找或声明幂等键之前检查会话所有权，因此被拒绝的请求不会占用该键。

## 请求

发送唯一的 `Idempotency-Key` header（最长 200 个字符），并在 JSON body 中提供以下三个字段：

```http
POST /api/integration/meta-invocations HTTP/1.1
Authorization: Bearer <operator-token>
Idempotency-Key: incident-2026-10-02-run-1
Content-Type: application/json

{
  "skill": "incident-review",
  "input": "Review incident INC-123 and identify the next action.",
  "sessionId": "incident-123"
}
```

`skill` 和 `sessionId` 不能为空。`input` 可以是字符串或 `null`。幂等键按已认证的调用方隔离，不同调用方可以独立使用相同的键。使用相同的 `skill`、`input` 和 `sessionId` 重试同一个键，会返回原调用状态或已完成结果；同一个键对应不同请求时会返回冲突。

## 响应

| HTTP 状态 | 含义 |
| --- | --- |
| `200 OK` | 调用已完成。重放请求会返回持久化的结果。 |
| `202 Accepted` | 相同请求仍在执行中。响应包含原始 `invocationId` 和 `Running` 状态；使用相同的键和请求重试即可查询状态。 |
| `409 Conflict` | 幂等键已用于不同请求，或调用状态为 `Uncertain`。键冲突返回错误对象；不确定调用会返回其 `invocationId`、`Uncertain` 状态和错误信息。 |

已完成响应示例：

```json
{
  "invocationId": "3a1d3589-2856-4f6b-b572-9f9e060db134",
  "status": "Completed",
  "result": "The next action is to rotate the affected credential.",
  "createdAtUtc": "2026-10-02T12:34:56Z"
}
```

JSON 无效或缺少必填字段时返回 `400 Bad Request`；会话不存在时返回 `404 Not Found`；调用方无权访问会话时返回 `403 Forbidden`。

## 幂等与恢复

调用声明和终态结果保存在已配置的 Gateway `Memory.StoragePath` 下。`Gateway.MetaInvocations.RetentionDays` 控制已完成和不确定记录的保留时间，默认值为 `30` 天。请将该值设为不短于任何调用方的最长重试期限，包括 DrasiWake outbox 的最长重试期限，避免重试发生时去重记录已过期。

如果执行失败、被取消，或进程在持久化终态之前停止，该调用会标记为 `Uncertain`。系统不会自动重试不确定的调用；调用方或运维人员需要先协调确认执行结果，再决定如何继续。使用相同键重试会返回已保存的不确定状态。

该 API 提供持久化去重和结果重放，但不保证在任意进程、机器或外部副作用故障下实现 exactly-once。对于同一个逻辑请求，调用方应在每次重试时保留相同的键；只有确实要发起新的调用时才使用新键。
# WhatsApp 第一方集成设置

OpenClaw 通过两个驱动引擎支持直接 WhatsApp 连接：**Baileys**（Node.js）和 **whatsmeow**（Go）。两者提供相同的功能——文本和媒体消息、群组、表情反应、已读回执、输入状态指示器和 QR/配对码认证——通过不同的底层库。

## 先决条件

| 驱动 | 运行时要求 | 说明 |
|--------|-----------------|-------|
| `baileys` | Node.js 18+ | 使用 [@whiskeysockets/baileys](https://github.com/WhiskeySockets/Baileys)，一个非官方 WhatsApp Web 库 |
| `whatsmeow` | 预构建二进制文件或 Go 1.21+ | 使用 [whatsmeow](https://github.com/tulir/whatsmeow)，一个 Go WhatsApp Web 库 |
| `simulated` | .NET 10 | 仅测试驱动——记录操作而不连接到 WhatsApp |

## 快速开始

```bash
# 自动设置——检测运行时，安装依赖，构建二进制文件
scripts/setup-whatsapp.sh
```

或手动：

```bash
# Baileys (Node.js)
cd src/whatsapp-baileys-worker
npm install

# whatsmeow (Go)
cd src/whatsapp-whatsmeow-worker
go build -o whatsapp-whatsmeow-worker .
```

## 配置

添加到 `appsettings.json`：

```json
{
  "Channels": {
    "WhatsApp": {
      "Enabled": true,
      "Type": "first_party_worker",
      "FirstPartyWorker": {
        "Driver": "baileys",
        "StoragePath": "./memory/whatsapp-worker",
        "Accounts": [
          {
            "AccountId": "default",
            "SessionPath": "./session/default",
            "PairingMode": "qr",
            "DeviceName": "OpenClaw"
          }
        ]
      }
    }
  }
}
```

### 配置参考

#### `FirstPartyWorker`

| 字段 | 默认值 | 描述 |
|-------|---------|-------------|
| `Driver` | `"baileys_csharp"` | 引擎：`"baileys"`、`"whatsmeow"`、`"simulated"` 或 `"baileys_csharp"` |
| `ExecutablePath` | 自动检测 | Worker 脚本/二进制文件的显式路径 |
| `WorkingDirectory` | 自动 | Worker 进程的工作目录 |
| `StoragePath` | `"./memory/whatsapp-worker"` | 会话、媒体和缓存文件的根目录 |
| `MediaCachePath` | `{StoragePath}/media-cache` | 下载的媒体文件缓存位置 |
| `HistorySync` | `true` | 首次连接时启用 WhatsApp 消息历史同步 |
| `Proxy` | 无 | WhatsApp 连接的 HTTP 代理 URL |
| `Accounts` | `[]` | WhatsApp 账户配置列表 |

#### `Accounts[]`

| 字段 | 默认值 | 描述 |
|-------|---------|-------------|
| `AccountId` | `"default"` | 此账户的唯一标识符 |
| `SessionPath` | `"./session/default"` | 会话凭据存储位置 |
| `DeviceName` | `"OpenClaw"` | 在 WhatsApp 已链接设备中显示的设备名称 |
| `PairingMode` | `"qr"` | `"qr"` 用于 QR 码扫描，`"pairing_code"` 用于 8 位代码 |
| `PhoneNumber` | 无 | `pairing_code` 模式必需（E.164 格式，例如 `"15551234567"`） |
| `SendReadReceipts` | `true` | 自动将入站消息标记为已读 |
| `AckReaction` | `false` | 当消息被接受时发送表情反应 |
| `MediaCachePath` | 继承 | 每个账户的媒体缓存覆盖 |
| `HistorySync` | `true` | 每个账户的历史同步覆盖 |
| `Proxy` | 继承 | 每个账户的代理覆盖 |

### 代理凭据

不要直接在 URI 中写入代理用户名/密码。将完整代理 URL 存入 OpenBao 或环境变量，并在 `Proxy` 中设置其引用：

```json
{
  "Proxy": "vault:secret/data/openclaw/prod/channels/whatsapp#proxy_url"
}
```

`FirstPartyWorker.Proxy` 和账户级 `Accounts[].Proxy` 均使用相同格式；引用解析出的值必须是完整代理 URL。密钥预热后，引用会在 worker 边界解析。公网绑定会拒绝代理 URI userinfo 以及其他内嵌凭据；`raw:` 引用需要显式启用公网兼容选项。

本地旧版 `admin-settings.json` 中的 WhatsApp 内嵌凭据仍可加载，但警告仅包含字段路径。新持久化快照、设置/setup 响应、agent bundle 导出和审计记录均不包含 WhatsApp 内嵌凭据。在 WhatsApp setup 更新 API 中，`null` 代理值会保留该账户当前值，空字符串则会清除它。

## 认证流程

### QR 码（默认）

1. 使用 `"PairingMode": "qr"` 启动网关
2. Worker 发出带有 QR 数据的 `qr_code` 认证事件
3. 查看 QR 码：
   - **管理界面**：导航到 WhatsApp 设置页面
   - **Companion 应用**：QR 自动显示
   - **API**：`GET /admin/channels/whatsapp/auth-status`
   - **SSE 流**：`GET /admin/channels/{channelId}/auth/stream`
4. 用你手机上的 WhatsApp 扫描 QR 码（设置 > 已链接设备 > 链接设备）
5. Worker 发出带有账户 JID 的 `connected` 事件

### 配对码（无头模式）

对于没有显示 QR 码方式的无头服务器：

1. 设置 `"PairingMode": "pairing_code"` 和 `"PhoneNumber": "15551234567"`
2. 启动网关
3. Worker 从 WhatsApp 请求配对码并发出 `pairing_code` 认证事件
4. 在你的手机上，前往设置 > 已链接设备 > 链接设备 > 使用手机号链接
5. 输入管理界面或 API 响应中显示的 8 位代码

### 会话持久化

凭据存储在 `SessionPath` 并跨重启持久化。每个账户只需配对一次。如果会话过期或你从手机注销，需要重新配对。

## 多账户

在 `Accounts` 数组中配置多个条目：

```json
{
  "Accounts": [
    {
      "AccountId": "personal",
      "SessionPath": "./session/personal",
      "PairingMode": "qr"
    },
    {
      "AccountId": "business",
      "SessionPath": "./session/business",
      "PairingMode": "pairing_code",
      "PhoneNumber": "15559876543"
    }
  ]
}
```

每个账户独立连接，并有自己的 QR/配对流程。入站消息从所有账户路由到网关管道。出站消息根据接收者路由到适当的账户。

## 媒体处理

两个驱动都支持入站和出站媒体：

**入站**：图像、视频、音频（包括语音备注）、文档和贴纸被下载到 `MediaCachePath`，并作为消息文本中的 `[IMAGE_URL:file://...]` 标记传递给网关。

**出站**：网关为每个出站消息发送 `BridgeMediaAttachment` 数组。Worker 从提供的 URL 下载媒体并通过 WhatsApp 发送。支持的类型：image、video、audio（作为 PTT 语音备注发送）、document、sticker。

## 驱动对比

| 功能 | Baileys (Node.js) | whatsmeow (Go) |
|---------|-------------------|----------------|
| 语言 | JavaScript/TypeScript | Go |
| 库 | @whiskeysockets/baileys | go.mau.fi/whatsmeow |
| 会话存储 | 多文件认证状态（JSON 文件） | SQLite 数据库 |
| 成熟度 | 广泛使用，社区维护 | 生产级，被 Matrix 桥接使用 |
| 协议稳定性 | 可能在 WhatsApp 更新时损坏 | 通常更稳定 |
| 内存使用 | 较高（Node.js 开销） | 较低（原生二进制文件） |
| 构建步骤 | `npm install` | `go build` |

**建议**：生产部署使用 **whatsmeow**（更稳定，资源使用更低）。如果你已经在运行 Node.js 或更喜欢 JavaScript 生态，使用 **Baileys**。

## 故障排除

### Worker 未找到

```
First-party WhatsApp worker executable was not found.
```

运行 `scripts/setup-whatsapp.sh` 或在配置中显式设置 `ExecutablePath`。

### Node.js 未找到（Baileys）

```
Node.js is required for the Baileys WhatsApp driver but was not found.
```

从 https://nodejs.org/ 安装 Node.js 18+ 或改为使用 whatsmeow 驱动。

### 依赖未安装（Baileys）

```
Baileys worker dependencies not installed.
```

在 `src/whatsapp-baileys-worker/` 中运行 `npm install`。

### 连接持续断开

WhatsApp 可能因各种原因断开连接：
- 检查你的互联网连接
- 确保你的手机已打开 WhatsApp 并已连接
- 如果使用代理，验证代理可达
- 检查网关日志中的断开原因代码

### 会话损坏

如果在先前成功连接后认证失败：
1. 停止网关

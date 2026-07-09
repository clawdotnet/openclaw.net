# 工作区管理 API

OpenClaw.NET 在 `/admin/workspace/*` 下暴露一组管理端点，用于工作区文件管理和 MCP 服务器配置。这些 API 专为操作员工作流设计，与 MCP App 宿主/代理接口分开。

## 端点

### 工作区文件

| 方法 | 端点 | 用途 |
|--------|----------|---------|
| `GET` | `/admin/workspace/browse?path=<相对路径>` | 扁平目录列表，含文件元数据 |
| `GET` | `/admin/workspace/tree?path=<相对路径>&depth=<最大深度>` | 递归目录树（默认深度：6） |
| `POST` | `/admin/workspace/upload?dir=<相对路径>` | 上传文件（multipart）或解压 ZIP 压缩包 |
| `GET` | `/admin/workspace/download?path=<相对路径>` | 下载单个文件 |

#### Browse 响应

```json
{
  "success": true,
  "files": [
    { "name": "readme.md", "path": "docs/readme.md", "isDirectory": false, "size": 2048 }
  ]
}
```

#### Tree 响应

```json
{
  "success": true,
  "root": "docs",
  "entries": [
    {
      "name": "docs",
      "path": "docs",
      "isDir": true,
      "children": [
        { "name": "readme.md", "path": "docs/readme.md", "isDir": false, "size": 2048 }
      ]
    }
  ]
}
```

#### 上传

- **单个 ZIP 文件**：解压到目标目录，带 ZIP-slip 防护
- **多个普通文件**：直接保存到目标目录
- 最大文件大小：可通过 `MaxUploadBytes` 配置

### 工作区 MCP

| 方法 | 端点 | 用途 |
|--------|----------|---------|
| `GET` | `/admin/workspace/mcp` | 获取当前工作区 MCP 服务器配置 |
| `PUT` | `/admin/workspace/mcp` | 持久化并热重载 MCP 服务器配置 |

此接口用于普通 `Plugins:Mcp` 服务器定义。当通过 `PUT` 更新配置时，网关无需重启即可热重载实时 MCP 工具面。这与 MCP Apps **分开**——基于清单发现的 MCP App 流程参见 [MCPAPP.md](MCPAPP.md)。

### 媒体

| 方法 | 端点 | 用途 |
|--------|----------|---------|
| `POST` | `/admin/media/upload` | 上传频道使用的媒体文件 |
| `GET` | `/admin/media/{id}` | 提供已上传的媒体文件 |

### 数字员工

| 方法 | 端点 | 用途 |
|--------|----------|---------|
| `GET` | `/admin/digital-employee` | 列出数字员工配置 |
| `POST` | `/admin/digital-employee` | 创建或更新数字员工 |

## 认证

所有 `/admin/*` 端点需要操作员认证。认证模式详情参见 [术语表](GLOSSARY.md)（启动令牌、操作员账户令牌、浏览器会话、OIDC JWT）。

## 安全

- ZIP-slip 防护：所有解压路径验证确保在目标目录内
- 文件大小限制防止资源耗尽
- 路径遍历以 400 响应拒绝
- 上传操作记录审计条目

## 相关文档

- [MCP App](MCPAPP.md) — 基于清单发现的 MCP Apps（与工作区 MCP 分开）
- [术语表](GLOSSARY.md) — 工作区管理定义
- [安全](../SECURITY.md) — 整体安全态势
- [企业频道](ENTERPRISE_CHANNELS.md) — 企业 IM 频道适配器

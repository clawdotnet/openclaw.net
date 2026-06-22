# 代码库 Harness 地图

Codebase Harness Map 为 OpenClaw.NET 提供仓库作为 Agent Harness 环境的结构化视图。

它映射：

- 解决方案和项目
- 模块
- 端点
- 工具
- Provider
- 通道
- 配置接口
- 测试
- 文档
- 最近的变更
- 指向契约、证据和共享 Harness 状态的链接

Agent 需要的不仅仅是文件搜索。在规划工作、委派任务或验证变更之前，它们需要一个有界的、可检查的环境地图。

## 与 Harness 基础组件的关系

- Harness Contracts 描述预期工作
- Evidence Bundles 记录发生了什么
- Shared Harness State 追踪活跃的多 Agent 工作
- Fractal Memory 存储持久的项目状态
- Codebase Harness Map 描述代码环境

## 命令

```bash
openclaw harness map
openclaw harness map --json
openclaw harness map --root ./src
openclaw harness map --category endpoints
openclaw harness map --output ./codebase-map.json
```

类别：`all`、`projects`、`endpoints`、`tools`、`providers`、`channels`、`config`、`tests`

## Admin API

```text
GET /admin/harness/codebase-map
GET /admin/harness/codebase-map?category=endpoints
GET /admin/harness/codebase-map?root=/path/under/workspace
```

Admin 端点受工作区根目录限制，拒绝配置工作区之外的请求根目录。

## 安全说明

- 扫描器不执行仓库代码
- Admin 端点将 `root` 限制在配置的工作区根目录内
- 不跟踪符号链接目录
- 扫描是保守的、静态的
- 近期变更标签基于文件系统修改时间
- 配置值不输出，密钥相关名称标记为敏感
- 哈希默认关闭

## 目前不支持的功能

- 非完整 Roslyn 语义图
- 非完整调用图
- 非测试覆盖率地图
- 非动态运行时追踪图
- 非 IDE 替代品
- 不直接写入 Fractal Memory 节点

# Dashboard 静态资源构建

Gateway 从 `wwwroot/dashboard` 提供 Dashboard 静态资源，但 Dashboard 项目并非以普通静态 Web 资源的 `ProjectReference` 方式引用。`src/OpenClaw.Gateway/OpenClaw.Gateway.csproj` 中的手动复制目标（manual copy targets）避免了静态 Web 资源冲突，同时仍能为本地构建和发布输出生成 Blazor WASM 文件。

## 默认行为

`CopyDashboardFiles` 在普通 Gateway 构建之后运行，将 `src/OpenClaw.Dashboard` 发布到 Gateway 输出目录：

```bash
dotnet build src/OpenClaw.Gateway/OpenClaw.Gateway.csproj -c Release
```

该目标将 Dashboard 的 `wwwroot` 文件复制到：

```text
src/OpenClaw.Gateway/bin/<Configuration>/<TFM>/wwwroot/dashboard
```

同时会创建非指纹化（non-fingerprinted）的 `blazor.webassembly.js` 和 `dotnet.js` 副本，确保 Dashboard 的 `index.html` 在被 `UseStaticFiles` 提供时能正确解析。

## 快速构建循环

当构建只需要 Gateway 代码和测试、不需要生成 Dashboard 静态资源时，设置 `OpenClawSkipDashboardBuild=true`：

```bash
dotnet build OpenClaw.Net.slnx -c Release -p:OpenClawSkipDashboardBuild=true
dotnet build src/OpenClaw.Gateway/OpenClaw.Gateway.csproj -c Release -p:OpenClawSkipDashboardBuild=true
```

跳过路径会从 Gateway 构建输出中移除 `wwwroot/dashboard`，避免旧构建中的过期 Dashboard 文件被意外提供。不要将其用于发布验证或任何需要检查已提供的 Dashboard 资源的场景。

## 发布行为

`CopyDashboardFilesPublish` 在 Gateway 发布之后运行，始终将 Dashboard 资源包含在发布目录中：

```bash
dotnet publish src/OpenClaw.Gateway/OpenClaw.Gateway.csproj -c Release
```

这确保了发布产物和冒烟脚本始终走完整的资源路径，即使普通构建循环启用了跳过属性。

## 排查指南

- 如果 `/dashboard` 返回缺少 `_framework` 文件，请在没有设置 `OpenClawSkipDashboardBuild=true` 的情况下重新构建或发布 Gateway。
- 如果 Gateway 构建期间静态 Web 资源解析失败，请将 Dashboard 保持在普通静态 Web 资源 `ProjectReference` 之外，改为更新手动复制目标。
- 如果 Blazor 启动脚本名称发生变化，请更新 Gateway 项目文件中的非指纹化副本匹配模式。

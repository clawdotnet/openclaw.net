# OpenClaw.NET MAF 1.28 升级设计说明

- 日期：2026-11-07
- 状态：设计已批准，待规格评审
- 语言：中文

## 1. 背景与目标

将仓库当前使用的 Microsoft Agent Framework (MAF) `1.10.0` 依赖升级到 `1.28`，保持 Gateway 的 A2A 行为与 MAF Adapter 的职责边界不变，并验证相关依赖族能够共同解析和运行。

核心包 `Microsoft.Agents.AI` 使用稳定版 `1.28.0`。当前 1.28 A2A Hosting 包为预览版；用户已同意使用 `1.28.0-preview.261106.1`。

## 2. 范围

### 2.1 In Scope

- 更新 `Directory.Packages.props` 中现有的三个 MAF 包：
  - `Microsoft.Agents.AI` → `1.28.0`
  - `Microsoft.Agents.AI.Hosting.A2A` → `1.28.0-preview.261106.1`
  - `Microsoft.Agents.AI.Hosting.A2A.AspNetCore` → `1.28.0-preview.261106.1`
- 将 A2A 与 A2A.AspNetCore 更新到 MAF 1.28 所需的 `1.0.0-preview4`。
- 按用户选择，将当前中心版本管理中的 Microsoft.Extensions.AI、Microsoft.Extensions、OpenTelemetry 依赖族整体升级到已查得的最新稳定版本：

| 包 | 目标版本 |
|---|---:|
| Microsoft.Extensions.AI | 10.11.0 |
| Microsoft.Extensions.AI.Abstractions | 10.11.0 |
| Microsoft.Extensions.AI.OpenAI | 10.11.0 |
| Microsoft.Extensions.Caching.Memory | 10.0.14 |
| Microsoft.Extensions.Configuration.Abstractions | 10.0.14 |
| Microsoft.Extensions.Configuration.Binder | 10.0.14 |
| Microsoft.Extensions.DependencyInjection | 10.0.14 |
| Microsoft.Extensions.DependencyInjection.Abstractions | 10.0.14 |
| Microsoft.Extensions.Hosting.Abstractions | 10.0.14 |
| Microsoft.Extensions.Http | 10.0.14 |
| Microsoft.Extensions.Logging.Abstractions | 10.0.14 |
| Microsoft.Extensions.Options | 10.0.14 |
| OpenTelemetry.Api | 1.20.1 |
| OpenTelemetry.Exporter.OpenTelemetryProtocol | 1.20.1 |
| OpenTelemetry.Extensions.Hosting | 1.20.1 |
| OpenTelemetry.Instrumentation.AspNetCore | 1.20.2 |
| OpenTelemetry.Instrumentation.Http | 1.20.2 |
| OpenTelemetry.Instrumentation.Runtime | 1.20.1 |
- 若升级引发实际编译不兼容，只在 MAF Adapter、Gateway 或对应测试中进行最小 API 适配。
- 保持所有项目的 `PackageReference` 版本无关，并继续由根目录 `Directory.Packages.props` 统一管理。

### 2.2 Out of Scope

- 与 MAF、A2A、Microsoft.Extensions 或 OpenTelemetry 升级无关的包更新。
- 将 MAF 类型或依赖引入 Core，或重构 Gateway/Runtime 边界。
- 预防性重写当前未受影响的 A2A API 使用方式。
- 改变用户可观察的 A2A、streaming、session 或错误行为。

## 3. 当前基线

当前 MAF 中心版本为 `1.10.0`；两个 MAF A2A Hosting 包为 `1.10.0-preview.260610.1`。A2A 与 A2A.AspNetCore 当前为 `1.0.0-preview2`。Microsoft.Extensions.AI 系列为 `10.7.0`，其他 Microsoft.Extensions 包为 `10.0.9` 或 `10.0.10`，OpenTelemetry 包为 `1.15.1` 至 `1.16.0`。

相关直接引用位于 `src/OpenClaw.MicrosoftAgentFrameworkAdapter`、`src/OpenClaw.Gateway` 和 `src/OpenClaw.Tests`。MAF Adapter 已标记为非 AOT 兼容；Core 不直接依赖 MAF。

## 4. 方案对比与选择

1. 只更新三个 MAF 包：变更较小，但与 MAF 1.28 要求的 A2A 和 Microsoft.Extensions.AI 依赖下限不一致。
2. 仅升级 MAF 1.28 明确要求的兼容依赖：范围较小，但不满足用户希望同步广泛依赖族的偏好。
3. 同步升级现有 Microsoft.Extensions.AI、Microsoft.Extensions 和 OpenTelemetry 中心版本：依赖版本更一致，但回归面较大。

选择方案 3。版本只覆盖这些现有包族与 MAF/A2A 依赖，不扩展到其他包族；通过完整 restore/build/test 与 Gateway smoke 限制回归风险。

## 5. 目标架构与行为边界

- 只在现有 Central Package Management 中更新版本，不改变依赖所有权或项目引用边界。
- `OpenClaw.MicrosoftAgentFrameworkAdapter` 继续封装 MAF 类型；Gateway 继续负责 A2A hosting；Core 不引用 MAF。
- MAF 1.28 发布记录包含 A2A agent 创建相关的 breaking change。仓库使用自定义 `AIAgent` 实现和 `AddA2AServer` 注册，而非该 A2A client helper；因此维持现状，只有编译验证证明必要时才适配。
- 保持 A2A wire contract、streaming、session 序列化和 Gateway 错误行为不变。
- 不调整 NativeAOT 兼容声明，不在 Core 中引入反射或动态加载路径。

## 6. 组件改造清单

### 6.1 中心包版本

- 文件：`Directory.Packages.props`
- 目标：更新 MAF、A2A、Microsoft.Extensions.AI/.NET Extensions 和 OpenTelemetry 的现有版本项。
- 不增加不必要的传递依赖版本钉制，不修改项目本地 `PackageReference` 版本。

### 6.2 MAF Adapter 与 Gateway

- 位置：`src/OpenClaw.MicrosoftAgentFrameworkAdapter`、`src/OpenClaw.Gateway/A2A`
- 目标：仅处理由新包 API 引起的编译错误；保留当前适配器、服务注册与 endpoint 结构。

### 6.3 测试

- 位置：`src/OpenClaw.Tests`
- 目标：使用现有 MAF 与 A2A 测试验证 API、session、streaming 和协议行为；若实际 API 差异暴露回归，则新增聚焦测试。

## 7. 错误处理与兼容策略

- Restore、构建或测试失败必须显式处理；不跳过未解决的包冲突，也不通过静默回退伪装成功。
- 若目标版本发生 API 不兼容，优先使用新 API 的最小局部改动，并保留现有协议与错误语义。
- 若兼容所需升级超出本文列出的包族，先核实实际依赖约束和失败证据，再决定是否扩展范围。
- 只有用户可观察行为或使用方式发生变化时，才同步更新相关用户文档。

## 8. 验收标准

从仓库根目录执行：

```powershell
dotnet restore OpenClaw.Net.slnx
dotnet build OpenClaw.Net.slnx --configuration Release --no-restore
dotnet test OpenClaw.Net.slnx --configuration Release --no-build
dotnet build OpenClaw.Net.slnx --configuration Release --no-restore -p:OpenClawEnableMafExperiment=true
dotnet run --project src/OpenClaw.Gateway -c Release --no-build -- --doctor
```

验收要求：

- 所有 restore、Release build、MAF lane build、测试和 doctor smoke 均成功。
- Release build 无警告；测试报告的执行数非零。
- A2A、streaming、session 与 Gateway 启动行为保持现有测试契约。
- Core 的 NativeAOT 兼容属性和依赖边界未改变。

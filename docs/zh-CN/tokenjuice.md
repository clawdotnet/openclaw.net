# TokenJuice：规则驱动的工具输出归约引擎

**状态：** 已实现 | **上游：** [vincentkoc/tokenjuice](https://github.com/vincentkoc/tokenjuice) (MIT) | **插件：** `OpenClaw.Plugins.TokenJuice`

## 概述

TokenJuice 是面向 AI Agent 运行时的确定性规则驱动输出归约引擎。它在工具原始输出（Shell 命令、构建日志、HTTP 响应、CLI 工具）**进入 LLM 上下文窗口之前**进行拦截，将其体积压缩 50–95%，同时完整保留核心语义、诊断信号和退出码。

与基于 LLM 的二次总结（用 Token 换 Token 的悖论）不同，TokenJuice 使用静态声明的 JSON 规则，通过匹配工具名、命令行参数、退出码和输出内容模式来实现归约。整个过程瞬时完成，确定性执行，零额外成本。

## 为什么需要 TokenJuice

在自主 Agent 工作流中，每次工具调用的结果都会被追加到对话历史中，并在后续轮次中重新发送给模型。原始输出通常包含：

- 终端输出中的 ANSI 转义序列
- 冗余的构建产物清单（数百行 `→ .dll`）
- 轮询循环中重复的状态行
- `curl` 返回的完整 HTML 页面
- 日志输出中相邻的重复行

如果不做归约，Token 消耗随轮次呈二次方增长：

$$T_{\text{total}} \approx \sum_{i=1}^{N} \left( C_{\text{base}} + \sum_{j=1}^{i-1} O_j \right)$$

其中 $O_j$ 是第 $j$ 轮的工具原始输出。引入 TokenJuice 后，每次输出乘以压缩系数 $\alpha \in [0.05, 0.5]$：

$$T'_{\text{total}} \approx \sum_{i=1}^{N} \left( C_{\text{base}} + \sum_{j=1}^{i-1} \alpha_j \cdot O_j \right)$$

## 架构

```
┌─────────────────────────────────────────────────────────┐
│                   OpenClawToolExecutor                   │
│                                                         │
│  Tool.Execute() → Redaction → Interceptor Pipeline      │
│                                    │                    │
│                           ┌────────▼──────────┐         │
│                           │ TokenJuiceInterceptor│       │
│                           │                        │     │
│                           │  ┌──────────────────┐ │     │
│                           │  │ 逃逸检测?        │ │     │
│                           │  │ (--raw / --full) │ │     │
│                           │  └──────┬───────────┘ │     │
│                           │         │              │     │
│                           │    ┌────▼─────────┐    │     │
│                           │    │ 规则匹配      │    │     │
│                           │    └────┬─────────┘    │     │
│                           │         │              │     │
│                           │    ┌────▼─────────┐    │     │
│                           │    │ 命中?         │    │     │
│                           │    │ 是 → 归约     │    │     │
│                           │    │ 否 → 密度检测 │    │     │
│                           │    └──────────────┘    │     │
│                           └────────────────────────┘     │
│                                    │                    │
│                           Interceptor Pipeline          │
│                                    │                    │
│                           → IToolHook.AfterExecute      │
│                           → 返回 LLM 上下文              │
└─────────────────────────────────────────────────────────┘
```

## 规则结构

每条规则是一个 JSON 文档，包含以下模块：

```json
{
  "id": "build/dotnet",
  "family": "build-dotnet",
  "description": "压缩 dotnet build 输出，保留诊断信息和最终摘要。",
  "match": {
    "toolNames": ["exec"],
    "argv0": ["dotnet"],
    "argvIncludesAny": [["build"], ["restore"], ["publish"]]
  },
  "transforms": {
    "stripAnsi": true,
    "dedupeAdjacent": true,
    "trimEmptyEdges": true
  },
  "summarize": {
    "head": 12,
    "tail": 12
  },
  "failure": {
    "preserveOnFailure": true,
    "head": 18,
    "tail": 18
  },
  "counters": [
    { "name": "error", "pattern": "error [A-Z]+\\d+|Build FAILED|failed", "flags": "i" },
    { "name": "warning", "pattern": "warning [A-Z]+\\d+|warning", "flags": "i" }
  ]
}
```

### 匹配维度

| 字段 | 行为 |
|---|---|
| `toolNames` | 精确匹配工具名。`exec` 匹配 shell/进程工具。 |
| `argv0` | 匹配命令行的第一个 token（如 `dotnet`、`git`、`npm`）。 |
| `argvIncludes` | 所有指定 flag 必须同时出现。 |
| `argvIncludesAny` | 任一 flag 组出现即匹配。 |
| `commandIncludes` | 命令文本包含所有关键字（不区分大小写）。 |
| `commandIncludesAny` | 命令文本包含任一关键字。 |
| `commandRegex` | 完整命令文本正则匹配。 |
| `exitCodes` | 匹配特定退出码。 |
| `outputRegex` | 通过正则匹配输出内容。 |

### 归约管道

```
原始输出
  ↓
1. StripAnsi        — 移除终端转义序列 (transforms.stripAnsi)
  ↓
2. TrimEmptyEdges   — 裁剪首尾空行 (transforms.trimEmptyEdges)
  ↓
3. DedupeAdjacent   — 合并相邻重复行 (transforms.dedupeAdjacent)
  ↓
4. SkipPatterns     — 按正则丢弃匹配行 (filters.skipPatterns)
  ↓
5. KeepPatterns     — 仅保留匹配行 (filters.keepPatterns)
  ↓
6. OutputMatches    — 若输出匹配模式，返回预设摘要消息
  ↓
7. Counters         — 按模式统计 error/warning 出现次数
  ↓
8. HeadTail         — 保留前 N + 后 M 行，中间注入 "... omitted X lines ..."
  ↓
9. Inline Format    — 将退出码 + 事实计数 + 摘要拼接为单条消息
```

### 语义密度兜底

当无规则命中时，语义密度检测作为安全网：

$$\rho = \frac{U}{\max(L, 1)} \cdot \frac{N}{\max(C, 1)}$$

其中 $L$ = 总行数，$U$ = 唯一行数，$C$ = 总字符数，$N$ = 非空白字符数。

若 $\rho < 0.3$（可配置），自动应用 `generic/fallback` 规则进行归约。

## 三层规则配置

| 层级 | 路径 | 优先级 | 用途 |
|---|---|---|---|
| **内置** | 嵌入程序集资源 | 最低 | 常用工具默认规则（git、dotnet、npm、docker、curl……） |
| **用户** | `~/.config/tokenjuice/rules/*.json` | 中等 | 跨项目的个人偏好 |
| **项目** | `.tokenjuice/rules/*.json` | 最高 | 团队标准，随 Git 提交 |

同 `id` 规则被更高优先级层覆盖。`generic/fallback` 规则始终排在匹配队列末尾。

## 逃逸通道

两种机制确保在需要字节精确输出的场景中跳过归约：

1. **参数扫描：** 若 `argumentsJson` 包含 `--raw` 或 `--full`，跳过归约。
2. **编程式绕过：** 在上游代码中设置 `ReductionContext.BypassReduction = true` 可跳过所有拦截器。

适用场景：`git diff` 输出（空白缩进必须精确保留）、加密操作（需精确输出）、二进制数据。

## 性能

| 指标 | 目标值 |
|---|---|
| 单次调用延迟（100 KB 输入） | < 5 ms |
| 冷启动规则加载（129 条规则） | < 50 ms |
| 单次归约内存开销 | < 输入的 1.2 倍 |
| NativeAOT 二进制增量 | < 500 KB |

### 基准参考数据

| 测试命令 | 原始体积 | 归约后 | 压缩率 | 触发的策略 |
|---|---|---|---|---|
| `git diff` | 412.5 KB | 98.2 KB | 76.2% | 上下文剔除 + 段落折叠 |
| `dotnet build` | 1,280 KB | 64.0 KB | 95.0% | 非异常流过滤 + 异常栈保留 |
| `docker ps -a` | 48.0 KB | 8.1 KB | 83.1% | 空白列折叠 + 表头去除 |
| `curl` HTML | 2,540 KB | 381 KB | 85.0% | 富文本标签剥离 |
| `git status` | 12.0 KB | 2.1 KB | 82.5% | 冲突/修改节点提取 |

## 错误处理

**Fail-open 设计。** 归约管道中任何组件抛出异常时：

- 规则 JSON 解析失败 → 跳过该文件，记录 warning 日志
- 正则模式编译失败 → 跳过该过滤器
- 归约结果为空 → 返回原始输出
- 归约结果大于原始输出 → 返回原始输出
- 拦截器抛出异常 → 捕获、记录日志、返回原始输出

管道永远不会阻止工具输出到达 LLM。

## 集成方式

TokenJuice 是一个原生 C# 插件（`OpenClaw.Plugins.TokenJuice`），通过 `INativeDynamicPlugin` 接口注册。它实现了 `IToolResultInterceptor`，在 `OpenClawToolExecutor` 管道中的执行顺序为：**redaction 之后、`IToolHook.AfterExecute` 之前**：

```csharp
// 工具执行管道顺序：
// 1. IToolHook.BeforeExecute  （审批、审计）
// 2. ITool.ExecuteAsync       （实际工具调用）
// 3. IRedactionPipeline       （密钥脱敏）
// 4. IToolResultInterceptor   （输出归约 — TokenJuice 在此层）
// 5. IToolHook.AfterExecute   （可观测性、日志）
// 6. 返回 LLM 上下文
```

## 测试

内置 129 条规则，覆盖 20+ 个工具家族。11 个集成测试覆盖：

- 规则匹配引擎（9 个匹配维度）
- 归约策略（StripAnsi、TrimEdges、Dedupe、HeadTail、Counters）
- 语义密度计算
- 逃逸通道（--raw、--full）
- Inline 文本格式化（退出码 + 事实计数 + 摘要）
- 端到端拦截器管道

全量回归：2181 个测试通过（零回归）。

## 与上游的关系

- 上游项目：[vincentkoc/tokenjuice](https://github.com/vincentkoc/tokenjuice)，MIT 许可证
- 规则 JSON 派生自上游，按 MIT 条款重新分发
- OpenClaw.NET 版本采用纯 C# 实现（.NET 10, C# 13），NativeAOT 兼容
- Python 参考实现位于 [OpenSquilla](https://github.com/openclaw/openclaw)`/src/opensquilla/plugins/tokenjuice/`

## 参考

- 设计规格：`docs/superpowers/specs/2026-06-19-tokenjuice-migration-design.md`
- 实现计划：`docs/superpowers/plans/2026-06-19-tokenjuice-migration.md`
- 源码：`src/OpenClaw.Plugins.TokenJuice/`
- 测试：`src/OpenClaw.Tests/TokenJuiceIntegrationTests.cs`
- 迁移方案：`docs/zh-CN/Tokenjuice Migration Plan.md`

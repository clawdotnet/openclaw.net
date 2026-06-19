# Tokenjuice Migration Design: OpenSquilla → OpenClaw.NET

**日期：** 2026-06-19
**状态：** Draft — 等待用户审查
**上游：** [vincentkoc/tokenjuice](https://github.com/vincentkoc/tokenjuice) (MIT License)
**参考：** `docs/zh-CN/Tokenjuice Migration Plan.md`, `E:\GitHub\opensquilla\src\opensquilla\plugins\tokenjuice\`

---

## 1. 概述

将 OpenSquilla 的 Python 版 tokenjuice 规则驱动工具输出归约引擎移植到 OpenClaw.NET，以纯 C# NativeAOT 友好的方式实现。目标是在工具结果进入 LLM 上下文窗口之前，通过确定性规则压缩输出体积 50-95%，降低 Token 消耗成本并减少上下文噪声。

---

## 2. 关键设计决策

| # | 决策点 | 选择 | 理由 |
|---|---|---|---|
| 1 | 集成方式 | 新建 `IToolResultInterceptor` 管道 + 保留 `IToolHook` | 关注点分离：Hook 做审批/审计，Interceptor 做数据变换 |
| 2 | 项目分层 | `Core` 放接口/管道 + `Plugins.TokenJuice` 放实现/规则 | 核心保持轻量，归约逻辑可选加载 |
| 3 | 逃逸通道 | 双通道：`BypassReduction` 标志 + `--raw`/`--full` 参数扫描 | 覆盖编程式和 LLM 自然表达两种逃逸场景 |
| 4 | 规则格式 | 完全兼容上游 JSON + 允许 C# 扩展字段 | 上游规则可直接复用，未知字段被反序列化器忽略 |
| 5 | 规则范围 | 全量移植 20+ 家族 | 一次覆盖，减少后续迭代 |
| 6 | 触发机制 | 规则匹配优先 + 语义密度兜底 | 精确规则保证确定性，密度计算覆盖未知模式 |
| 7 | 错误处理 | fail-open | 归约失败永远返回原始文本，不阻断工具调用链 |

---

## 3. 项目结构

```
OpenClaw.Core/                              ← 接口定义层
├── Abstractions/
│   ├── IToolResultInterceptor.cs          ← 新增：结果归约拦截器接口
│   ├── ReductionContext.cs                ← 新增：归约上下文
│   └── ReductionResult.cs                 ← 新增：归约结果 DTO

OpenClaw.Plugins.TokenJuice/                ← 实现层（独立插件项目）
├── TokenJuicePlugin.cs                     ← INativeDynamicPlugin 入口
├── Reduction/
│   ├── TokenJuiceInterceptor.cs            ← IToolResultInterceptor 实现
│   ├── SemanticDensityCalculator.cs        ← 语义密度计算
│   └── ReductionStrategies.cs              ← 四种清洗策略
├── Matching/
│   ├── RuleMatcher.cs                      ← 规则匹配引擎
│   └── CommandArgvParser.cs                ← 命令行参数解析
├── Rules/
│   ├── RuleLoader.cs                       ← 三层配置加载
│   ├── Rule.cs                             ← 规则实体
│   └── rules/                              ← EmbeddedResource: 20+ 家族 JSON
│       ├── build/   (dotnet, cmake, npm, gradle...)
│       ├── git/     (diff, status, log, show...)
│       ├── cloud/   (docker, kubectl, aws...)
│       ├── network/ (curl, ping, netstat...)
│       ├── generic/ (fallback)
│       └── ...
└── Formatters/
    ├── AnsiStripper.cs
    ├── LineDeduplicator.cs
    ├── WhitespaceFolder.cs
    └── HeadTailTrimmer.cs

OpenClaw.Plugins.TokenJuice.Tests/          ← 测试项目
├── TokenJuiceInterceptorTests.cs
├── RuleMatcherTests.cs
├── ReductionStrategiesTests.cs
├── SemanticDensityCalculatorTests.cs
├── RuleLoaderTests.cs
├── EscapeHatchTests.cs
├── FailOpenTests.cs
└── Fixtures/
```

---

## 4. 管道插槽与执行流程

### 4.1 `OpenClawToolExecutor` 集成点

```
BeforeExecute (hooks) → 执行工具 → InterceptorPipeline → AfterExecute (hooks) → 返回结果
```

`OpenClawToolExecutor` 新增构造函数参数 `IReadOnlyList<IToolResultInterceptor>?`，在执行完成后的结果文本依次通过拦截器链。

### 4.2 `INativeDynamicPluginContext` 新增注册方法

```csharp
void RegisterResultInterceptor(IToolResultInterceptor interceptor);
```

### 4.3 完整执行流

```
LLM 工具调用
    │
    ▼
OpenClawToolExecutor
    │
    ├─ IToolHook.BeforeExecute (审批)
    ├─ ITool.ExecuteAsync (执行)
    ├─ IToolResultInterceptor 链
    │    ├─ 逃逸检测: BypassReduction? 或 --raw/--full?
    │    │    yes → 跳过所有归约
    │    │    no  → 继续
    │    ├─ 规则匹配?
    │    │   命中 → 执行对应规则归约
    │    │   未命中 → 语义密度 < 0.3?
    │    │        yes → 应用 generic/fallback 归约
    │    │        no  → 原样返回
    │    └─ 返回 ReductionResult
    ├─ IToolHook.AfterExecute (审计/日志)
    └─ 返回 LLM 上下文
```

---

## 5. 核心接口与数据模型

### 5.1 `IToolResultInterceptor`

```csharp
public interface IToolResultInterceptor
{
    int Order { get; }
    string Name { get; }
    ValueTask<string> InterceptAsync(
        string toolName, string argumentsJson, string rawOutput, CancellationToken ct);
}
```

### 5.2 `ReductionContext`

```csharp
public readonly record struct ReductionContext
{
    public required string ToolName { get; init; }
    public required string ArgumentsJson { get; init; }
    public required string RawOutput { get; init; }
    public required bool IsError { get; init; }
    public required bool BypassReduction { get; init; }
}
```

### 5.3 `ReductionResult`

```csharp
public readonly record struct ReductionResult
{
    public required string Text { get; init; }
    public required int OriginalLength { get; init; }
    public required int ReducedLength { get; init; }
    public required double Ratio { get; init; }
    public string? ReducerId { get; init; }
    public bool WasReduced => ReducedLength < OriginalLength && !string.IsNullOrEmpty(ReducerId);

    public static ReductionResult Unchanged(string text)
        => new()
        {
            Text = text,
            OriginalLength = text.Length,
            ReducedLength = text.Length,
            Ratio = 1.0,
        };
}
```

### 5.4 Inline 文本格式化

归约后的 summary 和 facts 计数需要拼接为单条 inline 文本，等价 Python 版 `_format_inline()`：

```
格式规则：
  [exit N]          ← 仅当 exit_code != 0
  [fact_name: count] ← 仅非零 facts
  [summary]

示例：
  exit 1
  error: 12; warning: 3
  Build FAILED. 2 Error(s), 3 Warning(s)
```

若配置了 `maxInlineChars`，超过该长度的 inline 文本将被截断为：
`{前半部分}\n... omitted chars ...\n{后半部分}`

inline 文本长度 ≥ 原始内容长度时，归约视为无效，返回 `ReductionResult.Unchanged`。

### 5.4 逃逸通道（双入口）

- **入口 1：** `ReductionContext.BypassReduction = true`（编程式）
- **入口 2：** `argumentsJson` 包含 `--raw` 或 `--full` 字面量（LLM 自然表达）

两个入口任一满足即跳过所有归约。

---

## 6. 规则引擎

### 6.1 规则实体（AOT 安全）

所有 JSON 反序列化通过 `JsonSourceGenerator` 静态生成，零反射。

```csharp
[JsonSourceGenerationOptions(WriteIndented = false, GenerationMode = JsonSourceGenerationMode.Metadata)]
[JsonSerializable(typeof(List<TokenJuiceRule>))]
internal partial class TokenJuiceJsonContext : JsonSerializerContext { }

public sealed class TokenJuiceRule
{
    [JsonPropertyName("id")] public string Id { get; set; } = string.Empty;
    [JsonPropertyName("family")] public string Family { get; set; } = "generic";
    [JsonPropertyName("description")] public string? Description { get; set; }
    [JsonPropertyName("priority")] public int Priority { get; set; }

    // match 段
    [JsonPropertyName("match")] public RuleMatchBlock? Match { get; set; }

    // 操作段
    [JsonPropertyName("transforms")] public RuleTransformsBlock? Transforms { get; set; }
    [JsonPropertyName("summarize")] public RuleSummarizeBlock? Summarize { get; set; }
    [JsonPropertyName("failure")] public RuleFailureBlock? Failure { get; set; }
    [JsonPropertyName("counters")] public List<RuleCounter>? Counters { get; set; }
    [JsonPropertyName("filters")] public RuleFiltersBlock? Filters { get; set; }
    [JsonPropertyName("outputMatches")] public List<RuleOutputMatch>? OutputMatches { get; set; }
    [JsonPropertyName("onEmpty")] public string? OnEmpty { get; set; }
    [JsonPropertyName("counterSource")] public string? CounterSource { get; set; }

    // C# 扩展字段（上游规则无此字段，反序列化器忽略）
    [JsonPropertyName("_aotHint")] public string? AotHint { get; set; }
    [JsonPropertyName("_platformFilter")] public string? PlatformFilter { get; set; }
}
```

### 6.2 规则匹配维度

| 匹配字段 | 说明 |
|---|---|
| `toolNames` | 精确匹配工具名，`exec` 表示 shell 命令 |
| `argv0` | 匹配命令行第一个 token（如 `dotnet`、`git`） |
| `argvIncludes` | 所有指定 flag 必须同时出现 |
| `argvIncludesAny` | 任一 flag 出现即匹配 |
| `commandIncludes` | 命令文本包含所有关键字（不区分大小写） |
| `commandIncludesAny` | 命令文本包含任一关键字 |
| `commandRegex` | 正则匹配完整命令文本 |
| `exitCodes` | 匹配特定退出码（`isError=true` → `exitCode=1`, `false` → `0`） |
| `outputRegex` | 正则匹配输出内容 |

**exit_code 数据流：** `ReductionContext.IsError` → 内部 `exitCode = IsError ? 1 : 0` → 用于规则匹配 + 失败模式窗口放大。

### 6.3 规则选择逻辑

按优先级遍历所有规则 → 第一个完全匹配的规则当选 → 若全部不匹配 → 返回 null（触发密度兜底）。

---

## 7. 归约策略管道

输入文本经过以下处理阶段：

```
输入文本
    │
    ├─ 1. StripAnsi ──────── 移除 ANSI 转义序列 (若 transforms.stripAnsi)
    ├─ 2. TrimEmptyEdges ─── 裁剪首尾空行 (若 transforms.trimEmptyEdges)
    ├─ 3. DedupeAdjacent ─── 合并相邻重复行 (若 transforms.dedupeAdjacent)
    │
    ├─ 4. SkipPatterns ───── 按正则丢弃匹配行 (若 filters.skipPatterns)
    ├─ 5. KeepPatterns ───── 仅保留匹配行 (若 filters.keepPatterns，有匹配时)
    │
    ├─ 6. HeadTail ───────── 取前 N 行 + 后 M 行 + "... omitted X lines ..."
    │                        失败时放大窗口 (failure.head/failure.tail)
    │
    ├─ 7. Counters ───────── 统计 error/warning 等事实计数
    ├─ 8. OutputMatches ──── 若输出匹配指定模式，返回预设摘要消息
    │
    └─ 输出 ──────────────── { summary, facts: { error: n, warning: m } }
```

### 语义密度计算（兜底触发）

当无规则命中时，计算语义密度：

$\rho = \frac{U}{\max(L,1)} \times \frac{N}{\max(C,1)}$

其中 $L$ = 总行数, $U$ = 唯一行数, $C$ = 总字符数, $N$ = 非空白字符数。

若 $\rho < 0.3$（可配置），自动应用 `generic/fallback` 归约。

---

## 8. 三层规则加载

| 层级 | 路径 | 职责 |
|---|---|---|
| Builtin | 嵌入程序集资源 | 通用规则，随二进制分发 |
| User | `~/.config/tokenjuice/rules/*.json` | 用户全局偏好 |
| Project | `.tokenjuice/rules/*.json` | 项目团队标准，随 Git 提交 |

**合并策略：** 同 `id` 规则后者覆盖前者。`generic/fallback` 总是排在匹配队列末尾。

---

## 9. 错误处理

**核心原则：fail-open — 归约失败时永远返回原始文本。**

| 场景 | 处理方式 |
|---|---|
| JSON 规则解析失败 | 静默跳过该文件，记录 warning 日志 |
| 规则正则编译失败 | 跳过该条规则的 patterns，规则本身仍可用 |
| 归约后文本为空 | 返回 `onEmpty` 消息，若无则返回原始文本 |
| 归约后 > 原始大小 | 返回原始文本 (`ReductionResult.Unchanged`) |
| 拦截器抛出异常 | 捕获，记录 error 日志，返回原始文本 |
| NativeAOT 裁剪 | 所有 JSON 通过 `JsonSourceGenerator` 静态生成 |

---

## 10. 性能目标

| 指标 | 目标值 |
|---|---|
| 单次归约延迟（100KB 输入） | < 5ms |
| 规则加载冷启动（20 家族，~80 文件） | < 50ms |
| 内存分配（单次归约） | 输入文本的 1.2x 以内 |
| NativeAOT 二进制增量 | < 500KB（含所有规则 JSON） |

### 基准参考数据（来自上游）

| 测试命令 | 原始体积 | 归约后 | 压缩率 | 策略 |
|---|---|---|---|---|
| `git diff` | 412.5 KB | 98.2 KB | 76.2% | 上下文剔除 + 段落折叠 |
| `dotnet build` | 1,280 KB | 64.0 KB | 95.0% | 非异常流过滤 + 异常栈保留 |
| `docker ps -a` | 48.0 KB | 8.1 KB | 83.1% | 空白列折叠 + 表头去除 |
| `curl` HTML | 2,540 KB | 381 KB | 85.0% | HTML→Markdown 剥离 |
| `git status` | 12.0 KB | 2.1 KB | 82.5% | 冲突/修改节点提取 |

---

## 11. 测试策略

### 测试项目：`OpenClaw.Plugins.TokenJuice.Tests`

| 测试类 | 覆盖内容 |
|---|---|
| `RuleMatcherTests` | 9 种匹配维度的单元测试 |
| `ReductionStrategiesTests` | StripAnsi, TrimEdges, Dedupe, HeadTail, Counters |
| `SemanticDensityCalculatorTests` | 密度计算边界：空输入、全重复、纯空白 |
| `RuleLoaderTests` | 三层加载合并、覆盖顺序、损坏 JSON 跳过 |
| `EscapeHatchTests` | `--raw`/`--full`/`BypassReduction` 三种逃逸 |
| `FailOpenTests` | 异常不阻断管道、空输出处理 |
| `TokenJuiceInterceptorTests` | 端到端集成：真实 dotnet build / git diff 输出 |
| AOT 验证 | `JsonSourceGenerator` 序列化/反序列化往返 |

---

## 12. 上游归属

- 上游项目： [vincentkoc/tokenjuice](https://github.com/vincentkoc/tokenjuice) (MIT License)
- 规则文件派生自上游，按 MIT 条款重新分发
- `LICENSE.tokenjuice` 和 `THIRD_PARTY_NOTICES.md` 需保持同步
- 测试夹具使用合成数据，不从上游复制

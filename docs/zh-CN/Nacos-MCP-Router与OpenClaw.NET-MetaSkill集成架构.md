# Nacos MCP Router 与 OpenClaw.NET MetaSkill 集成架构设计

> 版本：v1.0　　日期：2026-09-13
>
> 适用环境：Nacos 3.2.4（MCP Registry）· Nacos MCP Router（router 模式）· OpenClaw.NET Runtime

---

## 1. 概述

本文档描述如何将 **Nacos MCP Router** 的动态能力发现机制与 **OpenClaw.NET 的 MetaSkill 技能编排体系**结合，构建一个「注册—发现—绑定—编排」四层解耦的 Agent 能力架构。

核心命题：

- **Nacos MCP Router** 解决「能力发现与延迟加载」：Agent 只暴露 `search_mcp_server`、`add_mcp_server`、`use_tool` 三个工具，通过关键字 + 语义向量检索按需挂载后端 MCP Server，避免一次性向模型暴露全部工具描述，显著降低 Token 消耗。
- **OpenClaw.NET MetaSkill** 解决「能力编排」：将领域工作流投影为技能 DAG，节点代表能力需求，边代表数据依赖。

两者的结合点：**把 MetaSkill 节点声明为「能力槽位（Capability Slot）」，把 Router 的三步工具链作为槽位的运行时绑定机制**。MetaSkill 声明「我需要什么能力」，Router 回答「注册中心里当前谁能提供」。

## 2. 背景与问题

### 2.1 要解决的痛点

| 痛点 | 表现 |
|---|---|
| 工具膨胀 | 后端 MCP Server 数量增长后，全部工具 Schema 注入上下文导致 Token 成本失控、模型选择困难 |
| 能力硬编码 | 工作流中直接写死具体服务/工具，服务升级或替换时需要改动编排逻辑，能力体系不可演进 |
| 治理缺失 | 私有/本地工具缺乏统一的注册、版本与准入管理 |
| 重复解析 | 每次会话由模型即兴完成「搜索—安装—调用」链，三轮 LLM 往返，成本高且行为不可复现 |

### 2.2 两者的职责互补

```mermaid
flowchart LR
    subgraph 痛点层
        P1[工具膨胀<br/>Token 成本] 
        P2[能力硬编码<br/>不可演进]
    end
    subgraph 解耦层
        R[Nacos MCP Router<br/>动态发现 · 延迟加载 · 代理调用]
        M[OpenClaw.NET MetaSkill<br/>意图声明 · DAG 编排]
    end
    P1 --> R
    P2 --> M
    R <-.->|能力槽位 × 三步绑定链| M
```

## 3. 核心概念映射

| Nacos / Router 侧 | OpenClaw.NET / MetaSkill 侧 | 映射关系 |
|---|---|---|
| MCP Server 注册信息（名称、版本、描述） | MetaSkill 节点的 capabilityRef 约束 | 注册即能力的供给侧元数据 |
| `description` 字段 | intent 中的 task_description / key_words | 共用同一套领域本体词汇 |
| Nacos namespace / group | DDD 限界上下文 | 命名空间划分领域边界 |
| `search_mcp_server` | Capability Resolver 的检索阶段 | 槽位解析第一步 |
| `add_mcp_server` | 绑定建立 + 连接池初始化 | 槽位解析第二步 |
| `use_tool` | DAG 节点的实际执行 | 透明代理调用 |
| Nacos 配置变更事件 | 绑定缓存失效信号 | 治理平面驱动运行时刷新 |
| 导入允许列表（3.2.4 新增） | MetaSkill 包的依赖准入 | 管理员预先放行私有/本地工具 |

## 4. 总体架构

```mermaid
flowchart TB
    subgraph OC["OpenClaw.NET Runtime"]
        direction TB
        DAG["MetaSkill DAG Executor<br/>(编排平面)"]
        RES["Capability Resolver<br/>绑定缓存 · 选择策略 · 降级"]
        POOL["MCP 连接池<br/>(按 Server 名幂等复用)"]
        OBS["Trajectory / Audit 导出<br/>(可观测性闭环)"]
        DAG --> RES
        RES --> POOL
        DAG --> OBS
    end

    subgraph GW["Gateway MCP Client"]
        MCP["streamableHTTP<br/>http://router:8000/mcp"]
    end

    subgraph ROUTER["Nacos MCP Router (router 模式)"]
        direction TB
        T1["search_mcp_server<br/>关键字 + ChromaDB 语义检索 (Top-5)"]
        T2["add_mcp_server<br/>stdio 安装 / SSE 直连"]
        T3["use_tool<br/>透明代理转发"]
        T1 --> T2 --> T3
    end

    subgraph NACOS["Nacos 3.2.4 (MCP Registry — 注册与治理平面)"]
        direction TB
        REG["Server 注册 / 版本 / 描述"]
        NS["namespace · group<br/>(限界上下文划分)"]
        AL["导入允许列表<br/>(私有/本地地址准入)"]
        EVT["配置变更事件推送"]
    end

    subgraph BACKEND["后端 MCP Servers"]
        S1["weather-mcp"]
        S2["amap-mcp-server"]
        S3["... (动态扩展)"]
    end

    OC --> GW
    GW -->|"Agent 仅见 3 个工具"| ROUTER
    ROUTER --> REG
    NACOS -.->|变更事件 → 缓存失效| RES
    T3 --> BACKEND
```

架构平面划分：

- **注册与治理平面**（Nacos）：能力的供给侧元数据、版本、准入控制。
- **动态绑定平面**（Router）：检索、安装/连接、代理执行。
- **编排平面**（OpenClaw.NET MetaSkill）：以 DAG 声明能力需求与数据依赖，不感知具体服务。
- **运行时支撑**（Resolver / 连接池 / 缓存）：把三步链从 LLM 决策下沉为确定性代码路径。

## 5. 核心设计：能力槽位与绑定模式

### 5.1 能力槽位（Capability Slot）

MetaSkill 节点不直接引用工具，而是声明一个能力槽位。槽位支持两种绑定模式：

- **静态绑定**：节点直接写死 `mcp_server_name + tool_name`，启动时一次性 `add_mcp_server` 解析并缓存。适用于核心链路、对稳定性要求高的节点。
- **动态绑定（延迟绑定）**：节点只携带 intent（`task_description` + `key_words`），DAG 执行到该节点时才走 `search → add → use` 完成晚绑定。适用于长尾能力、跨系统能力——这正是 Router Top-5 语义检索的价值场景。

### 5.2 节点 Schema 示例（JSON-LD 投影）

能力需求以领域本体词汇表达，与 Nacos 注册描述共用同一套术语：

```json
{
  "@context": {
    "cap": "https://example.com/ontology/capability#",
    "ms": "https://agentqi.dev/metaskill#"
  },
  "@id": "ms:node/weather-query",
  "@type": "ms:SkillNode",
  "ms:capabilityRef": {
    "ms:binding": "dynamic",
    "ms:intent": {
      "@type": "cap:WeatherQuery",
      "ms:taskDescription": "查询指定城市的天气",
      "ms:keywords": "天气,城市"
    },
    "ms:selectionPolicy": "first",
    "ms:fallback": "ms:node/native-web-search"
  }
}
```

> 契约对齐（#230 已实现）：`ms:keywords` 为**逗号分隔字符串**（对应 `resolve_capability` 的 `key_words` wire 参数），`ms:selectionPolicy` 为 `first` / `exact_name` 字符串枚举（对应 `selection_policy`）。`topK` / `preferVersion` 是 #231 预留扩展，Resolver 支持前不得写入节点 schema。

领域对象（DDD）经 JSON-LD Framing 投影为 MetaSkill 节点时，`@type`（如 `cap:WeatherQuery`）同时作为语义检索的关键词来源，保证「本体词汇 → 检索查询 → 注册描述」三者处于同一向量空间。

## 6. 执行时序

一次 DAG 节点执行的完整链路：

```mermaid
sequenceDiagram
    autonumber
    participant DAG as MetaSkill DAG Executor
    participant RES as Capability Resolver
    participant CACHE as 绑定缓存 (会话级)
    participant RT as Nacos MCP Router
    participant NC as Nacos Registry
    participant SRV as 目标 MCP Server

    DAG->>RES: 调度节点 (intent: task_description + key_words)
    RES->>CACHE: 查询绑定 (key = intent 哈希)
    alt 缓存命中
        CACHE-->>RES: 返回 (server, tool, schema)
    else 缓存未命中
        RES->>RT: search_mcp_server(task_description, key_words)
        RT->>NC: 读取注册列表 (内存缓存 + ChromaDB)
        RT-->>RES: Top-5 候选 Server + 指引
        RES->>RES: 按 selectionPolicy 选定 Server
        RES->>RT: add_mcp_server(mcp_server_name)
        RT->>SRV: stdio 安装 / SSE 建连
        RT-->>RES: 工具列表 + 使用方法
        RES->>CACHE: 写入 (intent → server, tool)
    end
    RES->>RT: use_tool(server, tool, params)
    RT->>SRV: 透明代理转发
    SRV-->>RT: 执行结果
    RT-->>RES: 返回结果
    RES-->>DAG: 节点输出 (进入 DAG 下一条边)
    Note over DAG,SRV: 同会话后续节点命中缓存，直接 use_tool
```

关键性质：

1. **解析幂等**：`add_mcp_server` 按 Server 名幂等，连接池复用，避免重复初始化。
2. **Token 最小化**：模型只接触 MetaSkill DAG 结构与 Router 的少量工具描述，而非全部后端服务的 Schema。
3. **绑定可演进**：更换/升级后端服务只需修改 Nacos 注册信息，MetaSkill 定义不变。

## 7. 关键工程决策

### 7.1 三步链下沉：从 LLM 决策到确定性代码

默认模式下，三步链由模型逐步推理完成，每次消耗三轮 LLM 往返且行为不可复现。建议在 OpenClaw.NET 中实现一个**原生工具**：

```
resolve_capability(intent) → binding { server, tool, schema }
```

内部程序化调用 Router 的三个端点，把绑定过程变成确定性代码路径；模型只负责产出 intent 参数。解析失败时再降级回 LLM 逐步模式。同时配合工具输出裁剪（如 TokenJuice 思路），对 search 返回的 Top-5 结果只保留 `name / description / rank`（rank 为候选在上游确定性排序中的位置；上游不返回 score，不做本地伪造）。

### 7.2 注册描述与本体对齐

Router 语义检索的质量完全取决于 Nacos 中 MCP Server 的 `description`。治理要求：

- 注册描述使用与 MetaSkill intent 同一套领域本体词汇（DDD 统一语言在基础设施层的投影）；
- 以 Nacos namespace / group 划分限界上下文（如 `namespace=logistics`、`namespace=geo`），Resolver 检索时按上下文过滤，缩小候选空间。

### 7.3 绑定缓存与失效

| 项 | 策略 |
|---|---|
| 缓存粒度 | 会话级（默认）+ 运行时级（静态绑定） |
| 缓存键 | intent 哈希（task_description + key_words + selectionPolicy） |
| 失效机制 | 订阅 Nacos 配置变更事件；Server 下线/升版时对应 binding 失效，下次执行重新解析 |

### 7.4 版本与准入治理

- MetaSkill 包（SkillKit 产物）对依赖的 Server 声明 `name@version` 约束，与 Nacos 注册的服务版本字段对应；
- 私有/本地 MCP 工具导入须通过 Nacos 3.2.4 的导入允许列表，由管理员预先配置；MetaSkill 侧只允许引用已批准的 Server。

### 7.5 降级与容错（节点级）

| 失败点 | 策略 |
|---|---|
| `search_mcp_server` 返回空 | 路由到 `ms:fallback` 指定节点（如 OpenClaw 原生 web 搜索工具） |
| `add_mcp_server` 失败 | 自动尝试 Top-5 中的下一个候选 |
| `use_tool` 执行错误 | 节点级重试 + 熔断，错误记入 DAG 执行轨迹 |

### 7.6 可观测性闭环

利用 OpenClaw.NET 的 trajectory / audit 导出能力，记录每次「intent → 选中 Server → 工具 → 结果」的完整链路。数据离线回流用于：

1. 作为 harness 回归测试的 fixture，保证绑定行为可复现；
2. 反哺优化：统计哪些 intent 经常选错 Server，据此修正 Nacos 注册描述与 MetaSkill 关键词（本体词汇对齐度度量）。

## 8. 落地路径

| 阶段 | 做法 | 适用场景 |
|---|---|---|
| **Agent 级（PoC）** | Gateway 作为 MCP Client 直连 Router 的 streamableHTTP 端点，Agent 看到 3 个工具，三步链由模型完成；MetaSkill 先全部使用静态绑定 | 一周内跑通验证 |
| **Runtime 级（生产）** | 实现原生 Capability Resolver + 绑定缓存 + 变更事件订阅；MetaSkill 节点支持动态槽位；三步链确定性化 | 正式架构 |

## 9. 附录：最小配置示例

### 9.1 Nacos 注册 MCP Server（片段）

```json
{
  "mcpServers": {
    "weather-mcp": {
      "description": "天气服务：提供城市天气查询、天气预报等工具",
      "command": "npx",
      "args": ["-y", "@example/weather-mcp"],
      "env": { "WEATHER_API_KEY": "***" }
    }
  }
}
```

> 描述字段务必使用与领域本体一致的词汇，这是语义检索命中率的关键。

### 9.2 OpenClaw.NET 侧挂接 Router（streamableHTTP）

```json
{
  "mcpServers": {
    "nacos-mcp-router": {
      "url": "http://127.0.0.1:8000/mcp"
    }
  }
}
```

Router 启动环境变量：

```bash
export NACOS_ADDR=127.0.0.1:8848
export NACOS_USERNAME=nacos
export NACOS_PASSWORD=<password>
export TRANSPORT_TYPE=streamable_http
uvx --with "mcp<2" nacos-mcp-router@latest
```

> `--with "mcp<2"` 为必要约束（2026-09-14 live 验证）：Router 0.2.2 声明 `mcp>=1.9.4` 无上界，mcp 2.x 改名 `streamablehttp_client` 导入导致启动崩溃。

## 10. 总结

一句话概括本架构：

> **Nacos 是能力的注册与治理平面，Router 是能力的动态绑定平面，MetaSkill DAG 是能力的编排平面。**

三者通过「**intent 声明 + 延迟绑定 + 会话缓存**」缝合，同时解决了 Agent 系统的两个典型痛点：工具膨胀带来的 Token 成本，与能力静态硬编码带来的不可演进性。

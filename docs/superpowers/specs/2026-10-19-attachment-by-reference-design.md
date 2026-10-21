# 附件按引用设计（O1）

## 摘要

分两阶段改造多模态附件的传递方式：

1. **ChatTurn 引用化**：会话历史里不再以「文本 marker + 原始 URL/绝对路径」携带媒体，改为结构化的轻量引用（`ChatAttachmentRef`）。内容本身留在媒体缓存里，历史只记 id 与元数据。
2. **provider 侧内联**：在真正调用模型之前，由一个解析器按策略把引用解析成 `AIContent`（`DataContent` / `UriContent`），带字节预算、去重和降级占位符。

目标是三个：停止在每次模型调用时重复搬运同一份媒体；让附件在云端 provider 与 MAF 运行时上真正生效；把「哪些轮次的附件要内联」变成一个显式策略而不是隐式行为。

> **状态（2026-10-20）**：P1（引用化，行为等价）、P2（provider 侧内联）、P3（清理 + 架构守卫）、P4（入站媒体落盘 + 资产 id）已实现。四处对原文的修改都写在对应阶段的「偏差」小节里，主要是**没有建 `IMediaAssetStore`**（四轮下来始终没有调用者）和**P3 没删掉 provider 的 `file:` 支持**（删了会废掉回滚开关）。P4 的遗留项：媒体缓存没有保留策略、缓存根迁移会导致旧引用失效。

## 现状（以代码为准）

### 四层各自的事实

| 层 | 现状 | 位置 |
| --- | --- | --- |
| 接入 | 渠道把媒体转成**一行文本 marker**（`[IMAGE_URL:...]`、`[IMAGE_PATH:...]`、`[IMAGE:telegram:file_id=...]`）拼进消息正文 | `Agent/Plugins/BridgedChannelAdapter.cs:260-301`、`Tools/ImageGenTool.cs:405` |
| 存储 | `ChatTurn` 只有 `Role` / `Content`（字符串）/ `Timestamp` / `ToolCalls`，**没有任何附件字段**；媒体以 marker 文本形式躺在 `Content` 里 | `Core/Models/Session.cs:220-226` |
| 读取 | 每次构建消息都重新 `MediaMarkerProtocol.Extract` 一遍，把 marker 变成 `UriContent`（本地文件转成 `file://` URI） | `Agent/Runtime/AgentPromptContextAssembler.cs:120-172`、`422-470` |
| 内联 | 由**各 provider 客户端各自实现**：Ollama 读文件转 base64（`OllamaNativeClients.cs:364-378`），`EmbeddedLocalChatClient` 转 data URL（`233-273`）；云端 provider 经 MS.Extensions.AI 插件透传 `UriContent`，**没有消费方** | 见左 |

### 由此确认的四个缺陷

**① 同一份媒体在每次模型调用时被重复搬运。**
`BuildMessages` 在每轮开始时从整个历史窗口（`MaxHistoryTurns` 默认 50，`GatewayConfig.cs:257`；AgentProfile 默认 20）重建消息列表，`UriContent` 对象进入 `messages`。而一次用户回合内有一个工具循环（`AgentRuntime.cs:446`，`_maxIterations` 默认 10，见 ctor 参数 `114`），每一圈都把这同一个 `messages` 交给 `IChatClient`，并把工具结果追加进去。于是：

- 一条 5 MB 的附图，在挂载它的那一轮里最多被发送 10 次（每次迭代一次），此后每个回合又被发送一次，直到滑出历史窗口；
- base64 膨胀 4/3 → 每次请求体 ~6.7 MB；
- Ollama 路径每次都 `File.ReadAllBytes`，10 次迭代就是 10 次读盘 + 10 次 base64 编码；
- 如果同一张图被重复提及（转发、重提），历史里就有多个 marker，倍数相乘。

这不是单纯的带宽问题：多数 provider 按「图片出现次数」计费，同一张图出现在同一请求的历史里两次就按两次算，而历史每轮都在重发。

**② 本地附件在云端 provider 上等于静默失效。**
`BuildTurnContents` 对 `IMAGE_PATH` / `FILE_PATH` 生成 `new Uri(Path.GetFullPath(value))`，也就是 `file:///C:/...`。只有 Ollama 与 `EmbeddedLocalChatClient` 会把这个 URI 读成字节；这两者都在 `OpenClaw.Gateway` 里。走 MS.Extensions.AI 插件的云端 provider，本仓库内没有任何代码把 `file://` 转成可发送的 `data:` URL 或上传，URI 只会被 SDK 原样序列化进请求。

> 注意：这条是**从「仓库内无消费方」推断出来的，没有在真实云端 provider 上实测**。落地前应先用一个本地图片附件打一次真实请求确认失败形态（400 还是静默忽略）。但无论哪种形态，结论都成立——附件没有以模型能理解的形式送达。

**③ MAF 运行时根本不解析 marker。**
`MafAgentRuntime.BuildMessages`（`3768-3796`）只用 `turn.Content` 字符串构造 `ChatMessage`，整个文件没有任何 `UriContent` / `DataContent`。所以在 MAF 路径上，`[IMAGE_URL:https://...]` 是以**字面文本**进入 prompt 的——用户发的图，模型从来没见过。这是两个运行时的 parity 缺口，不只是优化点。

**④ 同一段逻辑有两份实现，其中一份是死代码。**
`AgentRuntime.BuildTurnContents`（`3855-3892`）与 `AgentPromptContextAssembler.BuildTurnContents`（`422-470`）内容重复。实际生效的是后者——`AgentRuntime:1114` 把整件事转给了 `_contextAssembler`，而 `AgentRuntime` 那份**全文件没有任何调用点**（`grep` 只命中定义本身）。任何只改 `AgentRuntime` 那份的修复都不会生效，而且不会有任何编译或测试失败来提醒你。

## 目标

- 会话历史只保存媒体引用与元数据，不保存 URI/路径，也不保存字节。
- 内联行为集中到一个解析器，`AgentRuntime` 与 `MafAgentRuntime` 共用同一套语义。
- 内联有**显式策略**：哪些轮次内联、字节上限、附件数上限、超限降级为什么文本。
- 云端 provider 拿到的是它认识的形式（`data:` URL 或 provider 侧可访问的 URL），不再收到 `file://`。
- 旧会话（`Content` 里带 marker）继续可用，不需要迁移脚本。

## 非目标

- 不做媒体内容寻址（content-addressed）重构；`MediaCacheStore` 现有的随机 id 够用，去重只在**单次请求内**按摘要做。
- 不做附件的服务端托管 URL（把本地文件暴露成 HTTP 端点给 provider 抓取）——那需要公网可达性和鉴权，超出本设计范围。
- 不改渠道侧的 marker 协议：marker 仍是**线格式**（跨进程、跨插件、跨语言），只是不再落进 `ChatTurn.Content`。
- 不把工具产生的媒体 marker（`[IMAGE_PATH:...]`、`[FILE_PATH:...]`）纳入引用化。它们由工具在执行期产生、由渠道适配器消费，生命周期完全不同（见「决策 D-5」）。

## 架构

三层，职责单向：

```
渠道 / 工具
   │  产出 marker 文本（线格式，不变）
   ▼
[1] AttachmentReferenceExtractor
   │  写入期跑一次：marker → ChatAttachmentRef；Content 只剩纯文本
   ▼
ChatTurn { Content: "纯文本", Attachments: [ref, ...] }      ← 会话历史
   │
   ▼
[2] ChatMessageComposer
   │  每次构建消息时：ref → AIContent（按策略）
   ▼
[3] AttachmentInliner
   │  解析字节（缓存命中则 0 IO）、按预算裁量、失败降级
   ▼
IChatClient（Ollama / EmbeddedLocal / MS.Extensions.AI 云端 provider）
```

关键点：**内联在 [3] 收敛**，不再由各 provider 客户端各自实现。provider 客户端只保留「把 `DataContent` / `UriContent` 变成自家 JSON」的能力——那是格式适配，不是策略。

## 组件

### `Core/Models/AttachmentReferences.cs` — `ChatAttachmentRef`（P1 已实现形态）

```csharp
public sealed record ChatAttachmentRef
{
    public required MediaMarkerKind Kind { get; init; }   // 原 marker 的种类，决定读取期如何还原
    public required string Value { get; init; }            // 原 marker 的载荷：绝对路径 / URL / 渠道 file_id
    public string? MimeType { get; init; }                 // 由 Kind 推导（"image/*" …），不嗅探
    public string? FileName { get; init; }                 // 占位符与运维输出用
    public long SizeBytes { get; init; }                   // 0 = 未知；P1 不读盘，恒为 0
    public string? Digest { get; init; }                   // P2 起填，单次请求内去重用
    public string? AssetId { get; init; }                  // P2 起填（落媒体缓存后）
}
```

与最初设计的差别：`AssetId` 是**可空**的，并且额外保留了 `Kind` + `Value`（原文写的是 `MediaType` + `SourceUri` + 必填 `AssetId`）。原因是 P1 不落缓存——此时没有任何 `AssetId` 可填，而「逐字节等价」要求读取期能重建原来的 `UriContent`/`TextContent`，唯一能保证这点的就是原样保留 marker 的 kind 与 value。`MediaType`（`"image"`）被 `Kind` 覆盖，没有保留。

`SizeBytes` 与 `MimeType` 仍然必须自描述：P2 的预算判断要在 IO 之前完成，否则判断一份 40 MB 的视频要不要内联就得先把 40 MB 读进内存。

### `Core/Models/Session.cs` — `ChatTurn` 增加字段

```csharp
public sealed record ChatTurn
{
    public required string Role { get; init; }
    public required string Content { get; init; }
    public DateTimeOffset Timestamp { get; init; } = DateTimeOffset.UtcNow;
    public List<ToolInvocation>? ToolCalls { get; init; }

    /// <summary>本轮的附件引用。Null 表示没有附件（旧数据反序列化后也是 null）。</summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public List<ChatAttachmentRef>? Attachments { get; init; }
}
```

`ChatTurn` 已注册在 `CoreJsonContext`，加属性不需要改源生成配置。`SessionBranch.History` 直接复用 `ChatTurn`，分支自带引用。

`null` 与空列表在这个字段上**语义不同**：`null` = 「没有附件，或者是引用化之前的旧回合」，读取期走 marker 解析；空列表 = 「引用化之后的回合，确实没有附件」。`Extract` 因此返回 `null` 而不是空列表——否则旧会话与空附件回合无法区分，读取路径会在没有内容可渲染时走进引用分支。

### `Core/Abstractions/IMediaAssetStore.cs`（P4，未实现）

```csharp
public interface IMediaAssetStore
{
    Task<StoredMediaAsset?> FindAsync(string assetId, CancellationToken ct);
}
```

`MediaCacheStore` 现在是 `internal sealed` 且在 `OpenClaw.Gateway`（`MediaCacheStore.cs:6`）；`AttachmentInliner` 必须能同时被 `OpenClaw.Agent` 和 `OpenClaw.MicrosoftAgentFrameworkAdapter` 使用，所以抽象放 Core、实现留在 Gateway、DI 注册时挂上。找不到资产（缓存被清、目录迁移）时返回 null，内联器降级为占位符而不是抛。

**P1、P2 都不实现这一层。** 判据不是「阶段名」，而是「有没有写入方」：只要没有任何代码给 `ChatAttachmentRef.AssetId` 赋值，`FindAsync` 就永远拿不到参数。P2 的内联走 `Value`（原路径/URL），P4 才让渠道在落缓存时回填 `AssetId`。详见「分阶段」里的偏差 1。

### `Core/Models/AttachmentReferences.cs` — 提取与渲染原语（P1 已实现）

```csharp
public static (string Content, List<ChatAttachmentRef>? Attachments) Extract(string content);
public static ChatTurn CreateUserTurn(string message);
public static string RenderMarkerText(ChatTurn turn);   // 给只能发文本的消费方
public static string ToMarkerLine(ChatAttachmentRef attachment);
```

全部是纯函数——没有 IO、没有时间、没有环境状态。这是刻意的：P1 的全部主张是「引用路径与 marker 路径等价」，只有两侧都无副作用，这个主张才是**可测**的。

`MediaMarkerProtocol.Format` 同步补上（解析的逆函数），否则 `RenderMarkerText` 无法把结构化引用还原成线格式。接线期不落缓存，也没有 `IMediaAssetWriter`——原文设想的「写入期把字节写进缓存」被推到 P2。

### `Agent/Runtime/AttachmentInlinePolicy.cs`

纯数据 + 纯函数，便于单测（P2 已实现，实际形态）：

```csharp
public sealed record AttachmentInlinePolicy
{
    public static AttachmentInlinePolicy Default { get; }   // 文档默认值
    public static AttachmentInlinePolicy Legacy { get; }    // ReferenceMode = false, 窗口 = 0

    public bool ReferenceMode { get; init; } = true;
    public int InlineRecentUserTurns { get; init; } = 2;
    public long MaxInlineBytesPerRequest { get; init; } = 20 * 1024 * 1024;
    public int MaxInlineAttachmentsPerTurn { get; init; } = 4;
    public bool DeduplicateBySource { get; init; } = true;
    public string FailureMode { get; init; } = "placeholder"; // placeholder | fail

    public bool FailOnUnavailable { get; }
    public static AttachmentInlinePolicy FromConfig(AttachmentConfig? config);  // 越界值 clamp
}
```

`ReferenceMode` 也进了这里：读写两侧都要看它，让它跟着策略走比让两个运行时各读一次配置更不容易漏。

`AttachmentInlineBudget` 与它同文件：跨轮共享、有状态的额度（`RemainingBytes` / `IsSourceSeen` / `TryConsumeBytes` / `ReleaseBytes` / `Count`）。做成独立可变对象而不是往 `InlineAsync` 里传 `ref long`，是因为「哪一轮先花」这件事必须显式：composer 用同一个实例倒序遍历，顺序就是策略。

### `Agent/Runtime/AttachmentInliner.cs`

```csharp
ValueTask<IList<AIContent>> InlineAsync(
    IReadOnlyList<ChatAttachmentRef> attachments,
    bool inWindow,                     // 由 composer 算好，内联器不碰历史
    AttachmentInlineBudget budget,     // 跨轮共享的可变预算
    CancellationToken ct);
```

职责只有三件：判断该 ref 本轮是否内联 → 取字节 → 生成 `DataContent`/`UriContent`；不满足条件就生成占位文本。**不碰角色、不碰顺序、不遍历历史**，那些属于 composer。原文签名里的 `turnIndex` / `latestUserTurnIndex` 被去掉——内联器只需要一个布尔量，传索引会诱使它自己算窗口，那就又出现两处窗口定义了。

读取面抽成 `IAttachmentByteSource`（`GetSizeAsync` / `ReadAsync`），默认实现是文件系统。抽它的唯一目的是让「没有读盘」这件事可断言。

### `Agent/Runtime/ChatMessageComposer.cs`

把原来两份消息构建逻辑合成一份，供两个运行时共用（P2 已实现，实际形态）：

```csharp
ValueTask<List<ChatMessage>> ComposeHistoryAsync(
    Session session, int maxHistoryTurns, bool exactLatestToolBatch, CancellationToken ct);
```

差异于原文的两点，都是刻意的：

- **不含 system message**。两个运行时的前言来源不同（`AgentRuntime` 有路由指令与技能索引，MAF 由 agent 自己带），composer 只管历史，前言留在各自手里。
- **异步**。内联要读盘。原文写的是同步签名，真做的时候发现那会逼出 `File.ReadAllBytes`。

`AgentPromptContextAssembler.BuildMessages` 改为 `BuildMessagesAsync`（前缀 + `ComposeHistoryAsync`），`AgentRuntime.BuildTurnContents`（死代码）在 P1 已删，`MafAgentRuntime.BuildMessages` 改为转调 composer，**这是修掉缺陷 ③ 的地方**。`exactLatestToolBatch` 由 MAF 侧传 `false`：它不走 checkpoint 恢复，传 true 等于给它加一条从未走过的路径。

## 数据流

### 写入路径（每轮一次）

1. ~~渠道把媒体落缓存~~ —— **P2 未做，见偏差 1**。渠道照旧产出 marker，`Value` 就是原路径/URL。
2. 渠道照旧拼 marker 行（线格式不变，因为插件与外部进程还在用）。
3. `AgentRuntime` 在 `session.History.Add(...)` 之前（`377`、`744`）调 `AttachmentReferences.CreateUserTurn`：marker 行被摘出，`Content` 只剩纯文本，`Attachments` 拿到 ref 列表；`ReferenceMode = false` 时原样不摘，作为回滚开关的写入半边。`MafAgentRuntime` 同（`353`、`618`）。
4. `PrepareAudienceTurn`（`OpenClawToolExecutor.cs:142`）的 `AllowAttachments` 判断同时看 marker 和 refs——否则引用化之后这条策略会静默失效。

写入期**不读字节、不算摘要**：摘要按需在第一次内联时算并写回 ref（ref 是 record，写回意味着重建；实现上把摘要放进内联器的进程内缓存，不落库）。这样写入路径没有额外 IO。

### 读取路径（每次 provider 调用之前）

```
Composer 遍历历史窗口
  ├─ turn.Attachments == null  → 直接 BuildTurnContents(legacy)   ← 缺陷 ④ 的兼容点
  └─ turn.Attachments != null  → Inliner.InlineAsync(...)
        ├─ 命中内联窗口 + 预算够 + 未超条数 → DataContent(字节) 或 UriContent(远端)
        ├─ 超预算 / 超条数 / 不在窗口       → TextContent("[attachment not sent (over-byte-budget): cat.jpg, 3 MB]")
        └─ 尺寸探测或读取失败               → TextContent("[attachment not sent (unavailable): cat.jpg]")
```

占位符文案用英文，与仓库其余面向模型/运维的字符串一致（原文示例写的是中文）。三种情况共用同一个格式，第二个字段携带**命中的那条上限**，格式为
`[attachment not sent (<reason>): <fileName>, <size>]`。

### 内联决策（对每个 ref，按顺序短路）

0. **不是可内联的媒体**（渠道 file_id 这类相对值、无法解析的值）→ 完全绕过策略。它们既没有字节可读也不产生成本，`RawText` 一档渲染成 `TextContent(value)`、`Omitted` 一档什么都不发，都与改造前逐字一致。把这类引用也换成占位符只会让 prompt 少信息、并不会省钱。
1. 该轮是否落在 `InlineRecentUserTurns` 窗口内（窗口按**用户轮次**倒序计数，不是按历史条目计数——`[tool_use]` 与 assistant 轮不占窗口）。
2. 本轮**已内联**的附件数是否已达 `MaxInlineAttachmentsPerTurn`。计的是已内联数而不是索引：前一张因为超预算被降级时，后面的附件仍然可以占用这个名额。
3. 来源是否在本请求已出现过（去重）。
4. `SizeBytes`（或尺寸探测结果）是否会让累计值超 `MaxInlineBytesPerRequest`。
5. 任一条不满足 → 占位符；全部满足 → 取字节并内联。

**尺寸在读取之前拿。** 判定顺序里「超预算」排在「读字节」之前不是巧合：否则判断一份 30 MB 的视频要不要内联，就得先把 30 MB 读进内存。测试用替身计数 `ReadAsync` 调用次数来锁住这一点。

**读失败要把预算还回去。** 尺寸探测之后、读取之前文件可能被删掉；此时已扣的额度必须归还，否则下一张附件会为一张不存在的文件买单。

**预算分配顺序：从最新用户轮向旧分配。** 窗口是 2 轮，20 MB 预算仍可能装不下两轮的图（例如两张 15 MB 的图）。此时保住最新一轮，旧轮降级为占位符——反过来的话，模型会拿到「上一轮的图」而看不到「这一轮用户刚发的问题」。实现上 composer 先按轮次倒序排一遍再走判定，而不是按历史正序。

**降级粒度是单个附件，不是整轮。** 一轮 3 张图只装得下 2 张时，内联 2 张、第 3 张占位；不要因为一张放不下就把整轮的图都丢掉，也不要让同一轮出现「按文件大小排序后选择性内联」这种不可预测的行为——固定按附件出现顺序。

**降级与失败是两件事。** 窗口、条数、去重、字节预算命中是**策略的预期结果**，永远降级成占位符；只有「本该能读却读不到」（文件被删、权限不足）才受 `FailureMode` 支配。否则一个 30 MB 的图就能让 `FailureMode = fail` 的部署整轮失败——那是配置失误，不是文件损坏。

## 策略与默认值

| 项 | 默认 | 理由 |
| --- | --- | --- |
| `InlineRecentUserTurns` | **2** | 「再仔细看看刚才那张图」是最常见的跨轮指代，它恰好只需要上一轮，所以 2 是能覆盖真实用法的最小值。设为 1 会在第二轮就丢掉上一轮的图；设为 3 只多覆盖「上上轮」，但单次请求的图片计费上限从 2 份涨到 3 份，性价比明显变差。 |
| `MaxInlineBytesPerRequest` | 20 MB | 与 `AudioTranscriptionConfig.MaxAudioBytes`（25 MB）同量级，低于多数 provider 的 20–32 MB 请求体上限。**这是真正的成本天花板上限**——窗口决定「最多能有几轮竞争预算」，预算决定「实际发出去多少字节」。远端 URL 不计入（字节不过本进程），`data:` URI 按编码后长度计入。 |
| `MaxInlineAttachmentsPerTurn` | 4 | 一条消息 4 张图是常见的上限；再多就没有模型能对齐。 |
| `DeduplicateBySource` | true | 同一来源在窗口内出现两次只内联一次。**不叫 `DeduplicateByDigest`**：算摘要要先读字节，而那正是预算要避免的动作，零 IO 的按来源判重更符合成本目标。代价是同一文件的两个副本不会被判重。 |
| `FailureMode` | `placeholder` | 附件取不到不该让整轮失败；显式 `fail` 留给需要严格性的部署。只作用于「读不到」，不作用于「按策略不发」。 |

## 配置

`appsettings.json` 追加（挂在 `Multimodal` 节下，与现有媒体配置同处）。**注意：本仓库的 `appsettings.json` 目前没有任何 `Multimodal` 节，整个多模态配置都走代码默认值**，所以这一段是「需要调/需要回滚时才加」的覆盖点，不是必须落盘的配置。默认值即下表的值。

```jsonc
{
  "OpenClaw": {
    "Multimodal": {
      "Attachments": {
        "ReferenceMode": true,          // false = 退回旧行为（marker 留在 Content 里）
        "InlineRecentUserTurns": 2,
        "MaxInlineBytesPerRequest": 20971520,
        "MaxInlineAttachmentsPerTurn": 4,
        "DeduplicateBySource": true,
        "FailureMode": "placeholder"    // placeholder | fail
      }
    }
  }
}
```

`ReferenceMode` 是**总开关**，存在的意义是回滚：引用化会改变会话持久化格式，出问题时必须能一行配置退回去（旧格式会话在新代码里天然可读，反向不成立）。

## 兼容性

- **旧会话**：`Attachments` 为 null，`Content` 里带 marker → composer 走 legacy 分支，行为与今天完全一致（包括那份「云端 provider 收到 `file://`」的旧缺陷）。不做数据迁移。
- **新会话 + 旧代码**：`Attachments` 是未知属性，`CoreJsonContext` 反序列化时忽略；`Content` 已是纯文本、没有 marker，于是附件在旧代码里「丢失但不报错」。**这是不可逆的一次性代价**，也是 `ReferenceMode` 必须能整体关闭的原因（该开关属于 P2）。
- **`AudiencePolicy.AllowAttachments`**：**实现时确认不需要改判断**。四个调用点（`AgentRuntime:346/700`、`MafAgentRuntime:306/564`）都跑在 `CreateUserTurn` 之前，拿到的还是带 marker 的原始入站消息，所以 marker 计数照旧生效。但这个顺序是隐式契约，一旦有人把门禁挪到抽取之后，策略会**静默失效**——已在 `OpenClawToolExecutor.PrepareAudienceTurn` 里写明这条依赖。
- **历史压缩**：`CompactHistoryAsync` 把回合压成一行文本，原先靠 `Content` 里的 marker 才能看出「这一轮有图」。引用化之后 `Content` 是纯文本，所以压缩时补一句 `[N attachment(s) omitted]`（`AttachmentReferences.SummarizationNote`，两个运行时共用）。这比改造前更好一点：以前摘要里会漏出真实的媒体 URL，现在只保留「有附件」这个事实。被摘要掉的那一轮引用本身随之消失，这是可接受的。
- **`TrimHistory`**：按轮次淘汰，引用随轮次一起走，不额外处理。
- **`session_history` 技能输入**：meta-run 路由规划把 `session.History` 整段序列化成 JSON 喂给技能管线（`AgentRuntime:1492`、`MafAgentRuntime:1254`，`CoreJsonContext.Default.ListChatTurn`）。**已复核（2026-10-20）**，结论是安全的：
  - 唯一的消费者不是模板而是脚本——`meta-skill-creator` 把 `inputs.session_history` 原样塞进 `skill_exec_stdin`，由 `skills/history-explorer/scripts/explore.py` 解析。该脚本只读 `turn["role"]` 与 `turn["content"]`，`content` 用于子串匹配与 200 字预览，**不解析任何 marker**；新增的 `attachments` 字段对它是不认识的键，直接忽略。
  - 三个根都查过：仓库内 `src/OpenClaw.Gateway/skills`（11 个技能）、部署的托管根 `~/.openclaw/skills`（1 个：`it-contract-review`）、`extensions`（空）。workspace 根配的是容器路径 `/home/node/.openclaw/workspace`，本机不存在、无内容。全部没有按 marker 判断的逻辑。
  - 侧向收益：预览文本以前会把 `[IMAGE_URL:…]` 连同真实 URL 一起打进技能输出，现在只剩纯文本。
  - 已固化为格式级测试 `Persisted_history_stays_readable_for_role_and_content_consumers`：断言 `role`/`content` 仍在、`content` 内无 marker、`attachments` 结构可读。**它守不住第三方技能自己的判断逻辑**，只能保证格式层面的加法约定。
  - 一个附带事实：`CoreJsonContext` 没有字符串枚举转换器，`attachments[].kind` 落盘为**枚举序号**。外部脚本读它时不能假设跨版本稳定（重排 `MediaMarkerKind` 会改变含义）——需要用就按 `value`/`assetId`，或先补 `JsonStringEnumConverter`。

## 测试计划

P1 已实现（`src/OpenClaw.Tests/AttachmentReferenceTests.cs`）的用例标记为 ✅；其余属于 P2。

| 用例 | 断言 | 阶段 |
| --- | --- | --- |
| 引用化写入 | 用户轮 → `Content` 不含 marker、`Attachments` 非空、`Role == "user"` | ✅ P1 |
| 无 marker 时保持原样 | 无 marker 消息 → `Attachments == null` 且 `Content` 原引用返回 | ✅ P1 |
| 只有附件没有文字 | `Content == ""`、`Attachments.Count == 1` | ✅ P1 |
| marker 往返 | 13 种 `MediaMarkerKind` 全部：`Format → Extract → ToMarkerLine` 复原原行，且重新解析得到同 kind/value | ✅ P1 |
| 逐项等价 | 同一条混合消息，`Build(content, null)` 与 `Build(extract.Content, extract.Attachments)` 产出的 `(类型, 媒体类型, URI/文本)` 序列完全一致 | ✅ P1 |
| 降级等价 | telegram file_id 这类相对值，两条路径都退化成同一个 `TextContent` | ✅ P1 |
| 空回合 | 空内容仍产出一个 `TextContent`（与旧实现一致） | ✅ P1 |
| MAF 文本渲染 | 引用回合渲染成「正文 + marker 行」；无附件回合原样返回 | ✅ P1 |
| JSON 往返 | `CoreJsonContext` 序列化/反序列化保留 `Kind`/`Value`/`MimeType` | ✅ P1 |
| JSON 不膨胀 | 无附件回合的 JSON 里不出现 `Attachments` 字段 | ✅ P1 |
| 旧 JSON 兼容 | 缺该字段的旧 JSON 反序列化后 `Attachments == null` | ✅ P1 |
| 内联窗口 | 4 轮各带图，`InlineRecentUserTurns = 2` → 最新 2 轮内联、后 2 个旧轮占位 | ✅ P2 |
| 窗口边界 | 中间轮只有 assistant 与 `[tool_use]`（无用户轮）时，窗口计数不被它们占用 | ✅ P2 |
| 预算优先保最新 | 2 轮各带一张 15 MB 图、预算 20 MB → 只有较新那张内联；**断言内联的是较新那张** | ✅ P2 |
| 降级粒度 | 一轮 3 张图、预算只够 2 张 → 按附件出现顺序内联前 2 张，第 3 张占位 | ✅ P2 |
| 字节预算 | 单图 30 MB、预算 20 MB → 占位符，且**没有发生文件读取**（测试替身计数） | ✅ P2 |
| 条数上限 | 一轮 6 张图、上限 4 → 前 4 张内联，后 2 张占位 | ✅ P2 |
| 去重 | 同一来源在窗口内出现两次 → 只产生一个 `DataContent`，且只读一次盘 | ✅ P2（按来源，非摘要） |
| 资产缺失 | 尺寸探测返回 null、`FailureMode = placeholder` → 占位符，不抛 | ✅ P2 |
| 资产缺失 + fail | 同上但 `FailureMode = fail` → 抛出 `AttachmentUnavailableException`（带 `Attachment`） | ✅ P2 |
| MAF parity | 同一 fixture 在两个运行时下构建的消息，`AIContent` 种类与顺序一致 | ✅ P2（结构等价，见下） |
| 云端形态 | 本地文件引用内联后不出现 `file://`（断言所有 `UriContent.Uri.Scheme != "file"`） | ✅ P2 |
| 回滚开关 | `ReferenceMode = false` → 新会话也走 legacy 路径，**连 `file://` 一起退化回去** | ✅ P2 |
| 远端不占预算 | `[IMAGE_URL:]` 在 `MaxInlineBytesPerRequest = 1` 下仍然内联为 `UriContent`，且零读盘 | ✅ P2 |
| 占位符文案 | 原因与文件名进正文，大小按已知值格式化 | ✅ P2 |
| Embedded 的 `file:` | `file:` 的 `UriContent` 被落成 `data:` URL，请求体里不出现 `file://` | ✅ P3 |
| Ollama 的 legacy 路径 | 文件引用与 `data:` 引用都进 `images` 数组，内容为裸 base64 | ✅ P3 |
| 架构守卫 ×4 | 唯一渲染点、两个运行时到达 composer、内联器不遍历历史、唯一文件读取点 | ✅ P3 |
| 入站落盘 | 临时文件 → 缓存，marker 改写为缓存路径，原文件保留 | ✅ P4 |
| 落盘幂等 | 已在缓存根下的路径不再复制 | ✅ P4 |
| 落盘降级 | 文件不存在 / 读不到 → marker 原样保留，不失败整轮 | ✅ P4 |
| 落盘保序 | 多 marker + 散落正文时，行序与正文位置不变 | ✅ P4 |
| 资产 id 识别 | 缓存文件名 → `AssetId`；非缓存命名（`media_photo.png`、大写 hex、长度不符）一律不认 | ✅ P4 |
| 占位符带 id | 缓存附件显示 `ref=media_xxx`，临时路径附件不显示 | ✅ P4 |

架构测试：`AttachmentArchitectureTests`（4 条），源码扫描 + 剥注释，覆盖上面「偏差 3」列的四条规则。

## 分阶段

1. **P1 · 引用化**（已实现）：`ChatAttachmentRef` + `ChatTurn.Attachments` + `AttachmentReferences`（Extract / CreateUserTurn / RenderMarkerText / ToMarkerLine）+ `MediaMarkerProtocol.Format`；写入路径改造四处用户轮（`AgentRuntime:377/744`、`MafAgentRuntime:353/618`）；读取路径在 `AgentPromptContextAssembler`（Agent）与 `MafAgentRuntime.BuildMessages`（MAF）各接一条分支；`AttachmentContentRenderer` 承接原来的 marker→`AIContent` 逻辑。**内联行为不变**。
2. **P2 · provider 侧内联**（已实现）：`AttachmentInlinePolicy` + `AttachmentInlineBudget`、`AttachmentInliner`（窗口/预算/去重/降级）、`ChatMessageComposer`（两个运行时共用）、`ReferenceMode` 开关（写入与读取两侧）、`IAttachmentByteSource`（可替换的读取面）。这一阶段止血缺陷 ①②③。
3. **P3 · 清理**（已实现）：把「URI → 字节」收敛到唯一一处 `MediaPayloadAdapter`；补四条架构测试。**没有按原文删掉 provider 的 `file:` 支持**，理由见下面的偏差 1。
4. **P4 · 媒体资产化**（已实现）：`InboundAttachmentPersister` 在 Gateway 边界把入站媒体复制进媒体缓存并改写 marker；`ChatAttachmentRef.AssetId` 从缓存文件名识别，真正有值。**`IMediaAssetStore` 仍未建**，见「P4 与原文的偏差 1」。

P1 单独可发布且行为等价，这是刻意的——格式变更与行为变更不同批次上线，出问题时能立刻定位是哪一类。

### P4 与原文的偏差

原文这一阶段的三个词是「`IMediaAssetStore` + 写入期落缓存 + `AssetId` 有值」。三项里后两项做了，第一项没做，而且落盘点也不在原文设想的位置。

1. **不建 `IMediaAssetStore`，因为它的唯一方法仍然没有调用者。** 原文设想的读取链是 `AssetId → FindAsync → 路径`。但 P4 之后引用自带的 `Value` **就是**缓存里的绝对路径，内联器直接读它即可；插入一层 id→路径解析只会多一次 IO，而且没有任何场景需要它（缓存根搬迁时路径确实会失效，但那一半后来用「文件名 + 当前根重试」解决了，见下面遗留 3——那不需要一个存储层，只需要知道当前根；而文件真的没了的那一半，`FindAsync` 同样拿不到）。所以改成**从文件名反向识别 id**：`MediaCacheStore` 用 `AttachmentReferences.MediaAssetIdPrefix` 生成 id，`Extract` 用同一常量识别 `<prefix><16 hex>` 并把 id 记进 `AssetId`。共享常量让这件事从「巧合」变成契约。

2. **落盘点在 Gateway 边界，不在两个运行时。** 原文把落缓存放在运行时的写入路径。实际实现放在 `GatewayInboundMessageWorker` 派发前的最后一刻，理由是三条：
   - 只有 Gateway 知道缓存根（`Multimodal.MediaCachePath`），运行时拿到的是消息文本；
   - 两个运行时吃的是**同一段**消息文本，一处改写就让自研与 MAF 路径同时得到保证，不必各写一遍；
   - 该位置紧邻已有的 `ResolveFileUrlMarkersAsync`（把 `[FILE_URL:/media/{id}]` 还原成 `[FILE_PATH:…]`），两者是同一类「派发前把 marker 摆正」的动作。

   顺带的收益：**写入路径仍然没有多边形 IO 增加**——只有带路径 marker 的消息才会动磁盘，纯文本消息在 `Contains` 判断处就返回。

3. **`AssetId` 与显示名争同一个槽，显示名让位。** 缓存文件名从 `cat.png` 变成 `media_0123456789abcdef.png`，因此占位符里显示的名字变丑了。原始文件名仍在缓存的 sidecar 元数据里，占位符也带 `ref=`，信息没丢；但这是**明确付出的代价**。替代方案（保留原名 + 读取期按 id 查名字）会让每个占位符变成一次磁盘读。

### P4 遗留的三件事（后续处理结果）

1. **媒体缓存没有保留/清理策略。** 原状：`MediaCacheStore` 从来只管写不管删，P4 只是把入站媒体也写进去，增长变快。这是**既有运维缺口**，不是本阶段引入的。
   **已补**：`MultimodalConfig.MediaCache`（`MaxFileBytes` / `RetentionDays` / `MaxTotalBytes` / `SweepIntervalMinutes`）+ `MediaCacheSweeperService` 周期扫描，先过期后容量，`RetentionDays <= 0` 等于关掉（保留既有部署行为）。`GET /media/cache` 看状态、`POST /media/cache/sweep` 手动跑（运维角色门禁）。
2. **没有单文件大小下限/上限。** 原状：渠道已经下载过一次，这里只是复制；当初的判断是「给复制加一个上限意味着入站视频可能永远进不了缓存」，所以不加。
   **已改判**：上限的价值不在省空间，而在**让超限变成一个可见的、有理由的降级**——没有上限时一个 2 GB 入站视频会安静地吃掉磁盘，并且整轮派发都要先把它读进内存。现在的行为是：超限 → 不写缓存、marker 保留原临时路径、调用方拿到明确原因（上传端点回 413）。也就是原判断担心的「进不了缓存」正是想要的默认，只是不再是静默的。
3. **缓存根是可配置的相对路径**（`./memory/media-cache`），历史里存的是它的绝对路径快照。换部署目录/换机器后，旧引用会失效。
   **已部分补**：读取期 fallback —— `MediaCacheRelocationByteSource` 在记录路径读不到、且文件名符合资产 id 契约（`media_` + 16 位小写 hex）时，只用**文件名**到**当前**缓存根重试。这一半是能救的：把缓存目录整体搬到新位置（换盘、换部署目录、容器改挂载点）之后，旧会话的图自动恢复，不需要改任何历史数据。
   **剩下的是真不可恢复的**：文件跟着旧目录一起没了（换机器且没拷缓存），这时任何间接层都拿不到字节，仍然是 `unavailable` 占位符。「缓存根一旦上线不可迁移」这条**约束仍然成立**——fallback 只覆盖「文件还在、路径变了」，不覆盖「文件没了」。这也是当初判 `IMediaAssetStore` 救不了它的原因。

### P3 与原文的偏差

1. **不删 provider 的 `file:` 分支，改为「只有一处」。** 原文写的是「只处理 `DataContent`，不再接 `file://`」。真做时发现这会**废掉 `ReferenceMode` 这个回滚开关**：关掉 ReferenceMode 就会重新产生 `UriContent(file://)`，此时 provider 若不认 `file:`，本地图在任何 provider 上都不再可用——一个会弄坏东西的回滚开关比没有回滚开关更糟。旧会话（`Attachments == null`）同样依赖这条路径。

   所以真正的判据不是「有没有出现 `file:`」，而是「**有没有第二处在做策略判断**」。P2 之后 provider 客户端已经不判断该不该发、发多少、发几次，只剩机械的「这个 URI 代表哪些字节」；把这份机械转换收敛成一个 `MediaPayloadAdapter`，比删掉它更符合原意。删掉的前置条件是「让 ReferenceMode 也走内联器」——那是把回滚从「行为」降级为「仅存储格式」，属于另一个决定，不该顺手做。

2. **顺带修掉一个真实缺陷：`EmbeddedLocalChatClient` 会把 `file://` 直接发给本地推理服务。** 它对 `DataContent` 会拼 `data:` URL，对 `UriContent` 却原样转发 URI。本地服务解析不了 `file:///C:/…`，于是 legacy 会话里的本地图在这条路径上从来就没生效过——与缺陷 ② 同类，只是发生在「本地」provider 上，所以之前的排查没把它算进去。现在两条分支都经 `MediaPayloadAdapter`。

3. **架构测试的四条规则**（`AttachmentArchitectureTests`，扫描源码而不是程序集，并**先剥掉注释**）：
   - `AttachmentContentRenderer.Build` 只允许出现在 `ChatMessageComposer`（钉死缺陷 ④ 的复发路径）；
   - 两个运行时都必须到达 composer，且 `AgentRuntime` / `MafAgentRuntime` 里不得出现 `AttachmentContentRenderer.Build` 与 `RenderMarkerText`（钉死缺陷 ③）；
   - `AttachmentInliner` 里不得出现 `History`（窗口只有 composer 能算，内联器只收一个 `inWindow` 布尔）；
   - `src/OpenClaw.Gateway/Extensions` 下只有 `MediaPayloadAdapter` 能 `File.ReadAllBytes(`。

   剥注释不是洁癖：`MafAgentRuntime` 的 XML 注释里**故意写了**它替换掉的 `RenderMarkerText`，不剥注释的守卫会禁止文档记录它自己要防的那个 bug。

### 实现后与原文的三处偏差（P2）

1. **`IMediaAssetStore` 再次推迟（D-4 落空的唯一一项）。** 原文把「Core 接口 + Gateway 实现」放在 P2。但 P2 的内联输入是 `ChatAttachmentRef.Value`（原路径/URL），而 `AssetId` 至今**没有任何写入方**——渠道还没把字节落进媒体缓存并回填 id。此时建一个只有一个空实现的接口，等于把 P1 偏差 2 里刚说过的问题再犯一遍。更关键的是它并不改变行为：接口的消费点 `FindAsync(assetId)` 永远不会被调用。

   **代价必须写清楚**：P2 只能内联「原路径仍然有效」的附件。渠道把临时文件删掉之后，该引用降级为 `unavailable` 占位符——`FailureMode = placeholder` 下不报错，但模型确实看不到那张图。这不是回归（改造前它连内联都没有），但也不是设计目标里「内容留在媒体缓存、历史只记 id」的终态。已单列为 P4。

2. **去重键从「摘要」改成「来源」。** 原文 `DeduplicateByDigest`，实现与配置都叫 `DeduplicateBySource`。算摘要要先读字节，而**读字节正是要避免的动作**：一张 30 MB 的图如果为了让判重先算一次 SHA-256，预算就成了摆设。改为按归一化来源判重（本地路径 `Path.GetFullPath` + Windows 下忽略大小写；远端 `Uri.AbsoluteUri` 区分大小写），零 IO、零内存。代价：同一个文件被复制成两个路径时不会被判重。

3. **架构测试未补，MAF parity 只能做到结构等价。** 原文要求「同一 fixture 在两个运行时下构建的消息一致」。`MafAgentRuntime` 的构造需要 `MafOptions` / `MafAgentFactory` / `MafSessionStateStore` / `MafTelemetryAdapter` 整套 MAF 服务图，为本轮造这个夹具的收益低于它带来的脆弱性。改为：断言 `AgentPromptContextAssembler` 产出的历史部分与 `ChatMessageComposer` 完全一致（`Both_runtimes_compose_history_through_one_implementation`），再靠「MAF 侧已无自建消息逻辑」这个结构性事实兜住。架构测试留到 P3 与「禁止第二处 marker 解析」一起补。

### 实现期发现的两处连带修改

- **Ollama 客户端需要认识 `DataContent`。** 本地图以前以 `file://` 的 `UriContent` 到达，`OllamaNativeClients.TrySerializeImage` 自己读文件转 base64。P2 之后本地图变成 `DataContent`，而那个 `switch` 只有 `UriContent` 分支——不改的话 Ollama 会**静默丢图**。已在 P2 补一个 `DataContent` 分支（字节本来就是想要的 base64），而不是把「读文件」留在策略层。P3 再把 `TrySerializeImage` 里的文件读取删掉。
- **`data:` URI 也吃字节预算。** `[IMAGE_URL:data:image/png;base64,...]` 的字节已经内联在 marker 里，不占文件读取、也就绕过了「本地字节上限」。这类引用仍按原样发 `UriContent`（不改行为），但把**编码后长度**记进预算，否则「上限」这个词就是假的。

### 实现后与原文的三处偏差（P1）

1. **死代码删除从 P3 提前到 P1。** 原文把「删除 `AgentRuntime.BuildTurnContents`」放在 P3，但它本来就零调用点，而 P1 又引入了 `AttachmentContentRenderer`——不删的话仓库里会同时存在「新增的生效版」和「旧的死版」，正是缺陷 ④ 描述的那种陷阱的加强版。已删，两个文件现在都搜不到 `BuildTurnContents`。
2. **`IMediaAssetStore`/`IMediaAssetWriter` 推迟到 P2。** P1 不落缓存（`AssetId` 恒为 null），此时建接口等于造一个没有调用者的抽象。连带 `Extract` 也去掉了原文里的 `IMediaAssetWriter` 参数，成为纯函数——这反而让「等价」可测：两侧都无 IO、无时间、无环境状态。
3. **`ChatAttachmentRef` 增加 `Kind` + `Value`，`AssetId` 改为可空。** 原文假设 ref 指向媒体缓存，但 P1 没有缓存可指；要逐字节重建原来的 `UriContent`/`TextContent`，唯一可靠的做法是原样保留 marker 的 kind 与 payload。`MediaType`（`"image"`）被 `Kind` 覆盖，没有保留。

另外一处不是偏差、是发现：**持久化会话 JSON 是 camelCase**（`CoreJsonContext` 设了 `PropertyNamingPolicy = CamelCase`），并且已经全局 `DefaultIgnoreCondition = WhenWritingNull`。所以 `ChatTurn.Attachments` 上**不需要** `[JsonIgnore]`——加了只是重复全局配置，还会让人以为空值抑制是属性级行为。已去掉，改在 XML 注释里指向 `CoreJsonContext`。这是写测试时才暴露的：第一版测试用 PascalCase 的旧 JSON 反序列化，直接报缺 `role`/`content`。

## 决策

| # | 决策 | 结论 | 理由 / 备选 |
| --- | --- | --- | --- |
| D-1 | ref 是否自描述（带 mime/size/digest） | **是**（已定） | 预算判断必须能在 IO 之前做。备选：只存 `AssetId`，读元数据再判断——多一次 IO，且 40 MB 视频要先读进来才知道该不该读 |
| D-2 | 默认内联窗口 | **最近 2 个用户轮**（已定） | 「再仔细看看刚才那张图」只需要上一轮，2 是覆盖真实用法的最小值。设为 1 会在第二轮就丢掉上一轮的图；设为 3 只多覆盖「上上轮」，而单次请求的图片计费上限从 2 份涨到 3 份。成本天花板仍由 `MaxInlineBytesPerRequest` 兜住。**显式代价**：provider 按图片出现次数计费，窗口 2 表示同一请求最多带 2 轮的图，单次请求的图片计费上限约为窗口 1 的 2 倍 |
| D-3 | 历史附件怎么让模型「重看」 | **P2 不做**（已定） | 占位符带 `ref` 即可。备选：加 `load_attachment` 工具——工具面膨胀，且要再走一次鉴权 |
| D-4 | `IMediaAssetStore` 放哪层 | **Core 接口 + Gateway 实现**（结论未变，**推迟到 P4**） | 层位结论仍然成立。推迟的理由是写入方不存在：`AssetId` 至今没有赋值点，`FindAsync` 永远拿不到参数，接口会是一个没有调用者的抽象。代价是 P2 只能内联「原路径仍有效」的附件，见「偏差 1」 |
| D-5 | 工具产生的 `[IMAGE_PATH:]` 是否纳入 | **P1 不纳入**（已定） | 工具产物由渠道消费，生命周期不同，混进来会引出来源不明的资产。备选：一并引用化 |
| D-6 | 旧会话是否迁移 | **不迁移**，读取期 fallback（已定） | 备选：一次性迁移脚本——要为 50 轮 × 全量会话重写 JSON，风险与收益不成比例 |

D-2 已定，因此 `InlineRecentUserTurns` 的默认值（2）、配置示例、窗口测试与「预算优先保最新」的分配顺序是一组不可拆的东西：窗口从 1 变成 2 才引入了「预算可能装不下整个窗口」这个状态（两张 15 MB 的图就够触发），这也正是分配顺序必须显式规定、而不能靠遍历顺序碰巧对的原因。

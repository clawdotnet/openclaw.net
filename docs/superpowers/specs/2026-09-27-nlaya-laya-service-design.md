# NLaya 本地 Laya 服务迁移设计

## 状态

设计内容已在对话中确认；实现规划前，等待用户审阅本文档。

## 目标

将 `tools/laya_service` 下的 Python 实现和工具替换为独立的 .NET 10 CLI，使用固定版本的 NuGet 包 `NLaya` 1.0.0。该工具负责本地推理、checkpoint 下载与校验、评估、校准以及 provider-neutral 路由报告。这些工作流不得依赖 Python、pip 或 Hugging Face Python CLI。

保留 OpenClaw Gateway 当前使用的本地 HTTP 边界。Gateway 不直接加载 NLaya 或 TorchSharp。保留现有模型 revision 标识、安全策略以及默认禁用路由的行为。不得根据 parity 或合成测试宣称路由质量已达到生产要求。

## 非目标

- 不把 NLaya 集成进 Gateway，也不更改 Jev/ONNX 提供方选择。
- 不修改模型权重、不自动执行校准，也不默认启用 active Laya 路由。
- 不移除无关 Python 工具，也不要求整个仓库去 Python 化。
- 未成功完成发布和运行验证前，不宣称 TorchSharp 服务支持 NativeAOT。
- 不在新 runtime 下静默接受现有 Python 校准产物。

## 组件与依赖

在原路径 `tools/laya_service` 建立 .NET 10 可执行项目，替换该目录中的 Python 包。引用 `NLaya` 1.0.0 和匹配版本的 `NLaya.TorchSharp` 包。必须包含 TorchSharp CPU runtime；只有在受支持且显式选择的目标平台中才包含加速器 runtime 包。独立服务拥有这些依赖；Gateway 继续使用现有 HTTP client。

提供 `serve`、`download`、`evaluate`、`calibrate` 和 `report` CLI 命令。在可行时保留现有选项名称和含义：

- `serve`：manifest、校准文件、端口、设备、checkpoint 和线程数限制。
- `download`：目标目录、不可变模型 revision 和一个或多个 checkpoint。
- `evaluate`：case JSONL、本机 loopback endpoint 和 observations 输出路径。
- `calibrate`：互不重叠的原始拟合与验证 observations，以及输出产物路径。
- `report`：路由日志、可选标签、JSON 输出，以及可选 reliability/risk-coverage 图表。

Python 模块调用方式将由 .NET 可执行文件或 `dotnet run --project tools/laya_service -- <command> ...` 取代。相应更新所有运维示例。

## 下载与推理数据流

`download` 命令使用 .NET HTTP API，从 Hugging Face 仓库 `convaiinnovations/laya` 获取 NLaya 所需文件，并固定到 40 位 commit。不得调用 `hf` 或 Python，也不得使用未固定的分支。使用前校验每个文件的 SHA-256；下载内容先写入临时文件；manifest 以原子方式写入，记录 manifest 版本、revision、checkpoint 路径、文件哈希和 NLaya 版本。上游模型许可和归属说明与下载资产放在一起。不同 revision 使用不同目标目录。

保留当前模型 revision 作为初始候选。切换门槛之一是针对每个受支持 checkpoint 完成真实模型 smoke test；若该 revision 与 NLaya 不兼容，只有同时更新固定版本和哈希，并取得通过的 parity 证据后才能更改。CI 使用模拟 HTTP 测试，不下载模型权重。

启动时，`serve` 校验 manifest 和全部资产哈希，验证 runtime/package 与校准身份，加载并 warm-up 配置的 checkpoint，之后才绑定 `127.0.0.1`。推理过程离线运行，只加载本地模型路径。自动路由可选择 English 和 multilingual checkpoint；`typed-decisions` 必须显式指定，绝不能根据任务名称自动选择。设备必须显式选择；若对应 runtime 或硬件不可用则失败，不得静默退回 CPU。

保留 `GET /health`、`POST /v1/decisions`、请求字段、模型标识 `laya@<revision>`、typed-answer 响应结构及现有 loopback 保护。服务将 NLaya 结果适配为现有 wire response。保留 metadata 字段名，将 `sdk_version` 设为 `1.0.0`，并新增 `runtime: "NLaya"`；更新 `LayaDecisionClient`，使其与现有 revision、checkpoint、schema、rubric、device、truncation 和 calibration 校验一起验证这两个值。对于超过已验证 tokenizer token 预算的请求，服务必须拒绝而不是截断；只有完成检查后才能报告 `truncated: false`。

使用 golden cases 验证问题顺序、规范化 schema hash、checkpoint 选择（包括非拉丁文字和混合文字脚本）、答案映射及概率容差。未知或不支持的输入一律 fail closed。

## 评估与校准

.NET `evaluate` 命令读取现有带标签 case JSONL 格式，校验 case ID 唯一性和标签，并且只向字面值为 loopback 地址的 `/v1/decisions` endpoint 发送请求，不使用代理或跟随重定向。输出 raw observations，包含 case ID、规范化请求指纹、问题 ID、模型、checkpoint、schema hash、runtime 版本、原始答案和标签。observations 中绝不写入 state 或 prompt 文本。

.NET `calibrate` 命令分别读取拟合和 held-out observation 文件。拒绝重复或重叠的 ID、ID 不同但请求指纹相同的数据、混合的 model/schema/runtime 身份、缺失的 checkpoint/type/option-count 分桶、样本不足以及缺少多个标签的分桶。保留现有概率温度缩放行为和最小样本策略。报告 raw 与 calibrated 预测的 accuracy、NLL、Brier score、ECE、reliability bins 和 risk/coverage。校准只调整概率，不重新训练权重，也不启用提供方。

校准产物使用格式版本 2。每个产物绑定模型 revision、question schema、NLaya 版本、按 checkpoint 分桶的温度以及 held-out 指标。产物序列化为确定性的 UTF-8 JSON，并以换行符结尾；calibration ID 是对产物精确字节计算的 SHA-256。服务只接受匹配的 v2 产物；active routing 仍要求配置该产物的 SHA-256。对 v1 Python 生成的校准产物明确拒绝，并提示重新生成；不随代码发布生产校准产物。

将 provider-neutral 决策日志报告迁移到 .NET CLI。为 Jev 和 Laya 保留现有 JSON 字段及报告行为：摘要计数、延迟/成本、提案覆盖率/差异、可选的人工 tier 标签质量，以及按 provider/model/rubric/checkpoint/revision/calibration/schema 分组的 raw probability calibration 指标。使用固定版本的 .NET 绘图库保留可选 reliability 和 risk/coverage 图。移除导入 `tools.laya_service.calibration` 的 Python 报告入口，并同步更新文档和测试，改用 .NET 命令。

## 错误处理与安全

manifest、资产摘要、package/runtime 身份、checkpoint 或 calibration 无效时，服务须在开放 socket 前启动失败。服务仅绑定 loopback；拒绝非预期 Host 和任何 Origin；拒绝重定向、chunked 或超大请求体；限制连接数和并发推理；不记录请求或请求体日志。无效请求返回 422 和固定 reason code；过载与推理失败返回 503 和固定的非敏感错误。绝不回显 state、header、凭据或依赖异常原文。

下载时验证路径始终位于目标目录内，并原子替换资产/manifest。Runtime 不下载模型。校准和评估输出不含 state 或凭据。服务错误时保留 Gateway 现有 baseline fallback 行为，不新增 hosted fallback。

## 测试与验证

- 将现有 protocol、manifest、calibration 和 report 测试迁移为聚焦的 .NET 测试；保留 canonical hash 与报告结构的 golden assertions。
- 使用 fake predictor 测试服务 HTTP 安全和限制，包括无效 JSON、重复 key、禁止的 Host/Origin、超大/chunked 请求体、过载和不回显错误。
- 使用模拟 HTTP 测试 downloader allowlist、固定 revision、摘要不匹配、路径穿越、manifest 原子写入以及许可/归属信息输出。
- 测试 evaluation 隐私/溯源，以及 calibration 数据集划分、分桶、最小样本数、格式版本、稳定产物哈希和 held-out 指标行为。
- 更新 Gateway Laya routing 测试，验证 `sdk_version` 和 `runtime`、calibration 身份以及 fallback 语义不变。
- 对每个发布的 checkpoint 和受支持设备运行可选的真实模型 smoke/parity 测试，并与 NLaya/Python golden fixtures 对比。普通 CI 不下载模型。
- 将 CI 中 Laya 和 routing-report 的 Python 测试步骤替换为 .NET 测试。确保 `tools/laya_service` 的构建、测试和文档命令均不需要 Python。

默认以 JIT .NET 10 可执行程序运行服务。Gateway 的 NativeAOT 构建边界保持不变。未在受支持 TorchSharp 目标上成功发布并运行前，不得宣称服务支持 NativeAOT。

## 文档与迁移

更新 `docs/laya-routing.md`、其中文版、`tools/laya_service/README.md`、routing CLI 指南、CI workflow 以及所有报告命令引用。说明新的 package/runtime 要求、模型下载流程、本机命令格式、v2 校准重生成、设备支持、可选真实模型 smoke，以及 parity 并不能证明生产质量。保留现有 Laya 归属说明和 Apache-2.0 notice。只有在新实现及其测试覆盖到位后，才在实现过程中移除过时的 Python requirements、服务模块和测试。

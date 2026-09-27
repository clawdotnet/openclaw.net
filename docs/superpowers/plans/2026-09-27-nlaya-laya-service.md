# NLaya 本地 Laya 服务迁移实施计划

> **面向 agentic workers：** 实施时必须逐任务执行本计划，并在每个任务后独立验证。可选择 `superpowers:subagent-driven-development`（推荐）或 `superpowers:executing-plans`。步骤使用 checkbox (`- [ ]`) 跟踪。

**目标：** 将 `tools/laya_service` 与 Laya/Jev 路由评估工作流迁为不依赖 Python 的 .NET 10 工具，同时保持 Gateway 当前 loopback HTTP 契约和安全行为。

**架构：** 在 `tools/laya_service` 建立独立 .NET 10 CLI 与测试项目。服务通过 `NLaya`/`NLaya.TorchSharp` 使用本地模型资产，不把推理依赖引入 Gateway；共享 wire protocol、manifest、校准产物和报告逻辑均在 .NET 实现。先迁移并验证新实现，再删除 Python 模块、入口和测试。

**技术栈：** .NET 10、NLaya 1.0.0、NLaya.TorchSharp 1.0.0、TorchSharp-cpu 0.107.0、xUnit v3、ASP.NET Core shared framework、ScottPlot 5.1.59、GitHub Actions。

## 全局约束

- `tools/laya_service` 使用 .NET 10，并固定引用 NuGet 包 `NLaya` 1.0.0。
- 必须包含 TorchSharp CPU runtime；只有在受支持且显式选择的目标平台中才包含加速器 runtime 包。
- Gateway 不直接加载 NLaya 或 TorchSharp；维持 OpenClaw Gateway 当前使用的本地 HTTP 边界。
- 保留 `GET /health`、`POST /v1/decisions`、模型标识 `laya@<revision>`、现有安全策略以及默认禁用路由行为。
- 这些工作流不得依赖 Python、pip 或 Hugging Face Python CLI；不要求整个仓库去 Python 化。
- 模型下载固定到 40 位 Hugging Face commit 并校验逐文件 SHA-256；普通 CI 不下载模型权重。
- 校准产物使用格式版本 2；不得在新 runtime 下静默接受现有 Python 校准产物。
- 不得根据 parity 或合成测试宣称路由质量已达到生产要求。
- 未在受支持 TorchSharp 目标上成功发布并运行前，不得宣称服务支持 NativeAOT。

## 文件结构

- Create `tools/laya_service/LayaService.csproj`：服务可执行项目、NuGet 依赖及 rubric 的嵌入资源。
- Create `tools/laya_service/Program.cs`、`CommandLine.cs`：命令派发、参数解析、退出码和顶层安全错误处理。
- Create `tools/laya_service/Protocol/WireModels.cs`、`StrictJson.cs`、`RequestValidator.cs`：HTTP wire DTO、重复 JSON key 拒绝和请求合同校验。
- Create `tools/laya_service/Hosting/DecisionServer.cs`：loopback HTTP 服务、安全响应、限流和 inference gate。
- Create `tools/laya_service/Hosting/ServiceContracts.cs`、`Inference/ServeOptions.cs`、`Evaluation/Observation.cs`：跨 host、runtime 和 evaluation 共用的 options/observation contract。
- Create `tools/laya_service/Models/ModelManifest.cs`、`HuggingFaceDownloader.cs`：模型 manifest、资产白名单、下载、hash 与原子文件操作。
- Create `tools/laya_service/Inference/NLayaDecisionPredictor.cs`、`CalibrationStore.cs`：NLaya checkpoint/router 生命周期、typed-answer 适配、token budget 校验和校准应用。
- Create `tools/laya_service/Evaluation/CaseEvaluator.cs`、`CalibrationFitter.cs`、`CalibrationMetrics.cs`：JSONL 样本推理、温度拟合与指标。
- Create `tools/laya_service/Reporting/RoutingJournalReport.cs`、`ReliabilityPlot.cs`：Jev/Laya 决策日志报告与可选 PNG 图。
- Create `tools/laya_service/tests/LayaService.Tests.csproj` 及按协议、host、model、evaluation、report 分组的 xUnit 测试。
- Modify `OpenClaw.Net.slnx`：加入工具和测试项目。
- Modify `src/OpenClaw.Routing.Decisions/LayaDecisionClient.cs`、`src/OpenClaw.Tests/LayaRoutingTests.cs`：验证新 runtime metadata，同时保持 Gateway fallback。
- Modify `src/OpenClaw.Routing.Decisions/OpenClaw.Routing.Decisions.csproj` only if the rubric is moved; preferred implementation keeps `tools/laya_service/rubrics/openclaw-laya-tiers-v1.json` at its current path, so no project-file change is needed.
- Modify `.github/workflows/ci.yml`：以 .NET 工具测试替换 Laya 与 routing-report Python 测试步骤。
- Modify `docs/laya-routing.md`、`docs/jev-routing.md`、`docs/cli/routing.md`、`tools/laya_service/README.md`、`docs/README.md`、`docs/SITE_MAP.md`、`docs/zh-CN/SITE_MAP.md`；create `docs/zh-CN/integrations/laya-routing.md`。
- Delete after .NET coverage passes: `tools/laya_service/__init__.py`, `__main__.py`, `calibration.py`, `compat.py`, `download.py`, `evaluate.py`, `protocol.py`, `runtime.py`, `requirements.txt`, `tests/laya-service/test_service.py`, `tests/routing-eval/test_jev_report.py`, `scripts/evaluate-decision-routing.py`, `scripts/evaluate-jev-routing.py`.
- Keep `tools/laya_service/rubrics/**`, `tools/laya_service/licenses/**`, `tools/laya_service/THIRD_PARTY_NOTICES.md`, and routing JSONL sample fixtures; these remain inputs/resources, not Python code.

---

### Task 1：建立 .NET CLI 与测试骨架

**文件：**

- Create: `tools/laya_service/LayaService.csproj`
- Create: `tools/laya_service/Program.cs`
- Create: `tools/laya_service/CommandLine.cs`
- Create: `tools/laya_service/tests/LayaService.Tests.csproj`
- Create: `tools/laya_service/tests/CommandLineTests.cs`
- Modify: `OpenClaw.Net.slnx`

**接口：**

- `CommandLine.Parse(string[] args)` 产出 `CommandInvocation`，其 `Command` 为 `serve|download|evaluate|calibrate|report`，`Options` 保留重复 `--checkpoint` 值并拒绝未知选项。
- `Program` 只解析命令、调用相应 handler、将安全错误写到 stderr 并返回非零退出码；不得输出请求 state、凭据或任意底层异常文本。

- [ ] **步骤 1：先写 CLI 解析失败测试**，覆盖五个命令、重复 checkpoint、缺值参数、未知命令和未知参数。例如：

```csharp
[Fact]
public void Parse_DownloadPreservesRepeatedCheckpoints()
{
    var parsed = CommandLine.Parse(["download", "--destination", "models", "--checkpoint", "english", "--checkpoint", "multilingual"]);
    Assert.Equal("download", parsed.Command);
    Assert.Equal(new[] { "english", "multilingual" }, parsed.Options.GetMany("checkpoint"));
}
```

- [ ] **步骤 2：运行测试确认失败。**

运行：`dotnet test tools/laya_service/tests/LayaService.Tests.csproj -c Release --filter FullyQualifiedName~CommandLineTests`
预期：因项目或 `CommandLine` 尚不存在而失败。

- [ ] **步骤 3：创建项目骨架。** `LayaService.csproj` 设置 `OutputType=Exe`、`TargetFramework=net10.0`、root namespace；引用 `NLaya` 1.0.0、`NLaya.TorchSharp` 1.0.0、`TorchSharp-cpu` 0.107.0、`Microsoft.AspNetCore.App` 和 ScottPlot 5.1.59。测试项目采用 `eng/nacos-live/tests/NacosLiveAcceptance.Tests.csproj` 的 xUnit v3 模式，并引用服务项目。把两个项目放入 `.slnx` 的 `/tools/laya_service/` solution folder。
- [ ] **步骤 4：实现命令解析和顶层派发。** 每个命令 handler 初期可以返回明确的 `not_implemented` 错误；不得留下成功但无动作的空实现。保持 CLI 参数为 `dotnet run --project tools/laya_service -- <command> ...`。
- [ ] **步骤 5：重跑 CLI 测试并构建项目。**

运行：`dotnet test tools/laya_service/tests/LayaService.Tests.csproj -c Release`
预期：CLI 解析测试通过。
运行：`dotnet build tools/laya_service/LayaService.csproj -c Release`
预期：构建成功，warnings-as-errors 下无警告。

- [ ] **步骤 6：提交** `feat: scaffold NLaya local service CLI`。

### Task 2：实现严格 wire protocol 和安全 loopback server

**文件：**

- Create: `tools/laya_service/Protocol/WireModels.cs`
- Create: `tools/laya_service/Protocol/StrictJson.cs`
- Create: `tools/laya_service/Protocol/RequestValidator.cs`
- Create: `tools/laya_service/Hosting/DecisionServer.cs`
- Create: `tools/laya_service/tests/ProtocolTests.cs`
- Create: `tools/laya_service/tests/DecisionServerTests.cs`

**接口：**

- 在 `Hosting/ServiceContracts.cs` 定义 `IDecisionPredictor.PredictAsync(DecisionWireRequest request, CancellationToken cancellationToken)`、`GetHealth()` 和 `ServiceOptions(int Port, int MaxConnections, int MaxRequestBytes, TimeSpan RequestTimeout)`；健康方法返回 health JSON DTO。测试使用 fake predictor。
- `RequestValidator.Validate(DecisionWireRequest request, string configuredModel)` 拒绝无效请求并返回固定 reason code。
- `DecisionServer.Build(IDecisionPredictor predictor, ServiceOptions options)` 返回可由测试启动的 `WebApplication`；生产只监听 `IPAddress.Loopback`。

- [ ] **步骤 1：先写请求与 host 测试。** 覆盖 golden request、非法字段/type/大小、duplicate JSON key、Origin、Host、chunked/超大 body、错误码、并发 inference gate、响应不回显输入。至少固定 HTTP 状态与错误响应：无效输入 `422`；busy 和 predictor 异常 `503`。
- [ ] **步骤 2：运行测试确认失败。**

运行：`dotnet test tools/laya_service/tests/LayaService.Tests.csproj -c Release --filter 'FullyQualifiedName~ProtocolTests|FullyQualifiedName~DecisionServerTests'`
预期：测试类型尚未实现时失败。

- [ ] **步骤 3：实现 DTO 和 JSON 校验。** wire request 仅允许 `model/state/questions/rubric_version/language`；保留 choice/score/noul 形状、候选项顺序和已确认的请求界限。用 `Utf8JsonReader`/`JsonDocument` 严格拒绝重复 key、非有限数值和无效 UTF-8；不可将 JSON 对象重序列化后再计算 schema identity。
- [ ] **步骤 4：实现 ASP.NET Core server。** 绑定 loopback；只映射 `GET /health` 和 `POST /v1/decisions`；校验 Host 精确匹配绑定地址/端口并拒绝任意 Origin；要求 JSON `Content-Length`、拒绝 Transfer-Encoding、限制 body 为 65,536 bytes；无排队单推理 gate、有限连接数、连接/读取 deadline；关闭请求日志。以固定 reason code 返回 4xx/5xx，不回显异常。
- [ ] **步骤 5：重跑服务测试。**

运行：`dotnet test tools/laya_service/tests/LayaService.Tests.csproj -c Release --filter 'FullyQualifiedName~ProtocolTests|FullyQualifiedName~DecisionServerTests'`
预期：fake predictor 下所有 wire、安全和限制测试通过，不需要模型文件。

- [ ] **步骤 6：提交** `feat: add secure local Laya decision endpoint`。

### Task 3：实现 manifest、模型下载和资产完整性检查

**文件：**

- Create: `tools/laya_service/Models/ModelManifest.cs`
- Create: `tools/laya_service/Models/HuggingFaceDownloader.cs`
- Create: `tools/laya_service/tests/ModelManifestTests.cs`
- Create: `tools/laya_service/tests/HuggingFaceDownloaderTests.cs`
- Modify: `tools/laya_service/CommandLine.cs`
- Modify: `tools/laya_service/Program.cs`

**接口：**

- `ModelManifest.LoadAndVerify(string manifestPath, string expectedRevision)` 检查 manifest `version=1`、revision、checkpoint allowlist、路径 containment 和每个文件的 SHA-256，并返回 `VerifiedManifest`（checkpoint 名称、revision、绝对路径及文件哈希）。校准产物单独使用 `version=2`，二者不可混淆。
- `HuggingFaceDownloader.DownloadAsync(DownloadOptions options, HttpClient http, CancellationToken ct)` 仅从 `https://huggingface.co/convaiinnovations/laya/resolve/{revision}/{file}` 获取 allowlist 文件，输出模型 manifest 路径。
- 默认 revision 沿用现有实现的 `1c5edc17a7acd8701df6fc341c0d179f1c62c982`；迁移 `download.py` 中该 revision 的已知逐文件哈希。只有文件下载后哈希验证通过，才将其写入 manifest。

- [ ] **步骤 1：先写 manifest 与下载测试。** 覆盖固定 revision、三种 checkpoint 路径、缺失/改坏文件、路径穿越、未知文件、HTTP 错误/重定向、哈希不匹配、原子 manifest 以及许可/notice 输出；使用假的 `HttpMessageHandler`，不访问网络。
- [ ] **步骤 2：运行测试确认失败。**

运行：`dotnet test tools/laya_service/tests/LayaService.Tests.csproj -c Release --filter 'FullyQualifiedName~ModelManifestTests|FullyQualifiedName~HuggingFaceDownloaderTests'`
预期：实现缺失导致失败。

- [ ] **步骤 3：实现下载与校验。** 文件清单从已固定的 NLaya checkpoint 文件布局生成；每个下载写入 destination 下唯一临时文件，摘要通过后才原子替换；不同 revision 使用不同目标目录；验证任何最终路径都留在 destination 内。写入 manifest 前重新验证完整 checkpoint。复制现有 `THIRD_PARTY_NOTICES.md` 和 Apache-2.0 license 到模型目录。
- [ ] **步骤 4：连接 `download` CLI。** 支持 `--destination`、`--revision` 和可重复 `--checkpoint`；缺少必需值或 revision 不为 40 位 hex 时返回非零退出码。
- [ ] **步骤 5：重跑下载测试与 Release build。**

运行：`dotnet test tools/laya_service/tests/LayaService.Tests.csproj -c Release --filter 'FullyQualifiedName~ModelManifestTests|FullyQualifiedName~HuggingFaceDownloaderTests'`
预期：所有测试通过且测试未产生实际 Hugging Face 请求。

- [ ] **步骤 6：提交** `feat: add pinned NLaya model downloader`。

### Task 4：接入 NLaya runtime、checkpoint routing 和新 metadata

**文件：**

- Create: `tools/laya_service/Inference/NLayaDecisionPredictor.cs`
- Create: `tools/laya_service/Inference/CalibrationStore.cs`
- Create: `tools/laya_service/tests/NLayaDecisionPredictorTests.cs`
- Modify: `tools/laya_service/Program.cs`
- Modify: `src/OpenClaw.Routing.Decisions/LayaDecisionClient.cs`
- Modify: `src/OpenClaw.Tests/LayaRoutingTests.cs`

**接口：**

- 在 `Inference/ServeOptions.cs` 定义 `ServeOptions(string ManifestPath, string? CalibrationPath, int Port, string Device, string Checkpoint, int Threads)`。`NLayaDecisionPredictor.LoadAsync(VerifiedManifest manifest, ServeOptions options, CancellationToken ct)` 先校验所有模型与 calibration，再加载/warm-up 选中 checkpoint，返回 ready predictor。
- Predictor 映射 `DecisionWireRequest` 为 NLaya `Questions`/state/`RouteOptions`，再把 typed result 映射到现有 `DecisionWireResponse`。
- Metadata 必须提供现有字段并固定 `sdk_version="1.0.0"`、`runtime="NLaya"`、`revision`、`checkpoint`、`schema_hash`、`rubric_version`、`device`、`calibration_id` 和 `truncated=false`。

- [ ] **步骤 1：先写 fake/fixture 输出适配测试。** 测试 Choice/Score/Noul 与概率映射、raw_answers 保留、自动 English/multilingual 路由、语言 hint、非拉丁/混合文本、显式 typed-decisions、未知 checkpoint、设备不可用和输入超过 token budget 时拒绝。typed-decisions 不可因任务名称自动选择。
- [ ] **步骤 2：运行测试确认失败。**

运行：`dotnet test tools/laya_service/tests/LayaService.Tests.csproj -c Release --filter FullyQualifiedName~NLayaDecisionPredictorTests`
预期：NLaya adapter 尚未实现时失败。

- [ ] **步骤 3：实现 NLaya adapter。** 从通过 hash 校验的本地路径加载；使用 `NLaya.Routing.Router`/NLaya 1.0.0 公开 API 和 `UseTorchSharp`；禁用 task-name 自动选择；仅预加载 manifest 中请求的 checkpoints。使用同一 tokenizer 逻辑检查 token budget，超限在 inference 前失败，不将 `truncated=false` 用作未经检查的声明。明确配置 CPU；accelerator 仅在对应 runtime 被安装且显式选择时初始化，不捕获后退到 CPU。
- [ ] **步骤 4：更新 Gateway metadata contract。** 把 `LayaDecisionClient` 的 SDK 检查改为 `1.0.0` 并新增严格 `runtime == "NLaya"` 检查。扩展 `LayaRoutingTests` 的 handler metadata 和 mismatched runtime 用例，保持服务错误时 baseline fallback 与无 hosted fallback 断言。
- [ ] **步骤 5：运行服务 adapter 与 Gateway 定向测试。**

运行：`dotnet test tools/laya_service/tests/LayaService.Tests.csproj -c Release --filter FullyQualifiedName~NLayaDecisionPredictorTests`
预期：fake/fixture 测试通过，无权重下载。
运行：`dotnet test src/OpenClaw.Tests/OpenClaw.Tests.csproj -c Release -p:OpenClawSkipDashboardBuild=true --filter FullyQualifiedName~LayaRoutingTests`
预期：Gateway metadata 和原有 fallback 测试通过。

- [ ] **步骤 6：提交** `feat: adapt NLaya predictions to Laya wire contract`。

### Task 5：迁移样本 evaluation 与 v2 calibration

**文件：**

- Create: `tools/laya_service/Evaluation/CaseEvaluator.cs`
- Create: `tools/laya_service/Evaluation/CalibrationFitter.cs`
- Create: `tools/laya_service/Evaluation/CalibrationMetrics.cs`
- Create: `tools/laya_service/tests/CaseEvaluatorTests.cs`
- Create: `tools/laya_service/tests/CalibrationFitterTests.cs`
- Modify: `tools/laya_service/Inference/CalibrationStore.cs`
- Modify: `tools/laya_service/CommandLine.cs`
- Modify: `tools/laya_service/Program.cs`

**接口：**

- `CaseEvaluator.EvaluateAsync(string datasetPath, Uri endpoint, string outputPath, HttpClient http, CancellationToken ct)` 输出 JSONL observation 数量。
- 在 `Evaluation/Observation.cs` 定义 `Observation(string CaseId, string CaseFingerprint, string QuestionId, string Model, string Checkpoint, string SchemaHash, string RuntimeVersion, JsonElement RawAnswer, string Label)`；`RawAnswer` 保留答案类型及原始概率，不含 state。
- `CalibrationFitter.Fit(IReadOnlyList<Observation> training, IReadOnlyList<Observation> validation, int minimumSamples = 20)` 返回 v2 calibration DTO。
- `CalibrationMetrics.Measure(IReadOnlyList<LabeledPrediction> rows)` 返回样本数、accuracy、NLL、Brier、ECE、reliability bins 和 risk/coverage；`LabeledPrediction` 在 `CalibrationMetrics.cs` 定义，携带答案类型、候选键、归一化概率和标签。

- [ ] **步骤 1：先写 evaluation 隐私与 HTTP 测试。** 用 fake `HttpMessageHandler` 验证 case ID 唯一性、标签覆盖每个问题、默认 rubric/model、loopback literal endpoint 限制、proxy/redirect 禁用、服务 metadata identity 检查、canonical 请求指纹及 observation 不含原始 state。
- [ ] **步骤 2：先写 calibration metric/contract 测试。** 覆盖概率归一化、noul/choice/score 分布、temperature grid、train/validation ID 与 fingerprint 重叠、模型/schema/runtime 混合、bucket 不一致、每桶最少 20 条、多标签、稳定 v2 JSON 和 exact-byte SHA-256。保留旧 Python metrics golden 结果。
- [ ] **步骤 3：运行测试确认失败。**

运行：`dotnet test tools/laya_service/tests/LayaService.Tests.csproj -c Release --filter 'FullyQualifiedName~CaseEvaluatorTests|FullyQualifiedName~CalibrationFitterTests'`
预期：尚无实现时失败。

- [ ] **步骤 4：实现 evaluator。** 读取现有 case JSONL，每条请求只发往字面 loopback `/v1/decisions`；用 `HttpClientHandler` 设置 `AllowAutoRedirect=false` 且禁用代理。observations 只写 ID、canonical fingerprint、question、label、模型/checkpoint/schema/runtime provenance 和 raw answer；写入临时文件，成功处理整个数据集后原子替换目标。
- [ ] **步骤 5：实现 metrics、温度拟合和 v2 calibration store。** 拟合继续使用候选温度 `10 ** (-1 + i / 80)`，`i=0..160` 的 NLL 最小值；按 checkpoint 与 `type:option-count` 分桶。artifact 固定为 UTF-8 JSON、final newline、`version=2`，绑定模型 revision、schema hash、NLaya 版本、温度和 validation metrics；service 校验 configured SHA-256，拒绝 v1 并提示重新生成。
- [ ] **步骤 6：连接 `evaluate` 与 `calibrate` CLI 并重跑定向测试。**

运行：`dotnet test tools/laya_service/tests/LayaService.Tests.csproj -c Release --filter 'FullyQualifiedName~CaseEvaluatorTests|FullyQualifiedName~CalibrationFitterTests'`
预期：原始观察隐私、校准分桶及 artifact 哈希测试全部通过。

- [ ] **步骤 7：提交** `feat: port Laya evaluation and calibration to dotnet`。

### Task 6：迁移 provider-neutral routing journal report

**文件：**

- Create: `tools/laya_service/Reporting/RoutingJournalReport.cs`
- Create: `tools/laya_service/Reporting/ReliabilityPlot.cs`
- Create: `tools/laya_service/tests/RoutingJournalReportTests.cs`
- Create: `tools/laya_service/tests/ReliabilityPlotTests.cs`
- Keep as fixtures: `tests/routing-eval/sample-routing-baseline.json`
- Keep as fixtures: `tests/routing-eval/turn-routing-quality.sample.jsonl`
- Modify: `tools/laya_service/CommandLine.cs`
- Modify: `tools/laya_service/Program.cs`

**接口：**

- `RoutingJournalReport.ReadJsonLines(string path)` 对每行给出带行号的 JSON 错误；`Summarize(rows, labels)` 返回与当前脚本字段兼容的 report DTO。
- `ReliabilityPlot.WritePng(RoutingReport report, string outputPath)` 使用 ScottPlot 5.1.59 生成 reliability 与 risk/coverage 两列图；没有带标签概率 cohort 时返回明确错误。

- [ ] **步骤 1：先写 report parity tests。** 从现有 `test_jev_report.py` 逐例移植 Jev fallback、Laya cohort 分组、abstain 概率、无标签不报 accuracy、空/重复/unmatched 数据校验；断言输出字段、数值和 JSON key 与现有 Python fixtures 一致。
- [ ] **步骤 2：运行测试确认失败。**

运行：`dotnet test tools/laya_service/tests/LayaService.Tests.csproj -c Release --filter 'FullyQualifiedName~RoutingJournalReportTests|FullyQualifiedName~ReliabilityPlotTests'`
预期：尚无报告实现时失败。

- [ ] **步骤 3：实现 summary/quality/calibration cohort。** 保留 decisions、usage、eligible proposals、coverage、mode/provider/model/rubric/reason/tier counts、latency percentile、token/cost、quality confusion/F1/under/over-routing/high-risk retention 字段；概率指标调用 Task 5 的 `CalibrationMetrics` 并严格按 provider/model/rubric/checkpoint/revision/calibration/schema 分组。
- [ ] **步骤 4：实现 ScottPlot 输出。** 锁定 `<PackageReference Include="ScottPlot" Version="5.1.59" />`；生成 headless PNG，标题包含 cohort identity，绘出理想 calibration 对角线和 risk/coverage 曲线；无图像设备依赖，不生成无数据的误导图。
- [ ] **步骤 5：连接 `report` CLI 并运行 fixtures/PNG 测试。**

运行：`dotnet test tools/laya_service/tests/LayaService.Tests.csproj -c Release --filter 'FullyQualifiedName~RoutingJournalReportTests|FullyQualifiedName~ReliabilityPlotTests'`
预期：Jev 与 Laya fixtures 的 report JSON parity 通过，PNG 文件存在且非空。

- [ ] **步骤 6：提交** `feat: port decision routing reports to dotnet`。

### Task 7：接入 solution/CI 并移除 Python 实现

**文件：**

- Modify: `OpenClaw.Net.slnx`
- Modify: `.github/workflows/ci.yml`
- Delete: Task 1–6 文件清单中的 Python modules、Python requirements、Python 测试与两个 report entrypoint。
- Keep: rubric、license、notice 和 routing JSONL fixtures。

- [ ] **步骤 1：先确认 .NET 测试通过，再更新 CI 命令。** 在 `ci.yml` 的本地决策工具步骤中使用 `dotnet test tools/laya_service/tests/LayaService.Tests.csproj -c Release --no-build`；保留仓库其他既有 Python 检查，不删除 `eng/verify-vault-publish-boundary.py` 步骤。
- [ ] **步骤 2：将服务和测试项目加入 `.slnx` 并做 solution restore/build。**

运行：`dotnet restore OpenClaw.Net.slnx`
预期：NLaya/TorchSharp/ScottPlot 固定依赖可恢复。
运行：`dotnet build OpenClaw.Net.slnx --no-restore -c Release`
预期：solution build 成功。

- [ ] **步骤 3：仅在新测试已通过后删除 Python 实现/测试/入口。** 保留许可、rubric 和 JSONL fixtures。用 `rg -n "tools\.laya_service|tools/laya_service|evaluate-jev-routing|evaluate-decision-routing|tests/laya-service|test_jev_report"` 扫描 tracked 文件；仅允许新文档里说明历史迁移的文字，不允许剩余运行时引用。
- [ ] **步骤 4：运行 CI 对应的 Python 依赖边界检查与 .NET tests。**

运行：`dotnet test tools/laya_service/tests/LayaService.Tests.csproj -c Release`
预期：所有工具测试通过。
运行：`dotnet test src/OpenClaw.Tests/OpenClaw.Tests.csproj -c Release -p:OpenClawSkipDashboardBuild=true --filter FullyQualifiedName~LayaRoutingTests`
预期：Gateway contract 与安全 fallback 测试通过。
运行：`dotnet build OpenClaw.Net.slnx -c Release`
预期：完整 solution build 通过。

- [ ] **步骤 5：提交** `build: replace Python Laya tooling with dotnet`。

### Task 8：迁移中英文文档并执行最终验收

**文件：**

- Modify: `docs/laya-routing.md`
- Modify: `docs/jev-routing.md`
- Modify: `docs/cli/routing.md`
- Modify: `tools/laya_service/README.md`
- Modify: `docs/README.md`
- Modify: `docs/SITE_MAP.md`
- Modify: `docs/zh-CN/SITE_MAP.md`
- Create: `docs/zh-CN/integrations/laya-routing.md`
- Modify: `docs/cli/routing.md` only once; consolidate references into the same edit.

- [ ] **步骤 1：将运维命令改为 .NET CLI。** 中英文文档说明 NuGet/runtime 版本、`serve/download/evaluate/calibrate/report` 命令、revision/hash/manifest、runtime metadata、校准 v2 重生成、设备选择和 rollback；Jev 报告改为同一 `report` 命令，旧 Python 命令不再展示。
- [ ] **步骤 2：更新文档导航。** 在 `docs/README.md`、`docs/SITE_MAP.md` 和 `docs/zh-CN/SITE_MAP.md` 链接运维指南及中文版；确保新增相对链接目标存在。
- [ ] **步骤 3：运行最终 focused gates。**

运行：`dotnet test tools/laya_service/tests/LayaService.Tests.csproj -c Release`
预期：全部 service/download/evaluate/calibrate/report tests 通过，且不访问 Hugging Face。
运行：`dotnet test src/OpenClaw.Tests/OpenClaw.Tests.csproj -c Release -p:OpenClawSkipDashboardBuild=true --filter FullyQualifiedName~LayaRoutingTests`
预期：Gateway 新 metadata 与 baseline fallback tests 通过。
运行：`dotnet build OpenClaw.Net.slnx -c Release`
预期：所有 solution projects 编译通过。
运行：`git diff --check`
预期：无空白错误。

- [ ] **步骤 4：提供 opt-in 真实模型验证说明。** 记录需由操作者在模型下载和硬件已配置的环境运行的命令：分别 smoke 每个已支持 checkpoint，检查 parity 的 typed-answer/category 与概率容差；不得把此项混入普通 CI，也不得称为生产质量证据。
- [ ] **步骤 5：提交** `docs: document NLaya Laya service migration`。

## 规格覆盖自检

- 独立 CLI、精确 package/runtime、Gateway 进程边界：Tasks 1、4、7。
- 模型下载、revision/hash/manifest、许可归属与离线启动：Tasks 3、4。
- Loopback HTTP、输入/token 限制、错误码、并发与隐私：Task 2、4。
- Python 去除及所有原运行路径无 Python 依赖：Tasks 5–8。
- evaluation、校准、v2 artifact、指标与 privacy：Task 5。
- Jev/Laya provider-neutral report、PNG 与旧入口移除：Tasks 6–8。
- Gateway metadata/fallback contract、CI、文档和可选真实模型 smoke：Tasks 4、7、8。

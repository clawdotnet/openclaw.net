# 本地 Laya 决策路由

OpenClaw 可将 Laya 作为可选的本地决策服务。Native 与 Microsoft Agent Framework runtime 共用决策路由策略；默认关闭。Laya 输出结构化决策，不是聊天模型，不应配置为 `Models.Profiles` 中的生成模型。

## 运行边界

独立服务以 .NET 10 构建，依赖 `NLaya`/`NLaya.TorchSharp` 1.0.0 和 `TorchSharp-cpu` 0.107.0。Gateway 仅通过 loopback HTTP 访问服务，不引用 NLaya 或 TorchSharp。当前按 JIT 服务部署；不声明 NativeAOT 支持。当前依赖集验证过 CPU。`cuda` 与 `mps` 只有在安装匹配的 TorchSharp backend 后才能启动；backend 不可用时服务会失败退出，不会静默切换到 CPU。

Laya 由 Nandakishor Mukkunnoth（Nandakishor M）、ConvAI Innovations 和上游贡献者开发。模型、SDK 和研究成果归其作者所有。OpenClaw 单独维护本地服务、适配层和评估工具。详见[作者文章](https://laya.convaiinnovations.com/)、[源代码仓库](https://github.com/NandhaKishorM/laya)、[模型卡](https://huggingface.co/convaiinnovations/laya)以及[第三方声明](../../../tools/laya_service/THIRD_PARTY_NOTICES.md)。

## 下载与启动

在仓库根目录使用 .NET 10 SDK。默认 revision 固定为 `1c5edc17a7acd8701df6fc341c0d179f1c62c982`；可按需选择 checkpoint：

```bash
dotnet run --project tools/laya_service -c Release -- download \
  --destination /path/to/laya-models \
  --revision 1c5edc17a7acd8701df6fc341c0d179f1c62c982 \
  --checkpoint english --checkpoint multilingual
```

下载器只获取 allowlist 中的模型、配置和 tokenizer 文件，为每个文件计算 SHA-256 并写入 `manifest.json`，同时复制上游 attribution 和 Apache-2.0 license。变更 revision 时使用新的目标目录。服务启动前会校验 manifest 路径与资产哈希；serve 过程只加载本地资产，不下载权重。模型和许可证仍属于上游项目，参见 [THIRD_PARTY_NOTICES.md](../../../tools/laya_service/THIRD_PARTY_NOTICES.md)。

```bash
dotnet run --project tools/laya_service -c Release -- serve \
  --manifest /path/to/laya-models/manifest.json --device cpu --port 8099 --threads 4
```

端口默认 `8099`，设备默认 `cpu`，线程数默认 `4`。`--checkpoint auto` 为默认路由；`english` 与 `multilingual` 按语言路由。`typed-decisions` 必须下载后显式选择，不会因问题名称而自动启用。健康检查位于 `http://127.0.0.1:8099/health`。

服务仅绑定 `127.0.0.1`，拒绝浏览器 Origin 和不匹配的 Host，限制请求大小和连接数，并且单次只允许一个推理请求，不排队。Gateway 绕过 proxy/redirect，只接受字面 loopback endpoint；Laya 失败时回到 baseline，不会 fallback 到托管 Jev。被选中的会话文本仍会跨越本地 HTTP 进程边界。

## Gateway 配置

保留现有 `Policy.Tiers` profile 映射，一次只启用一个决策 provider。Laya 示例：

```json
{
  "OpenClaw": {
    "DynamicTurnRouting": {
      "Jev": { "Mode": "disabled" },
      "Laya": {
        "Mode": "shadow",
        "Endpoint": "http://127.0.0.1:8099/v1/decisions",
        "Model": "laya@1c5edc17a7acd8701df6fc341c0d179f1c62c982",
        "CalibrationId": "",
        "Language": "",
        "TimeoutMs": 1500,
        "MaxConcurrentRequests": 1,
        "DiagnosticsPath": "routing/laya-decisions.jsonl"
      }
    }
  }
}
```

`shadow` 保持 baseline 结果不变，但会增加有界的评估耗时。服务 metadata 使用 `sdk_version: "1.0.0"` 和 `runtime: "NLaya"`；Gateway 会严格检查两项身份。错误、过载、超时和不确定结果都保留 baseline。共享策略仍保留脱敏、显式模型选择、安全 floor、sticky tier 和工具权限，不会授权执行操作。

## 评估与校准

评估集每行包含唯一 `case_id`、`state` 和覆盖每个问题的人工标签；省略 model/questions 时，工具使用共享 rubric 和固定 model：

```json
{"case_id":"cal-001","state":{"current_request":"将 hello 改成大写。","recent_conversation":[]},"labels":{"tier":"T0","high_risk":false,"requires_tools":false}}
```

准备代表性的拟合集和独立验证集，涵盖多语言、多轮引用、歧义、高影响任务和依赖工具的请求。每个 checkpoint/question-type/option-count bucket 至少 20 条只是工具下限，并非生产样本量建议。每个拟合桶需要多个标签；不要从模型输出来机械生成风险或工具标签。

```bash
dotnet run --project tools/laya_service -c Release -- evaluate /path/to/calibration-cases.jsonl \
  --endpoint http://127.0.0.1:8099/v1/decisions --output /path/to/calibration-raw.jsonl
dotnet run --project tools/laya_service -c Release -- evaluate /path/to/validation-cases.jsonl \
  --endpoint http://127.0.0.1:8099/v1/decisions --output /path/to/validation-raw.jsonl
dotnet run --project tools/laya_service -c Release -- calibrate \
  --fit /path/to/calibration-raw.jsonl --validate /path/to/validation-raw.jsonl \
  --output /path/to/calibration.json
```

`evaluate` 只允许字面 `http://127.0.0.1:PORT/v1/decisions`，禁用 proxy 与自动 redirect。Observation 保存 raw answer、标签、来源身份和 canonical request fingerprint，不包含 state；仍应按可能敏感的 metadata 保护这些文件。`calibrate` 按 checkpoint 与 `type:option-count` 分桶拟合温度，并检查 ID/fingerprint 不重叠、model/schema/runtime 一致和每桶样本要求。结果包括 held-out NLL、Brier、ECE、reliability bins 与 risk/coverage。产物是 UTF-8 JSON，末尾换行，`version: 2`，绑定模型 revision、schema hash、`sdk_version: "1.0.0"` 与 `runtime: "NLaya"`；命令输出产物的精确 SHA-256。

以 `--calibration /path/to/calibration.json` 重启服务，并将打印的 SHA-256 配置到 `Laya.CalibrationId`。服务拒绝 v1 calibration、不匹配的 schema，或缺失的 checkpoint/bucket。先继续 shadow 并检查独立验证集，再考虑 active。温度缩放不能修复排序错误或知识缺失；仓库不提供生产 calibration artifact。

## 路由报告

Jev 与 Laya journal 使用同一个 provider-neutral .NET 报告命令。标签字段为 `decision_id`、`expected_tier`，以及可选 boolean `high_risk`。概率指标按 provider/model/rubric/checkpoint/revision/calibration/schema 分 cohort。无标签时不会声称准确率；PNG 图必须有带标签的概率数据。

```bash
dotnet run --project tools/laya_service -c Release -- report \
  /path/to/jev-decisions.snapshot.jsonl \
  --labels /path/to/tier-labels.jsonl --output /path/to/report.json \
  --plot /path/to/reliability.png
```

## 回滚与验证

将 `Laya.Mode` 设为 `disabled` 并重启 Gateway；也可以运行 `openclaw routing configure router --router disabled` 关闭 ONNX、Jev 和 Laya。删除可能重新启用路由的环境变量，并停止独立运行的 .NET 服务进程。

```bash
dotnet test tools/laya_service/tests/LayaService.Tests.csproj -c Release
dotnet test src/OpenClaw.Tests/OpenClaw.Tests.csproj -c Release \
  -p:OpenClawSkipDashboardBuild=true --filter FullyQualifiedName~LayaRoutingTests
```

这些自动化测试使用 fake 与 fixture，不会下载模型，也不证明真实模型质量。真实推理和 checkpoint parity 是需在已配置模型资产与设备 backend 的环境中单独执行的可选运维检查。Skill 选择、记忆重排和 workflow escalation 属于后续独立集成，需要各自的质量评估。

可选的真实模型检查需先下载三个 checkpoint。分别用 `--checkpoint english`、`--checkpoint multilingual`、`--checkpoint typed-decisions` 启动服务；每个 checkpoint 都对同一份带标签 parity 数据集运行评估：

```bash
dotnet run --project tools/laya_service -c Release -- serve \
  --manifest /path/to/laya-models/manifest.json --checkpoint english --device cpu
dotnet run --project tools/laya_service -c Release -- evaluate /path/to/parity-cases.jsonl \
  --endpoint http://127.0.0.1:8099/v1/decisions --output /path/to/english-observations.jsonl
```

对 `multilingual` 和 `typed-decisions` 重复执行，并使用不同的输出路径。将 observation 与经批准的参考输出比较：typed answer/category 应精确匹配；概率差异应在同一 device/runtime 下、运行前预先确定的容差内。记录 checkpoint、revision、runtime、device 和容差。仓库未提供真实模型 golden output 或生产验收阈值；此检查不属于普通 CI，也不构成生产路由质量证据。
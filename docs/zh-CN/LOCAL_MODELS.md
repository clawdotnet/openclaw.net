# 嵌入式本地模型

OpenClaw.NET 支持一个可选的 `embedded` 提供商，用于 OpenClaw 管理的本地模型包。嵌入式模式用于私密的、离线的、低成本的辅助任务，无需用户管理 Ollama 或单独的模型服务器。

主运行时仍然不会在进程中加载模型库。它启动一个受监督的环回 sidecar，并通过内部的 OpenAI 兼容协议与其通信。

## 何时使用

在以下场景使用嵌入式本地模式：

- 追踪和运行摘要
- 本地路由和意图分类
- 记忆提取
- 简单的私密/离线问答
- 小型代码解释
- 当所选嵌入式模型支持图像输入时，进行基于帧的视频理解

在以下场景优先使用云端或生产推理配置文件：

- 可靠的工具调用
- 严格的 JSON Schema 或结构化输出
- 大规模重构
- 高风险推理
- 超出本地内存的超长上下文任务

## CLI 工作流

列出可安装的包：

```bash
openclaw models packages
```

从本地模型文件安装：

```bash
openclaw models install gemma-local-small-q4 \
  --accept-license \
  --path ~/Downloads/gemma-3-4b-it-q4_0.gguf
```

验证或移除包：

```bash
openclaw models verify gemma-local-small-q4
openclaw models status gemma-local-small-q4
openclaw models remove gemma-local-small-q4
```

包目录显示后端、格式、上下文窗口、实验状态和 SHA-256 期望值。受控模型下载应通过显式许可证接受和令牌支持的下载或手动下载的文件路径进行安装。

## 提供商配置

使用 `embedded` 提供商和基于包的预设：

```json
{
  "OpenClaw": {
    "Models": {
      "DefaultProfile": "embedded-local",
      "Profiles": [
        {
          "Id": "embedded-local",
          "PresetId": "embedded-gemma-small-q4",
          "Provider": "embedded",
          "Model": "gemma-local-small-q4",
          "Tags": ["local", "private", "offline", "cheap"],
          "FallbackProfileIds": ["frontier-tools"]
        }
      ]
    },
    "LocalInference": {
      "Enabled": true,
      "AutoStart": true,
      "RuntimePath": "llama-server",
      "Host": "127.0.0.1",
      "Port": 0,
      "Threads": "auto",
      "GpuLayers": "auto"
    }
  }
}
```

对于源代码检出，`openclaw setup --provider embedded --model-preset embedded-gemma-small-q4 --model gemma-local-small-q4` 写入无密钥的嵌入式配置文件。

## 动态回合路由

OpenClaw 可以将每个传入的用户回合分类为 `T0` 到 `T3`，并将该回合映射到现有的模型配置文件。

当前状态：

- 运行时布线在 native 和 MAF 编排器路径中均已实现。
- ONNX 路由器是可选的，并且对于分类器质量仍然是实验性的。
- 当路由资产不可用时，启动仍然可用；运行时回退到 `T2`。
- 捆绑包加载是兼容优先的，但不是刚性的：当捆绑包元数据提供元数据时，加载器已经可以解析嵌套的清单资产路径和嵌入维度。
- 原始的 OpenSquilla v4.2 模型目录仍然不是即插即用的 `BundlePath` 输入，因为原生包布局和推理管道与当前的 OpenClaw 兼容合约不匹配。
- 分词器加载现在支持 Hugging Face 风格的 BPE 和 WordPiece `tokenizer.json` 文件，但兼容性仍然是资产特定的，取决于支持的预分词器形状。

首选配置是捆绑包优先，然后是策略覆盖：

```json
{
  "OpenClaw": {
    "DynamicTurnRouting": {
      "Enabled": true,
      "BundlePath": "models/routing/opensquilla-v4-compat",
      "Policy": {
        "EnableStickyTier": true,
        "EnableMarginUpgrade": true,
        "EnableUnderRoutingSafety": true,
        "Tiers": {
          "T0": { "ModelProfileId": "local-freeform", "DisableTools": true, "PromptMode": "minimal" },
          "T1": { "ModelProfileId": "mini-readonly", "AllowedTools": ["read_file"], "PromptMode": "compact" },
          "T2": {
            "ModelProfileId": "frontier-tools",
            "DirectModelFallbackProfileId": "frontier-tools-fallback",
            "ReasoningLevel": "medium",
            "ResponsePolicy": "balanced",
            "ImageCapableModelProfileId": "frontier-vision",
            "PromptMode": "full"
          },
          "T3": { "ModelProfileId": "frontier-deep", "PromptMode": "full" }
        }
      }
    }
  }
}
```

`Policy.Tiers` 是现代配置中支持的层级映射位置。

当不使用 `BundlePath` 时，兼容模式仍然支持直接的 `Assets.*` 路径（`ClassifierModelPath`、`EmbeddingModelPath`、`TokenizerPath`）。

对于 OpenSquilla 互操作性，优先将 `BundlePath` 指向显式的兼容导出，如 `models/routing/opensquilla-v4-compat`，而不是原始的 `v4.2_phase3_inference` 目录。主要剩余差距不是层级命名，而是原生管道形状：OpenClaw 目前期望一个嵌入模型加一个 4 类 ONNX 分类器，而 OpenSquilla v4.2 使用多阶段融合路由器。

路由管理的运维 CLI 表面：

- [cli/routing.md](../cli/routing.md)

回退语义：

- 缺少/不可用的分类器资产：回退到 `T2`，附带机器可读原因（例如 `classifier_unavailable`）
- 运行时推理错误：回退到 `T2`，附带机器可读原因（例如 `classifier_runtime_error`）

此仓库不提交分类器或嵌入二进制文件。将这些工件保存在本地运维管理的模型目录中，以便源代码树保持小巧、可审计和许可证中立。

## Sidecar 合约

嵌入式提供商期望 sidecar 暴露：

```http
GET /health
GET /v1/models
POST /v1/chat/completions
```

当模型/配置文件请求流式传输时，流式传输使用服务器发送事件。

对于 `llama.cpp`，OpenClaw 使用模型路径、主机、端口、上下文、可选的 Jinja/聊天模板标志、多模态投影仪、媒体路径、推理标志和草稿模型标志启动 `llama-server`。

## 基于帧的视频

嵌入式视频支持是确定性预处理，而不是原始视频摄入。

当回合包含 `video/*` 内容时，OpenClaw：

1. 使用 `ffprobe` 验证大小和时长
2. 使用 `ffmpeg` 采样有序的 JPEG 帧
3. 将帧存储在现有的媒体缓存中
4. 向模型发送一个文本块加上有序的 `image_url` 帧部分

在 `OpenClaw:Multimodal:Video` 下配置：

```json
{
  "OpenClaw": {
    "Multimodal": {
      "Video": {
        "Enabled": true,
        "FfmpegPath": "ffmpeg",
        "FfprobePath": "ffprobe",
        "MaxVideoBytes": 104857600,
        "MaxDurationSeconds": 120,
        "MaxFrames": 8,
        "FrameIntervalSeconds": 5,
        "FrameWidth": 768,
        "ExtractAudioTranscript": false,
        "FailureMode": "degrade"
      }
    }
  }
}
```

视频路由是能力感知的。嵌入式配置文件仅在视频预处理已启用且模型支持图像输入时才广告视频输入。如果所选配置文件无法满足视频回合，OpenClaw 在配置时回退到兼容的配置文件。

## LiteRT-LM

LiteRT-LM 包是实验性的。目录包括 `gemma-4-litert-e2b`，使用 `litert-community/gemma-4-E2B-it-litert-lm`、`gemma-4-E2B-it.litertlm` 文件、32k 运行时上下文和模型文件 SHA-256。

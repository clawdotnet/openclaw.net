# Embedded Local Models

OpenClaw.NET supports an optional `embedded` provider for OpenClaw-managed local model packages. Embedded mode is for private, offline, low-cost helper tasks without requiring users to manage Ollama or a separate model server.

The main runtime still does not load model libraries in-process. It starts a supervised loopback sidecar and talks to it through an internal OpenAI-compatible protocol.

## When To Use It

Use embedded local mode for:

- trace and run summarization
- local routing and intent classification
- memory extraction
- simple private/offline Q&A
- small code explanation
- frame-based video understanding when the selected embedded model supports image input

Prefer a cloud or production inference profile for:

- reliable tool calling
- strict JSON schema or structured outputs
- large refactors
- high-stakes reasoning
- long-context tasks that exceed local memory

## CLI Workflow

List installable packages:

```bash
openclaw models packages
```

Install from a local model file:

```bash
openclaw models install gemma-local-small-q4 \
  --accept-license \
  --path ~/Downloads/gemma-3-4b-it-q4_0.gguf
```

Verify or remove the package:

```bash
openclaw models verify gemma-local-small-q4
openclaw models status gemma-local-small-q4
openclaw models remove gemma-local-small-q4
```

The package catalog prints backend, format, context window, experimental status, and SHA-256 expectations. Gated model downloads should be installed with explicit license acceptance and either a token-backed download or a manually downloaded file path.

## Provider Configuration

Use provider `embedded` and a package-backed preset:

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

For source checkouts, `openclaw setup --provider embedded --model-preset embedded-gemma-small-q4 --model gemma-local-small-q4` writes the keyless embedded profile.

## Dynamic Turn Routing

OpenClaw can classify each incoming user turn into `T0` through `T3` and map that turn onto existing model profiles.

Current status:

- Runtime wiring is implemented in both native and MAF orchestrator paths.
- The ONNX router is optional and remains experimental for classifier quality.
- Startup stays available when routing assets are unavailable; runtime falls back to `T2`.
- Bundle loading is compat-first but not rigid: the loader can already resolve nested manifest asset paths and embedding dimensions when bundle metadata provides them.
- Raw OpenSquilla v4.2 model directories still are not drop-in `BundlePath` inputs because the native package layout and inference pipeline do not match the current OpenClaw compat contract.
- Tokenizer loading now supports Hugging Face-style BPE and WordPiece `tokenizer.json` files, but compatibility is still asset-specific and depends on supported pre-tokenizer shapes.

Preferred configuration is bundle-first, then policy overrides:

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

`Policy.Tiers` is the supported tier-mapping location in modern configuration.

Compatibility mode is still supported with direct `Assets.*` paths (`ClassifierModelPath`, `EmbeddingModelPath`, `TokenizerPath`) when you do not use `BundlePath`.

For OpenSquilla interoperability, prefer pointing `BundlePath` at an explicit compat export such as `models/routing/opensquilla-v4-compat` rather than at the raw `v4.2_phase3_inference` directory. The main remaining gap is not tier naming but native pipeline shape: OpenClaw currently expects one embedding model plus one 4-class ONNX classifier, while OpenSquilla v4.2 uses a multi-stage fused router.

Operator CLI surface for routing management:

- [cli/routing.md](cli/routing.md)

Fallback semantics:

- missing/unavailable classifier assets: fallback to `T2` with machine-readable reason (for example `classifier_unavailable`)
- runtime inference error: fallback to `T2` with machine-readable reason (for example `classifier_runtime_error`)

This repository does not commit classifier or embedding binaries. Keep those artifacts in your local operator-managed model directory so the source tree stays small, auditable, and license-neutral.

## Sidecar Contract

The embedded provider expects the sidecar to expose:

```http
GET /health
GET /v1/models
POST /v1/chat/completions
```

Streaming uses server-sent events when the model/profile requests streaming.

For `llama.cpp`, OpenClaw starts `llama-server` with the model path, host, port, context, optional Jinja/chat template flags, multimodal projector, media path, reasoning flags, and draft-model flags.

## Frame-Based Video

Embedded video support is deterministic preprocessing, not raw video ingestion.

When a turn contains `video/*` content, OpenClaw:

1. validates size and duration with `ffprobe`
2. samples ordered JPEG frames with `ffmpeg`
3. stores the frames in the existing media cache
4. sends the model a text block plus ordered `image_url` frame parts

Configure it under `OpenClaw:Multimodal:Video`:

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

Video routing is capability-aware. An embedded profile only advertises video input when video preprocessing is enabled and the model supports image input. If the selected profile cannot satisfy a video turn, OpenClaw falls back to a compatible profile when configured.

## LiteRT-LM

LiteRT-LM packages are experimental. The catalog includes `gemma-4-litert-e2b` using `litert-community/gemma-4-E2B-it-litert-lm`, the `gemma-4-E2B-it.litertlm` file, a 32k runtime context, and the model file SHA-256.

OpenClaw does not assume a generic `litert-server`. Set `OpenClaw:LocalInference:LiteRtRuntimePath` to an OpenClaw-compatible adapter binary that exposes the same internal HTTP contract as `llama-server`.

The LiteRT-LM CLI can be useful inside that adapter for initial text-only smoke support, but it is not a drop-in replacement for the sidecar contract. A CLI wrapper must still:

- expose `/health`, `/v1/models`, and `/v1/chat/completions`
- render chat messages into a prompt
- map responses back to OpenAI-compatible JSON
- avoid claiming streaming, image, video, audio, or tool support until the adapter actually implements those paths

Frame-based video can flow into a LiteRT adapter only after the adapter proves it accepts image frame inputs. Raw MediaPipe graph ingestion remains experimental.

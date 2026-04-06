# Model Profiles and Gemma

OpenClaw integrates **Gemma-family models, including Gemma 4**, through the existing provider seams instead of creating a Gemma-specific runtime fork.

That design keeps:

- one execution stack
- one tool-calling stack
- one session/compaction/middleware stack
- one MAF integration path

Gemma is treated as a **model backend** that can be reached through:

1. **Ollama** for local and development workflows
2. **OpenAI-compatible endpoints** for production or self-hosted inference gateways
3. future provider extensions if needed, without changing the runtime architecture

## Why profiles exist

Providers and models do not expose the same capabilities. A route that needs tool calling, structured outputs, and image input should not silently run against a model that only supports plain text chat.

Model profiles let OpenClaw describe a model instance independently from the provider transport:

- profile id
- provider id
- model id
- base URL
- API key or env ref
- capabilities
- context/output hints
- tags such as `local`, `private`, `cheap`, `tool-reliable`, `vision`

The runtime uses those profiles to:

- select a profile explicitly
- choose a profile based on route/session capability requirements
- prefer tags such as `local` or `private`
- fall back to another profile when allowed
- fail clearly when no profile can safely satisfy the request

## Example configuration

```json
{
  "OpenClaw": {
    "Llm": {
      "Provider": "openai",
      "Model": "gpt-4.1"
    },
    "Models": {
      "DefaultProfile": "gemma4-prod",
      "Profiles": [
        {
          "Id": "gemma4-local",
          "Provider": "ollama",
          "Model": "gemma4",
          "BaseUrl": "http://localhost:11434/v1",
          "Tags": ["local", "private", "cheap"],
          "Capabilities": {
            "SupportsTools": false,
            "SupportsVision": true,
            "SupportsJsonSchema": false,
            "SupportsStructuredOutputs": false,
            "SupportsStreaming": true,
            "SupportsParallelToolCalls": false,
            "SupportsReasoningEffort": false,
            "SupportsSystemMessages": true,
            "SupportsImageInput": true,
            "SupportsAudioInput": false,
            "MaxContextTokens": 131072,
            "MaxOutputTokens": 8192
          }
        },
        {
          "Id": "gemma4-prod",
          "Provider": "openai-compatible",
          "Model": "gemma-4",
          "BaseUrl": "https://your-inference-gateway.example.com/v1",
          "ApiKey": "env:MODEL_PROVIDER_KEY",
          "Tags": ["private", "prod", "vision"],
          "FallbackProfileIds": ["frontier-tools"],
          "Capabilities": {
            "SupportsTools": true,
            "SupportsVision": true,
            "SupportsJsonSchema": true,
            "SupportsStructuredOutputs": true,
            "SupportsStreaming": true,
            "SupportsParallelToolCalls": true,
            "SupportsReasoningEffort": false,
            "SupportsSystemMessages": true,
            "SupportsImageInput": true,
            "SupportsAudioInput": false,
            "MaxContextTokens": 262144,
            "MaxOutputTokens": 16384
          }
        },
        {
          "Id": "frontier-tools",
          "Provider": "openai",
          "Model": "gpt-4.1",
          "Tags": ["tool-reliable", "frontier"],
          "Capabilities": {
            "SupportsTools": true,
            "SupportsVision": true,
            "SupportsJsonSchema": true,
            "SupportsStructuredOutputs": true,
            "SupportsStreaming": true,
            "SupportsParallelToolCalls": true,
            "SupportsReasoningEffort": true,
            "SupportsSystemMessages": true,
            "SupportsImageInput": true,
            "SupportsAudioInput": true,
            "MaxContextTokens": 1000000,
            "MaxOutputTokens": 32768
          }
        }
      ]
    },
    "Routing": {
      "Enabled": true,
      "Routes": {
        "telegram:private-coder": {
          "ChannelId": "telegram",
          "SenderId": "private-coder",
          "ModelProfileId": "gemma4-local",
          "PreferredModelTags": ["local", "private"],
          "FallbackModelProfileIds": ["frontier-tools"],
          "ModelRequirements": {
            "SupportsTools": true,
            "SupportsStreaming": true
          }
        }
      }
    }
  }
}
```

## Gemma through Ollama

Use this when you want local/private inference for development or workstation deployments.

```json
{
  "Id": "gemma4-local",
  "Provider": "ollama",
  "Model": "gemma4",
  "BaseUrl": "http://localhost:11434/v1",
  "Tags": ["local", "private", "cheap"]
}
```

Notes:

- OpenClaw talks to Ollama through the existing OpenAI-compatible adapter path.
- `BaseUrl` defaults to `http://localhost:11434/v1` if omitted by the legacy provider config, but setting it explicitly is clearer for named profiles.
- If the profile does not advertise `SupportsTools`, routes that require tools will fail clearly or fall back.

## Gemma through an OpenAI-compatible gateway

Use this when Gemma is hosted behind a production inference service that exposes an OpenAI-compatible API.

```json
{
  "Id": "gemma4-prod",
  "Provider": "openai-compatible",
  "Model": "gemma-4",
  "BaseUrl": "https://your-inference-gateway.example.com/v1",
  "ApiKey": "env:MODEL_PROVIDER_KEY",
  "Tags": ["private", "prod", "vision"]
}
```

Notes:

- OpenClaw uses the existing OpenAI-compatible provider transport.
- No Gemma-specific runtime logic is required.
- Capability flags should reflect what your actual gateway exposes for that Gemma deployment.

## Route assignment and fallback

Routes can now express:

- `ModelProfileId`
- `PreferredModelTags`
- `FallbackModelProfileIds`
- `ModelRequirements`

Common patterns:

- coding/tool-heavy route: require `SupportsTools=true`, prefer tag `tool-reliable`
- privacy-sensitive route: prefer tags `local` and `private`
- cheap summarization route: prefer tags `cheap` and `local`

If the selected profile cannot satisfy the request, OpenClaw will either:

- fall back to the first compatible profile in `FallbackModelProfileIds`, or
- fail with a clear message such as:

`This route requires tool calling, but selected model profile 'gemma4-local' does not support it.`

## Capability flags

OpenClaw currently uses capability flags for:

- tool calling
- vision and image input
- JSON schema and structured outputs
- streaming
- parallel tool calls
- reasoning effort
- system messages
- audio input
- context/output token hints

These flags drive profile selection and request validation. They do not add provider-specific runtime branches.

## CLI and operator surfaces

List profiles:

```bash
openclaw models list
```

Run profile doctor:

```bash
openclaw models doctor
```

Run the built-in evaluation suite:

```bash
openclaw eval run --profile gemma4-prod
```

Compare multiple profiles:

```bash
openclaw eval compare --profiles gemma4-prod,frontier-tools
```

The gateway also exposes:

- `GET /admin/models`
- `GET /admin/models/doctor`
- `POST /admin/models/evaluations`

## Evaluation harness

The first version ships with OpenClaw-native scenarios:

- plain chat response
- structured JSON extraction
- tool selection correctness
- multi-turn continuity
- compaction recovery
- streaming behavior
- vision input behavior

Reports are written to:

- `memory/admin/model-evaluations/<run-id>.json`
- `memory/admin/model-evaluations/<run-id>.md`

This is intentionally lightweight and filesystem-based for the first release.

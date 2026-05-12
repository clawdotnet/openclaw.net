# Microsoft.Extensions.AI Provider Bridge

OpenClaw.NET already uses `Microsoft.Extensions.AI.IChatClient` internally as the chat boundary. The optional `OpenClaw.Providers.MicrosoftExtensionsAI` package makes that boundary explicit for developers who want to bring an arbitrary `IChatClient` implementation as an OpenClaw provider.

Use this bridge when a provider already exposes an `IChatClient` and you want OpenClaw to keep owning policy, tracing, budget checks, approvals, session handling, prompt cache behavior, retries, and provider usage accounting.

Do not use this bridge as a replacement for the built-in provider routes when those already fit. OpenAI, Claude, Gemini, Azure OpenAI, Ollama, and OpenAI-compatible endpoints remain the simplest supported paths.

## Runtime Mode

The bridge is a JIT-only native dynamic plugin. It is intentionally not a NativeAOT promise because arbitrary third-party `IChatClient` factories can require reflection, dynamic loading, or provider SDK behavior that is not trim-safe.

Use:

```json
{
  "OpenClaw": {
    "Runtime": {
      "Mode": "jit"
    },
    "Plugins": {
      "DynamicNative": {
        "Enabled": true
      }
    }
  }
}
```

## Factory Contract

Create a public parameterless factory that implements:

```csharp
using Microsoft.Extensions.AI;
using OpenClaw.Providers.MicrosoftExtensionsAI;

public sealed class MyChatClientFactory : IMicrosoftExtensionsAiChatClientFactory
{
    public IChatClient Create(MicrosoftExtensionsAiProviderFactoryContext context)
    {
        return CreateYourProviderClient(context);
    }
}
```

The factory receives:

- `PluginId`: the dynamic native plugin id.
- `ProviderId`: the OpenClaw provider id being registered.
- `Models`: the configured model ids.
- `Config`: provider-specific JSON from the plugin config.
- `Logger`: the OpenClaw plugin logger.

## Config Shape

Configure one or more providers under the bridge plugin entry:

```json
{
  "OpenClaw": {
    "Llm": {
      "Provider": "my-meai-provider",
      "Model": "my-model"
    },
    "Plugins": {
      "DynamicNative": {
        "Enabled": true,
        "Load": {
          "Paths": ["/path/to/openclaw-microsoft-extensions-ai-provider"]
        },
        "Entries": {
          "openclaw-microsoft-extensions-ai-provider": {
            "Config": {
              "providers": [
                {
                  "providerId": "my-meai-provider",
                  "models": ["my-model"],
                  "factoryAssemblyPath": "My.Provider.Factory.dll",
                  "factoryTypeName": "My.Provider.Factory.MyChatClientFactory",
                  "config": {
                    "apiKey": "env:MY_PROVIDER_KEY"
                  }
                }
              ]
            }
          }
        }
      }
    }
  }
}
```

`factoryAssemblyPath` may be absolute or relative to the bridge plugin assembly directory. Relative paths must resolve inside the bridge plugin directory. `factoryTypeName` may also be assembly-qualified when the factory assembly is already loadable.

## Validation

Startup fails the bridge plugin load when:

- `providers` is missing or empty.
- `providerId` is blank or duplicated.
- `models` is empty after trimming blank entries.
- `factoryTypeName` is blank or cannot be resolved.
- The resolved factory type does not implement `IMicrosoftExtensionsAiChatClientFactory`.
- The factory cannot be created with a public parameterless constructor.
- The factory returns `null`.

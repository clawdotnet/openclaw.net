# Microsoft.Extensions.AI Provider Sample

This sample shows how to expose an arbitrary `Microsoft.Extensions.AI.IChatClient` through the optional OpenClaw.NET provider bridge.

Build the bridge and this sample, then place both assemblies beside `OpenClaw.Providers.MicrosoftExtensionsAI.dll` in the dynamic native plugin folder.

Example dynamic native plugin config:

```json
{
  "OpenClaw": {
    "Runtime": {
      "Mode": "jit"
    },
    "Llm": {
      "Provider": "microsoft-extensions-ai-sample",
      "Model": "sample-model"
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
                  "providerId": "microsoft-extensions-ai-sample",
                  "models": ["sample-model"],
                  "factoryAssemblyPath": "OpenClaw.MicrosoftExtensionsAIProvider.dll",
                  "factoryTypeName": "OpenClaw.MicrosoftExtensionsAIProvider.SampleDeterministicChatClientFactory"
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

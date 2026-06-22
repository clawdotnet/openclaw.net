# Microsoft.Extensions.AI Provider 桥接

OpenClaw.NET 内部已使用 `Microsoft.Extensions.AI.IChatClient` 作为对话边界。可选包 `OpenClaw.Providers.MicrosoftExtensionsAI` 为希望将任意 `IChatClient` 实现作为 OpenClaw Provider 引入的开发者明确了这一边界。

当某个 Provider 已经暴露了 `IChatClient`，且你希望 OpenClaw 继续负责策略、追踪、预算检查、审批、会话处理、提示缓存行为、重试和 Provider 用量统计时，使用此桥接。

在内置 Provider 路由已经适用的情况下，不要使用此桥接来替代它们。OpenAI、Claude、Gemini、Azure OpenAI、Ollama 和 OpenAI 兼容端点仍然是最简单的受支持路径。

## 运行时模式

该桥接是一个仅 JIT 的原生动态插件。它有意不作为 NativeAOT 承诺，因为任意第三方 `IChatClient` 工厂可能涉及反射、动态加载或非裁剪安全的 Provider SDK 行为。

使用方式：

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

## 工厂契约

创建一个公共的无参工厂，实现以下接口：

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

工厂接收的上下文包含：

- `PluginId`：动态原生插件 ID。
- `ProviderId`：正在注册的 OpenClaw Provider ID。
- `Models`：配置的模型 ID 列表。
- `Config`：来自插件配置的 Provider 特定 JSON。
- `Logger`：OpenClaw 插件日志记录器。

## 配置结构

在桥接插件条目下配置一个或多个 Provider：

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

`factoryAssemblyPath` 可以是绝对路径或相对于桥接插件程序集目录的路径。相对路径必须在桥接插件目录内解析。当工厂程序集已经可加载时，`factoryTypeName` 也可以使用程序集限定名。

## 验证

启动时，以下情况会导致桥接插件加载失败：

- `providers` 缺失或为空。
- `providerId` 为空或重复。
- `models` 在去除空条目后为空。
- `factoryTypeName` 为空或无法解析。
- 解析出的工厂类型未实现 `IMicrosoftExtensionsAiChatClientFactory`。
- 工厂无法通过公共无参构造函数创建。
- 工厂返回 `null`。

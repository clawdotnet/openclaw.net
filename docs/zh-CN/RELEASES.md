# 发布下载

OpenClaw.NET 的低摩擦桌面路径是在 [GitHub Releases](https://github.com/clawdotnet/openclaw.net/releases/latest) 上发布的**桌面捆绑包**。它包含：

- Companion
- NativeAOT 网关
- NativeAOT CLI

用户应为其平台使用桌面捆绑包开始，而不是 GitHub Actions 工件。

| 资产 | 受众 |
| --- | --- |
| [`openclaw-desktop-win-x64.zip`](https://github.com/clawdotnet/openclaw.net/releases/latest/download/openclaw-desktop-win-x64.zip) | Windows 桌面用户 |
| [`openclaw-desktop-osx-arm64.zip`](https://github.com/clawdotnet/openclaw.net/releases/latest/download/openclaw-desktop-osx-arm64.zip) | Apple Silicon macOS 桌面用户 |
| [`openclaw-desktop-linux-x64.zip`](https://github.com/clawdotnet/openclaw.net/releases/latest/download/openclaw-desktop-linux-x64.zip) | Linux 桌面用户 |
| `openclaw-gateway-aot-*.zip` | 希望使用默认 `native` 和可选 `Runtime.Orchestrator=maf` 的 NativeAOT 网关的运维人员 |
| `openclaw-cli-aot-*.zip` | 仅 CLI 安装和脚本 |

每个归档都有匹配的 `.sha256` 校验和文件。

网关工件现在在正常构建中包含 Microsoft Agent Framework 适配器。`Runtime.Orchestrator=native` 在每个工件中保持默认值；仅当想要支持的 MAF 运行时路径时，设置 `OpenClaw:Runtime:Orchestrator=maf`。

## 用户首次运行

1. 从[最新的 GitHub Release](https://github.com/clawdotnet/openclaw.net/releases/latest) 下载桌面捆绑包。
2. 解压归档文件。
3. 从 `companion` 文件夹启动 Companion。
4. 使用**设置**选项卡输入提供商、模型、工作区和提供商密钥。
5. 点击**设置并启动**。

Companion 写入正常的本地 OpenClaw 配置，在 `127.0.0.1` 上启动捆绑的网关，并连接到它。如果配置已存在，Companion 可以在启动时自动启动本地网关。

## 当前签名状态

发布工作流构建 Windows 和 macOS 归档，并具有可选的签名/公证钩子。资产未签名，除非配置了所需的仓库密钥。

- Windows 归档未签名，直到配置 Authenticode 签名密钥。部分用户可能会看到 SmartScreen 警告。
- macOS 归档未签名且未公证，直到配置 Apple Developer ID 签名密钥。用户可能需要右键点击打开或移除隔离以进行本地测试。

发布级引导需要以下密钥：

| 密钥 | 用途 |
| --- | --- |
| `WINDOWS_SIGNING_CERT_BASE64` | Base64 编码的 Authenticode `.pfx` |
| `WINDOWS_SIGNING_CERT_PASSWORD` | Authenticode 证书密码 |
| `APPLE_DEVELOPER_ID_CERT_BASE64` | Base64 编码的 Apple Developer ID `.p12` |
| `APPLE_DEVELOPER_ID_CERT_PASSWORD` | Apple 证书密码 |
| `APPLE_CODESIGN_IDENTITY` | Developer ID Application 身份 |
| `APPLE_ID` | 用于公证的 Apple ID |
| `APPLE_TEAM_ID` | 用于公证的 Apple 团队 ID |
| `APPLE_APP_SPECIFIC_PASSWORD` | 用于公证的应用特定密码 |

安装程序优化仍然是后续工作：Windows 使用 `.exe`/`.msix`，macOS 使用 `.dmg`。

## 维护者流程

标记的发布自动构建和发布资产：

```bash
git tag v0.1.0
git push origin v0.1.0
```

维护者也可以手动运行 `Release` 工作流。当提供标签时，手动运行可以创建或更新草稿发布。

工作流当前构建：

- `linux-x64` 在 `ubuntu-latest`
- `win-x64` 在 `windows-latest`
- `osx-arm64` 在 `macos-15`

macOS runner 标签故意为 ARM 原生，用于 `osx-arm64` 工件。仅当你想支持更旧的 Intel Mac 且有一个可以可靠 NativeAOT 发布该 RID 的 runner 时，才添加 Intel macOS 行。

### Companion 发布冒烟测试

在发布公共桌面版本之前，在至少一个桌面捆绑包上运行此手动冒烟测试：

1. 将桌面归档解压到干净的目录中。
2. 从 `companion` 文件夹启动 Companion。
3. 使用**设置**，使用临时工作区和本地 Ollama 模型或一次性提供商密钥。
4. 确认 Companion 写入配置，在 `127.0.0.1` 上启动捆绑的网关，并显示已连接状态。
5. 停止托管网关，再次启动，并确认 Companion 重新连接。
6. 删除或损坏临时配置，并确认设置显示清晰的可恢复错误。
7. 关闭 Companion 并确认托管网关进程退出。

### macOS NativeAOT 链接器说明

网关和发布工作流默认使用 Apple 当前的链接器来生成 `osx-arm64` NativeAOT 发布。拉取请求、定时和手动 CI 也会在 `macos-15` 上验证此路径。在不使用 `-ld_classic` 的情况下，发布工作流必须构建网关、运行 `--doctor`、打包工件、解压桌面归档、验证可执行权限，并执行随附的 CLI。

已弃用的经典链接器临时保留为紧急诊断覆盖：

```bash
dotnet publish src/OpenClaw.Gateway/OpenClaw.Gateway.csproj -c Release -r osx-arm64 -p:PublishAot=true -p:OpenClawUseClassicMacLd=true
```

不要将此覆盖用于常规发布工件。在一个已发布版本使用现代链接器成功完成后，删除该覆盖。

## CI 工件 vs 发布

Actions 工件对于验证提交很有用，但它们不是用户友好的分发渠道。它们可能过期，可能需要 GitHub 访问权限，并且用户更难找到。GitHub Releases 是普通用户受支持的下载界面。

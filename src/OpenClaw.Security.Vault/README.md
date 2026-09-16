# OpenClaw.Security.Vault

Vault / OpenBao 后端密钥解析，基于 [VaultSharp](https://github.com/rajanadar/VaultSharp)。

## AOT 兼容性

`IsAotCompatible=false`。VaultSharp 大量依赖反射（auth 方法、KV 响应反序列化），不适配 NativeAOT trim。

Gateway 通过条件构建边界处理：AOT 发布构建（`dotnet publish`，`_IsPublishing=true and PublishAot=true`）不引用本项目，
并借助 `OPENCLAW_VAULT_EXCLUDED` 编译常量移除 `Program.cs` 中的 `AddOpenClawVaultSecrets` 注册；
JIT 构建（`dotnet build` / `dotnet run` / `dotnet test`）保留完整 Vault 支持。

注意：这是**发布期**的排除——运行时 `Security.Vault.Enabled` 不影响发布产物内容。
AOT 发布产物中 `vault:` 引用按未配置处理（fail-closed，抛 `VaultNotConfiguredException`）。

JIT publishing (`dotnet publish -p:PublishAot=false`) retains Vault support. NativeAOT publishing excludes it and `vault:` references fail closed with `VaultNotConfiguredException`.

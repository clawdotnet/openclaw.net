# OpenClaw.Security.Vault

Vault / OpenBao 后端密钥解析，基于 [VaultSharp](https://github.com/rajanadar/VaultSharp)。

## AOT 兼容性

`IsAotCompatible=false`。VaultSharp 大量依赖反射（auth 方法、KV 响应反序列化），不适配 NativeAOT trim。
引用此项目的 gateway 在做 `dotnet publish -p:PublishAot=true` 时会包含 VaultSharp 全部传递依赖。

如使用 AOT 部署且未启用 Vault（`Security.Vault.Enabled=false`），可通过条件 ProjectReference
或反射剔除减小产物——本仓库暂不实现，按需后续优化。

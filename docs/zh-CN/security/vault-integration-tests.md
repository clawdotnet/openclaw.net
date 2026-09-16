# Vault 集成测试

`OpenClaw.Tests` 测试套件包含针对 Vault 密钥解析器的真实后端集成测试，标记为 `[Trait("Category", "Integration")]`。它们与真实运行的 OpenBao（或 Vault）服务器通过 HTTP 通信。

## 默认行为

除非同时设置 `OPENBAO_ADDR` 与 `OPENBAO_TOKEN` 环境变量，否则集成测试**跳过**。常规 CI 构建通过 `Category!=Integration` 过滤排除它们。

## 本地启动 OpenBao

```bash
docker compose -f deploy/docker-compose/openbao.yml up -d
```

该命令以 dev 模式启动 `openbao/openbao:2.0.0`，监听 `http://127.0.0.1:8200`，root token 为 `root`，并带健康检查（`bao status`）。

> **安全提醒：** `-dev` 模式仅用于集成测试；禁止在任何生产或共享环境使用 `-dev` token。

## 运行集成测试

```bash
OPENBAO_ADDR=http://127.0.0.1:8200 OPENBAO_TOKEN=root \
  dotnet test src/OpenClaw.Tests/OpenClaw.Tests.csproj \
    --filter "Category=Integration"
```

`OPENBAO_ADDR` 可指向任意可达的 OpenBao/Vault 地址；`OPENBAO_TOKEN` 需要具备 `secret` 挂载点上的 KV v2 读写权限。

## 清理

```bash
docker compose -f deploy/docker-compose/openbao.yml down -v
```

## CI

`.github/workflows/ci.yml` 中的可选 `vault-integration` job 在 `ubuntu-latest` runner 上运行相同的 filter。仅当仓库不是 fork 且仓库变量 `RUN_VAULT_INTEGRATION` 为 `true`（Settings → Variables）时触发。

该 job 的步骤：

1. 通过 `deploy/docker-compose/openbao.yml` 启动 OpenBao
2. 等待健康端点（`/v1/sys/health`）
3. 设置 `OPENBAO_ADDR`/`OPENBAO_TOKEN` 后运行 `dotnet test --filter "Category=Integration"`
4. 用 `down -v` 拆除容器

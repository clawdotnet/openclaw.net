# Vault Integration Tests

The `OpenClaw.Tests` suite contains real-backend integration tests for the Vault secret resolver, marked `[Trait("Category", "Integration")]`. They talk to a live OpenBao (or Vault) server over HTTP.

## Default Behavior

Integration tests are **skipped** unless both `OPENBAO_ADDR` and `OPENBAO_TOKEN` environment variables are set. The regular CI build excludes them (`Category!=Integration`).

## Starting OpenBao Locally

```bash
docker compose -f deploy/docker-compose/openbao.yml up -d
```

This starts `openbao/openbao:2.0.0` in dev mode on `http://127.0.0.1:8200` with root token `root` and a healthcheck (`bao status`).

> **Security note:** `-dev` mode is for integration testing only; never use a `-dev` token in production or any shared environment.

## Running the Integration Tests

```bash
OPENBAO_ADDR=http://127.0.0.1:8200 OPENBAO_TOKEN=root \
  dotnet test src/OpenClaw.Tests/OpenClaw.Tests.csproj \
    --filter "Category=Integration"
```

Set `OPENBAO_ADDR` to any reachable OpenBao/Vault address and `OPENBAO_TOKEN` to a token with KV v2 read/write access on the `secret` mount.

## Tearing Down

```bash
docker compose -f deploy/docker-compose/openbao.yml down -v
```

## CI

The opt-in `vault-integration` job in `.github/workflows/ci.yml` runs the same filter on a `ubuntu-latest` runner. It only triggers when the repository is not a fork and the repository variable `RUN_VAULT_INTEGRATION` is set to `true` (Settings → Variables).

What the job does:

1. starts OpenBao via `deploy/docker-compose/openbao.yml`
2. waits for the health endpoint (`/v1/sys/health`)
3. runs `dotnet test --filter "Category=Integration"` with `OPENBAO_ADDR`/`OPENBAO_TOKEN` set
4. tears the container down with `down -v`

# deploy/docker-compose/

Containerized tooling configuration directory.

## openbao.yml

OpenBao server for integration tests (**dev mode only**).

> **Security note:** `-dev` mode is for integration testing only; **never** use a `-dev` token in production or any shared environment.

Start:

```bash
docker compose -f deploy/docker-compose/openbao.yml up -d
```

Environment variables:

- `OPENBAO_ADDR=http://127.0.0.1:8200`
- `OPENBAO_TOKEN=root`

Run the OpenClaw integration tests:

```bash
OPENBAO_ADDR=http://127.0.0.1:8200 OPENBAO_TOKEN=root \
  dotnet test src/OpenClaw.Tests/OpenClaw.Tests.csproj \
    --filter "Category=Integration"
```

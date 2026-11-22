#!/usr/bin/env bash
# 一键起 Keycloak 并完成导入后的必要修补，最后打印 openclaw 侧要用的配置。
#
#   ./scripts/up.sh
set -euo pipefail

START_DIR=$PWD
trap 'cd "$START_DIR"' EXIT
cd "$(dirname "$0")/.."

step() { printf '\n\033[1m==> %s\033[0m\n' "$1"; }

step "1/4 准备 .env"
./scripts/init-env.sh

# shellcheck disable=SC1091
set -a; . ./.env; set +a

step "2/4 启动 Keycloak"
docker compose up -d

step "3/4 等待就绪（不能探 8080，health 在 9000）"
./scripts/wait-keycloak.sh "http://localhost:${KEYCLOAK_MGMT_PORT:-9000}/health/ready" 180

step "4/4 修补 realm 导入后的已知缺口"
./scripts/allow-unmanaged-attributes.sh

cat <<EOF

\033[1mKeycloak 已就绪\033[0m

  控制台  ${KEYCLOAK_PUBLIC_URL}
  账号    ${KEYCLOAK_ADMIN} / ${KEYCLOAK_ADMIN_PASSWORD}
  realm   ${KEYCLOAK_REALM}
  种子用户  demo-operator / demo-admin / demo-viewer（口令见 realm 文件，上线前必改）

\033[1m下一步：启动 openclaw\033[0m

  方式一（推荐，Linux/macOS/Windows 通用）：直接在宿主机跑

    OpenClaw__Security__AuthMode=oidc \\
    OpenClaw__Security__AlwaysRequireAuth=true \\
    OpenClaw__Security__Oidc__Authority=${KEYCLOAK_PUBLIC_URL}/realms/${KEYCLOAK_REALM} \\
    OpenClaw__Security__Oidc__Audience=openclaw \\
    OpenClaw__Security__Oidc__RequireHttpsMetadata=false \\
    OpenClaw__Security__Oidc__RoleClaim=operator_role \\
    OpenClaw__Security__AllowQueryStringToken=true \\
    dotnet run --project ../../src/OpenClaw.Gateway

  方式二（仅 Linux）：把 openclaw 也放进容器

    docker compose -f docker-compose.yml -f docker-compose.openclaw.yml up -d

\033[1m验证\033[0m

  ./scripts/verify.sh
EOF

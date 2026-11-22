#!/usr/bin/env bash
# 生成 deploy/keycloak/.env：随机管理员/数据库口令 + 默认地址。
#
# 用法: ./scripts/init-env.sh [--force]
set -euo pipefail

cd "$(dirname "$0")/.."

ENV_FILE="${ENV_FILE:-.env}"
FORCE="${1:-}"

if [ -f "$ENV_FILE" ] && [ "$FORCE" != "--force" ]; then
  echo "$ENV_FILE 已存在，跳过（要重新生成请加 --force）"
  exit 0
fi

rand() { python -c "import secrets,sys; print(secrets.token_urlsafe(int(sys.argv[1])))" "$1"; }

cat > "$ENV_FILE" <<EOF
# 由 scripts/init-env.sh 生成。含口令，不要提交到版本库。

# ── Keycloak ───────────────────────────────────────────────────
# 对外完整地址。决定 token 里的 iss，必须与 openclaw 的
# Oidc.Authority 以及浏览器地址栏三方逐字一致。
KEYCLOAK_PUBLIC_URL=http://localhost:8081
KEYCLOAK_HTTP_PORT=8081
# 管理口：/health/ready 在这里，不在 8081
KEYCLOAK_MGMT_PORT=9000
KEYCLOAK_REALM=openclaw

KEYCLOAK_ADMIN=admin
KEYCLOAK_ADMIN_PASSWORD=$(rand 18)
KEYCLOAK_DB_PASSWORD=$(rand 24)

# ── openclaw ───────────────────────────────────────────────────
OPENCLAW_PORT=18789
OPENCLAW_MODEL=gpt-4o
# 必填：不填时 openclaw 能起来但对话会失败
MODEL_PROVIDER_KEY=
MODEL_PROVIDER_ENDPOINT=
# 本地 HTTP 自测用 false；生产走 HTTPS 时改 true
OPENCLAW_OIDC_REQUIRE_HTTPS=false
OPENCLAW_ALLOW_QUERY_TOKEN=true
EOF

chmod 600 "$ENV_FILE"
echo "已生成 $ENV_FILE（权限 600）"
echo
echo "别忘了填 MODEL_PROVIDER_KEY，否则对话会失败。"

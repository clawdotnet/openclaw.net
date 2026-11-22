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

ADMIN_PASSWORD=$(rand 18)
DB_PASSWORD=$(rand 24)
[ -n "$ADMIN_PASSWORD" ] || { echo "生成 KEYCLOAK_ADMIN_PASSWORD 失败" >&2; exit 1; }
[ -n "$DB_PASSWORD" ] || { echo "生成 KEYCLOAK_DB_PASSWORD 失败" >&2; exit 1; }

umask 077
TMP_ENV=$(mktemp "${ENV_FILE}.tmp.XXXXXX")
cleanup() { [ -z "${TMP_ENV:-}" ] || rm -f "$TMP_ENV"; }
trap cleanup EXIT
trap 'exit 129' HUP
trap 'exit 130' INT
trap 'exit 143' TERM

cat > "$TMP_ENV" <<EOF
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
KEYCLOAK_ADMIN_PASSWORD=${ADMIN_PASSWORD}
KEYCLOAK_DB_PASSWORD=${DB_PASSWORD}

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

chmod 600 "$TMP_ENV"
mv -f "$TMP_ENV" "$ENV_FILE"
TMP_ENV=""
echo "已生成 $ENV_FILE（权限 600）"
echo
echo "别忘了填 MODEL_PROVIDER_KEY，否则对话会失败。"

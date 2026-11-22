#!/usr/bin/env bash
# 仅允许管理员编辑未在 User Profile 里显式声明的 attribute，并复核 realm 导入结果。
#
# ── 为什么必须做这一步 ────────────────────────────────────────────
# Keycloak 26 默认 unmanagedAttributePolicy=None：通过 Admin API 给用户写一个
# 没在 user profile 里声明的 attribute，会返回 **204（看起来成功）**，但实际
# 被静默丢弃 —— 再读回来是 "attributes": null。
#
# 后果：operator_role 永远存不进去 → token 里没有该 claim →
# openclaw 侧 Normalize(null) 返回 viewer → 所有用户降级成 viewer，
# 连聊天都进不去。而 Keycloak 与 openclaw 两边日志都不会报错。
#
# ENABLED 会允许终端用户编辑未声明属性，包括进入 access token 的 operator_role，
# 造成用户自行提升角色。ADMIN_EDIT 保留管理员写入能力，同时禁止终端用户编辑这些属性。
# 更严格的部署可将 operator_role 声明为仅管理员可编辑的 managed attribute。
# 导入 realm 后应用 ADMIN_EDIT；脚本幂等，可重复执行。
#
# 用法: ./scripts/allow-unmanaged-attributes.sh
set -euo pipefail

cd "$(dirname "$0")/.."

ENV_FILE="${ENV_FILE:-.env}"
[ -f "$ENV_FILE" ] || { echo "找不到 $ENV_FILE，先跑 scripts/init-env.sh" >&2; exit 1; }

env_get() {
  python - "$ENV_FILE" "$1" <<'PY'
import re, sys
try:
    text = open(sys.argv[1], encoding="utf-8").read()
except FileNotFoundError:
    sys.exit(0)
m = re.search(r"^%s=(.*)$" % re.escape(sys.argv[2]), text, re.M)
if not m:
    sys.exit(0)
v = m.group(1).strip()
if len(v) >= 2 and v[0] == v[-1] and v[0] in "'\"":
    v = v[1:-1]
print(v)
PY
}

KC_URL="${KEYCLOAK_PUBLIC_URL:-$(env_get KEYCLOAK_PUBLIC_URL)}"
KC_URL="${KC_URL%/}"
REALM="${KEYCLOAK_REALM:-$(env_get KEYCLOAK_REALM)}"
REALM="${REALM:-openclaw}"
ADMIN="${KEYCLOAK_ADMIN:-$(env_get KEYCLOAK_ADMIN)}"
ADMIN="${ADMIN:-admin}"
ADMIN_PW="${KEYCLOAK_ADMIN_PASSWORD:-$(env_get KEYCLOAK_ADMIN_PASSWORD)}"

# admin-cli 在 master realm，不是业务 realm
TOKEN=$(curl -fsS --max-time 15 -X POST "${KC_URL}/realms/master/protocol/openid-connect/token" \
  --data-urlencode "grant_type=password" --data-urlencode "client_id=admin-cli" \
  --data-urlencode "username=${ADMIN}" --data-urlencode "password=${ADMIN_PW}" \
  | python -c 'import json,sys;print(json.load(sys.stdin)["access_token"])') \
  || { echo "取 admin token 失败，检查 KEYCLOAK_ADMIN / KEYCLOAK_ADMIN_PASSWORD" >&2; exit 1; }

WORK=$(mktemp -d "./.kc-work-XXXXXX")
trap 'rm -rf "$WORK"' EXIT

curl -fsS --max-time 15 -H "Authorization: Bearer $TOKEN" \
  "${KC_URL}/admin/realms/${REALM}/users/profile" -o "$WORK/profile.json" \
  || { echo "拉不到 realm ${REALM} 的 user profile" >&2; exit 1; }

PROFILE_FILE="$WORK/profile.json" WORK_DIR="$WORK" python - <<'PY'
import json, os, sys

path = os.environ["PROFILE_FILE"]
cfg = json.load(open(path, encoding="utf-8"))
current = cfg.get("unmanagedAttributePolicy")
if current == "ADMIN_EDIT":
    print("unmanagedAttributePolicy 已是 ADMIN_EDIT，无需改动")
    sys.exit(0)

cfg["unmanagedAttributePolicy"] = "ADMIN_EDIT"
out = os.path.join(os.environ["WORK_DIR"], "profile-new.json")
with open(out, "w", encoding="utf-8", newline="\n") as f:
    json.dump(cfg, f, ensure_ascii=False)
print("unmanagedAttributePolicy: %s → ADMIN_EDIT" % (current or "None"))
PY

if [ -f "$WORK/profile-new.json" ]; then
  CODE=$(curl -sS --max-time 15 -o "$WORK/put.txt" -w '%{http_code}' -X PUT \
    -H "Authorization: Bearer $TOKEN" -H "Content-Type: application/json" \
    --data-binary "@$WORK/profile-new.json" \
    "${KC_URL}/admin/realms/${REALM}/users/profile")
  if [ "$CODE" != "200" ] && [ "$CODE" != "204" ]; then
    echo "更新失败 HTTP ${CODE}" >&2
    sed 's/^/  /' "$WORK/put.txt" >&2
    exit 1
  fi
  echo "已更新（HTTP ${CODE}）"
fi

# 复核 + 检查种子用户的 attribute 是否真的存下来了
curl -fsS --max-time 15 -H "Authorization: Bearer $TOKEN" \
  "${KC_URL}/admin/realms/${REALM}/users/profile" \
  | python -c '
import json, sys
cfg = json.load(sys.stdin)
policy = cfg.get("unmanagedAttributePolicy")
print("复核 unmanagedAttributePolicy =", policy)
sys.exit(0 if policy == "ADMIN_EDIT" else 1)
' || exit 1

echo
echo "检查种子用户的 operator_role："
curl -fsS --max-time 15 -H "Authorization: Bearer $TOKEN" \
  "${KC_URL}/admin/realms/${REALM}/users?max=20" \
  | python -c '
import json, sys
users = json.load(sys.stdin)
if not users:
    print("  realm 里没有用户（realm 导入可能失败）")
    sys.exit(1)
missing = []
for u in users:
    role = (u.get("attributes") or {}).get("operator_role")
    if role:
        print("  %-16s operator_role=%s" % (u["username"], role[0]))
    else:
        print("  %-16s operator_role=<缺失>" % u["username"])
        missing.append(u["username"])
if missing:
    print()
    print("  有用户缺 operator_role，openclaw 侧会把他们当 viewer。")
    print("  由 realm 管理员补上：Users → <user> → Attributes → operator_role = viewer|operator|admin")
    sys.exit(1)
'

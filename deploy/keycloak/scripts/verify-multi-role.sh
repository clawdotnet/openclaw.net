#!/usr/bin/env bash
# 真机验证：多角色 token 不被静默降级。
#
# ── 这个脚本解决什么问题 ──────────────────────────────────────────
# 单元测试用伪造的 ClaimsPrincipal 复现多值 claim，但无法证明「Keycloak 真的
# 会产出一个数组 claim」以及「openclaw 真的能从中取出最高档」。这里用真实
# Keycloak 管理 API 给一个用户挂多个 realm role，取回真实 token，再打真实网关。
#
# 与其他脚本的关系：verify.sh 验的是常规配置（单值 operator_role），
# 本脚本专门验多值聚合；两者互补，不重叠。
#
# 前置：
#   ./scripts/up.sh                      # Keycloak 已就绪
#   openclaw 以 RoleClaim=realm_roles 启动（脚本会提示所需命令）
#
# 用法：
#   ./scripts/verify-multi-role.sh                    # 只验 claim 层
#   ./scripts/verify-multi-role.sh http://localhost:18789   # 连网关一起验
set -euo pipefail

cd "$(dirname "$0")/.."

ENV_FILE="${ENV_FILE:-.env}"
[ -f "$ENV_FILE" ] || { echo "找不到 $ENV_FILE，先跑 ./scripts/up.sh" >&2; exit 1; }

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
ADMIN_PW="${KEYCLOAK_ADMIN_PASSWORD:-$(env_get KEYCLOAK_ADMIN_PASSWORD)}"
ADMIN="${KEYCLOAK_ADMIN:-$(env_get KEYCLOAK_ADMIN)}"
ADMIN="${ADMIN:-admin}"
GATEWAY="${1:-}"

WORK=$(mktemp -d "./.kc-multi-XXXXXX")

GRANT_CLEANUP=false
cleanup() {
  local status=$?
  local cleanup_succeeded=true
  trap - EXIT HUP INT TERM
  if [ "$GRANT_CLEANUP" = true ] && [ -s "$WORK/cleanup.json" ]; then
    local code
    code=$(curl -sS --max-time 10 -o "$WORK/cleanup-response.txt" -w '%{http_code}' \
      -X DELETE -H "Authorization: Bearer $AT" -H "Content-Type: application/json" \
      --data-binary "@$WORK/cleanup.json" \
      "${KC_URL}/admin/realms/${REALM}/users/${USER_ID}/role-mappings/realm" || printf '000')
    if [ "$code" != "204" ]; then
      echo "清理本次新增 realm role mapping 失败（HTTP ${code}）" >&2
      [ ! -s "$WORK/cleanup-response.txt" ] || sed 's/^/  /' "$WORK/cleanup-response.txt" >&2
      cleanup_succeeded=false
      [ "$status" -ne 0 ] || status=1
    else
      echo "已清理本次新增的 realm role mapping"
    fi
  fi
  if [ "$cleanup_succeeded" = true ]; then
    rm -rf "$WORK"
  else
    echo "realm '${REALM}' 中用户 '${USER_NAME}'（${USER_ID}）的测试角色映射尚未清理。" >&2
    echo "已保留清理清单：${PWD}/${WORK}/cleanup.json" >&2
    echo "恢复：在 Keycloak 管理控制台打开 realm '${REALM}'、用户 '${USER_NAME}' 的 Role mapping，" >&2
    echo "仅移除清单中的映射；确认移除后再删除目录：${PWD}/${WORK}" >&2
  fi
  exit "$status"
}
trap cleanup EXIT
trap 'exit 129' HUP
trap 'exit 130' INT
trap 'exit 143' TERM

PASS=0 FAILED=0 INCONCLUSIVE=0
ok()   { printf '  \033[32mOK  \033[0m %s\n' "$1"; PASS=$((PASS+1)); }
bad()  { printf '  \033[31mFAIL\033[0m %s\n' "$1"; FAILED=$((FAILED+1)); }
inconclusive() { printf '  \033[33mINCONCLUSIVE\033[0m %s\n' "$1"; INCONCLUSIVE=$((INCONCLUSIVE+1)); }
hdr()  { printf '\n\033[1m%s\033[0m\n' "$1"; }

hdr "0. 准备：拿 admin token"

AT=$(curl -fsS --max-time 15 -X POST "${KC_URL}/realms/master/protocol/openid-connect/token" \
  --data-urlencode "grant_type=password" --data-urlencode "client_id=admin-cli" \
  --data-urlencode "username=${ADMIN}" --data-urlencode "password=${ADMIN_PW}" \
  | python -c 'import json,sys;print(json.load(sys.stdin)["access_token"])') \
  || { echo "取 admin token 失败，检查 .env 里的 KEYCLOAK_ADMIN_PASSWORD" >&2; exit 1; }
ok "拿到 admin token"

# ── 决定用哪一组 realm role 来构造"多角色" ──────────────────────
# openclaw 只认字面 viewer/operator/admin，而 realm 里的业务角色叫
# openclaw_*。为了证明「多值里含 admin 就不会被降级」，需要 token 里真的出现
# 字面 "admin" 和另一个角色。做法：在 realm 里临时建 viewer/admin 两个字面角色
# （若不存在），赋给测试用户，验证完再撤销。
hdr "1. 在 realm 里确保字面角色 viewer / admin 存在"

ensure_role() {
  local name="$1"
  local code
  code=$(curl -s -o "$WORK/role.json" -w '%{http_code}' --max-time 10 \
    -H "Authorization: Bearer $AT" "${KC_URL}/admin/realms/${REALM}/roles/${name}")
  if [ "$code" = "200" ]; then
    return 0
  fi
  curl -fsS --max-time 10 -X POST -H "Authorization: Bearer $AT" \
    -H "Content-Type: application/json" \
    -d "{\"name\":\"${name}\",\"description\":\"多角色聚合验证用，可删\"}" \
    "${KC_URL}/admin/realms/${REALM}/roles" >/dev/null 2>&1 || true
}

ensure_role viewer
ensure_role admin
ok "viewer / admin 角色就位"

hdr "2. 给测试用户同时授予 viewer + admin（构造真实多值 token）"

USER_NAME="${MULTI_ROLE_USER:-demo-admin}"
USER_ID=$(curl -fsS --max-time 10 -H "Authorization: Bearer $AT" \
  "${KC_URL}/admin/realms/${REALM}/users?username=${USER_NAME}&exact=true" \
  | python -c 'import json,sys;u=json.load(sys.stdin);print(u[0]["id"] if u else "")')

if [ -z "$USER_ID" ]; then
  bad "找不到用户 ${USER_NAME}"
  exit 1
fi

# 取两个字面角色的表示
curl -fsS --max-time 10 -H "Authorization: Bearer $AT" \
  "${KC_URL}/admin/realms/${REALM}/roles/viewer" -o "$WORK/r_viewer.json"
curl -fsS --max-time 10 -H "Authorization: Bearer $AT" \
  "${KC_URL}/admin/realms/${REALM}/roles/admin" -o "$WORK/r_admin.json"

python - "$WORK" <<'PY'
import json, sys, os
w = sys.argv[1]
roles = [json.load(open(os.path.join(w, "r_viewer.json"), encoding="utf-8")),
         json.load(open(os.path.join(w, "r_admin.json"), encoding="utf-8"))]
payload = [{"id": r["id"], "name": r["name"]} for r in roles]
json.dump(payload, open(os.path.join(w, "grant.json"), "w", encoding="utf-8"))
PY

curl -fsS --max-time 10 -H "Authorization: Bearer $AT" \
  "${KC_URL}/admin/realms/${REALM}/users/${USER_ID}/role-mappings/realm" \
  -o "$WORK/existing-mappings.json"

python - "$WORK" <<'PY'
import json, os, sys
w = sys.argv[1]
targets = json.load(open(os.path.join(w, "grant.json"), encoding="utf-8"))
existing = json.load(open(os.path.join(w, "existing-mappings.json"), encoding="utf-8"))
existing_ids = {role["id"] for role in existing}
cleanup = [role for role in targets if role["id"] not in existing_ids]
with open(os.path.join(w, "cleanup.json"), "w", encoding="utf-8") as f:
    if cleanup:
        json.dump(cleanup, f)
PY

# Activate cleanup before POST: even a lost/error response may follow a committed grant.
GRANT_CLEANUP=true
curl -fsS --max-time 10 -X POST -H "Authorization: Bearer $AT" \
  -H "Content-Type: application/json" --data-binary "@$WORK/grant.json" \
  "${KC_URL}/admin/realms/${REALM}/users/${USER_ID}/role-mappings/realm" >/dev/null \
  && ok "已授予 viewer + admin" || bad "授予角色失败"

hdr "3. 取回真实 token，检查 realm_roles 是否为多值"

TOKEN=$(curl -fsS --max-time 15 -X POST "${KC_URL}/realms/${REALM}/protocol/openid-connect/token" \
  --data-urlencode "grant_type=password" --data-urlencode "client_id=openclaw-verify" \
  --data-urlencode "username=${USER_NAME}" --data-urlencode "password=${MULTI_ROLE_PASS:-change-me-admin}" \
  --data-urlencode "scope=openid" \
  | python -c 'import json,sys;print(json.load(sys.stdin)["access_token"])') \
  || { bad "取用户 token 失败（口令被改过？用 MULTI_ROLE_PASS 覆盖）"; exit 1; }

print_token_roles() {
  KC_TOKEN="$1" python -c '
import base64, json, os, sys
t = os.environ["KC_TOKEN"]
seg = t.split(".")[1]; seg += "=" * (-len(seg) % 4)
p = json.loads(base64.urlsafe_b64decode(seg))
roles = p.get("realm_roles")
if not isinstance(roles, list):
    print("realm_roles 不是数组：%r" % (roles,))
    sys.exit(1)
print(" ".join(roles))
' 2>/dev/null
}

ROLES=$(print_token_roles "$TOKEN" || true)
if [ -z "$ROLES" ]; then
  bad "token 里没有 realm_roles 数组"
else
  ok "realm_roles = [${ROLES}]"
  case " $ROLES " in
    *" admin "*) ok "数组里含 admin" ;;
    *) bad "数组里没有 admin，无法验证降级问题" ;;
  esac
  # Require a recognized lower role before admin so the first-claim regression is deterministic.
  if python -c '
import sys
roles = sys.argv[1].split()
admin_index = roles.index("admin") if "admin" in roles else -1
sys.exit(0 if admin_index > 0 and any(role in ("viewer", "operator") for role in roles[:admin_index]) else 1)
' "$ROLES"; then
    ok "已知低权限角色排在 admin 之前 —— 老实现（FindFirstValue）会降级"
  else
    bad "没有在 admin 之前的已知低权限角色，回归检查结果不确定"
  fi
fi

hdr "4. 用同一份 token 判定最高档位（与 EndpointHelpers 同一条逻辑）"

KC_TOKEN="$TOKEN" python -c '
import base64, json, os, sys

t = os.environ["KC_TOKEN"]
seg = t.split(".")[1]; seg += "=" * (-len(seg) % 4)
p = json.loads(base64.urlsafe_b64decode(seg))
roles = [r for r in (p.get("realm_roles") or []) if isinstance(r, str)]

RANK = {"viewer": 1, "operator": 2, "admin": 3}
best, best_rank = "viewer", 1
for r in roles:
    cand = r.strip().lower()
    rank = RANK.get(cand, -1)
    if rank > best_rank:
        best, best_rank = cand, rank

first = roles[0].strip().lower() if roles else ""
first_rank = RANK.get(first, 1)

print("  按新逻辑（取最高）：%s" % best)
print("  按老逻辑（取首个）：%s" % ("viewer" if first_rank < 1 else first))
sys.exit(0 if best == "admin" else 1)
' && ok "最高档位解析为 admin" || bad "最高档位解析不是 admin"

hdr "5. 真实网关：admin 专属端点是否放行"

if [ -z "$GATEWAY" ]; then
  echo "  跳过：未提供 gateway 地址"
  echo "  要验："
  echo "    1) 以 RoleClaim=realm_roles 启动网关（见下方提示）"
  echo "    2) $0 http://localhost:18789"
else
  GATEWAY="${GATEWAY%/}"

  # /tools/approve 需要 admin。token 里含 admin ⇒ 应通过权限判定，
  # 得到一个业务错误（404 无此审批单），而不是 403。
  CODE=$(curl -s -o "$WORK/gw.txt" -w '%{http_code}' --max-time 10 -X POST \
    -H "Authorization: Bearer $TOKEN" \
    "${GATEWAY}/tools/approve?approvalId=multi-role-probe&approved=true" 2>/dev/null || echo 000)

  case "$CODE" in
    404) ok "admin 通过权限判定（404=无此审批单，符合预期）" ;;
    400) inconclusive "admin 探测缺少 requester identity，未验证角色授权" ;;
    403)
      bad "admin 被拒（403）—— 多角色聚合没生效"
      sed 's/^/     /' "$WORK/gw.txt" >&2
      echo "     网关是否以 RoleClaim=realm_roles 启动？当前若是 operator_role，" >&2
      echo "     token 里的 operator_role=admin 也应通过——请核对启动参数。" >&2 ;;
    401)
      bad "401 未认证 —— 网关的 Authority 与 Keycloak 不一致，或 JWKS 缓存过期"
      sed 's/^/     /' "$WORK/gw.txt" >&2 ;;
    405) bad "405 —— /tools/approve 是 POST 路由（脚本已用 POST，检查网关版本）" ;;
    000) bad "连不上 ${GATEWAY}" ;;
    *)   bad "返回 ${CODE}，期望 404" ;;
  esac
fi

hdr "6. 反向对照：只有 viewer 时必须是 403"

if [ -z "$GATEWAY" ]; then
  echo "  跳过（未提供 gateway）"
else
  VUSER="${MULTI_ROLE_VIEWER_USER:-demo-viewer}"
  VTOKEN=$(curl -fsS --max-time 15 -X POST "${KC_URL}/realms/${REALM}/protocol/openid-connect/token" \
    --data-urlencode "grant_type=password" --data-urlencode "client_id=openclaw-verify" \
    --data-urlencode "username=${VUSER}" --data-urlencode "password=${MULTI_ROLE_VIEWER_PASS:-change-me-viewer}" \
    --data-urlencode "scope=openid" \
    | python -c 'import json,sys;print(json.load(sys.stdin)["access_token"])' 2>/dev/null || true)

  if [ -z "$VTOKEN" ]; then
    bad "取 viewer 用户 token 失败"
  else
    CODE=$(curl -s -o "$WORK/gwv.txt" -w '%{http_code}' --max-time 10 -X POST \
      -H "Authorization: Bearer $VTOKEN" \
      "${GATEWAY}/tools/approve?approvalId=multi-role-probe&approved=true" 2>/dev/null || echo 000)
    case "$CODE" in
      403) ok "viewer 被拒（403）—— 说明上一步的 404 确实来自 admin 角色" ;;
      400) inconclusive "viewer 探测缺少 requester identity，未验证角色授权" ;;
      404) bad "viewer 也拿到 404 —— 权限校验被整体跳过，这是越权漏洞" ;;
      401) bad "401 —— 网关验签配置问题" ;;
      000) bad "连不上 ${GATEWAY}" ;;
      *)   bad "返回 ${CODE}，期望 403" ;;
    esac
  fi
fi

echo
if [ "$FAILED" -gt 0 ]; then
  printf '\033[31m%d 项失败\033[0m，%d 项通过，%d 项无法判定\n' "$FAILED" "$PASS" "$INCONCLUSIVE"
  exit 1
elif [ "$INCONCLUSIVE" -gt 0 ]; then
  printf '\033[33m结果无法判定\033[0m（%d 项通过，%d 项无法判定）\n' "$PASS" "$INCONCLUSIVE"
  exit 2
else
  printf '\033[32m全部通过\033[0m（%d 项）\n' "$PASS"
  exit 0
fi

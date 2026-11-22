#!/usr/bin/env bash
# 验证 Keycloak 侧产出的 claim 能被 openclaw 正确解析，且角色真的影响权限。
#
#   ./scripts/verify.sh                    # 只验 Keycloak 与 claim
#   ./scripts/verify.sh http://localhost:18789   # 连上正在跑的 openclaw 一起验
#
# 不验「登录成功」就算过 —— 只验 claim 是否存在、角色是否真的生效。
set -euo pipefail

cd "$(dirname "$0")/.."
ENV_FILE="${ENV_FILE:-.env}"

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
GATEWAY="${1:-}"

WORK=$(mktemp -d "./.kc-verify-XXXXXX")
trap 'rm -rf "$WORK"' EXIT

PASS=0 FAILED=0 INCONCLUSIVE=0
ok()   { printf '  \033[32mOK  \033[0m %s\n' "$1"; PASS=$((PASS+1)); }
bad()  { printf '  \033[31mFAIL\033[0m %s\n' "$1"; FAILED=$((FAILED+1)); }
inconclusive() { printf '  \033[33mINCONCLUSIVE\033[0m %s\n' "$1"; INCONCLUSIVE=$((INCONCLUSIVE+1)); }
hdr()  { printf '\n\033[1m%s\033[0m\n' "$1"; }

# 种子用户口令与 realm 文件一致；上线前应改掉，那时用环境变量覆盖
OP_USER="${KC_TEST_OPERATOR:-demo-operator}"
OP_PASS="${KC_TEST_OPERATOR_PASS:-change-me-operator}"
AD_USER="${KC_TEST_ADMIN:-demo-admin}"
AD_PASS="${KC_TEST_ADMIN_PASS:-change-me-admin}"

token_for() {
  curl -fsS --max-time 15 -X POST "${KC_URL}/realms/${REALM}/protocol/openid-connect/token" \
    --data-urlencode "grant_type=password" --data-urlencode "client_id=openclaw-verify" \
    --data-urlencode "username=$1" --data-urlencode "password=$2" --data-urlencode "scope=openid" \
    | python -c 'import json,sys;print(json.load(sys.stdin)["access_token"])' 2>/dev/null
}

hdr "1. discovery 与 issuer"

if ! curl -fsS --max-time 10 \
    "${KC_URL}/realms/${REALM}/.well-known/openid-configuration" -o "$WORK/oidc.json"; then
  bad "拉不到 discovery：${KC_URL}/realms/${REALM}/.well-known/openid-configuration"
  echo "     Keycloak 起来了吗？docker logs openclaw-keycloak --tail 40" >&2
  exit 1
fi
ISSUER=$(python -c "import json;print(json.load(open('$WORK/oidc.json'))['issuer'])")

# issuer 里的 /auth 前缀是 Keycloak 17+ 已去掉的老路径
case "$ISSUER" in
  */auth/realms/*) bad "issuer 含 /auth 前缀（Keycloak 17+ 已去掉）：${ISSUER}" ;;
  *) ok "issuer = ${ISSUER}" ;;
esac

# 这一条最重要：issuer 必须与 .env 里给 openclaw 的 Authority 完全一致，
# 否则 openclaw 会以 issuer 不匹配拒掉所有 token。
EXPECTED_AUTHORITY="${KC_URL}/realms/${REALM}"
if [ "$ISSUER" = "$EXPECTED_AUTHORITY" ]; then
  ok "issuer 与 openclaw 的 Authority 一致"
else
  bad "issuer($ISSUER) ≠ Authority($EXPECTED_AUTHORITY)"
  echo "     检查 KEYCLOAK_PUBLIC_URL 与 --hostname 是否逐字一致" >&2
fi

hdr "2. JWKS 非对称且带 kid"

if curl -fsS --max-time 10 "${KC_URL}/realms/${REALM}/protocol/openid-connect/certs" -o "$WORK/jwks.json"; then
  if KEYCLOAK_FIXTURE_JWKS="$WORK/jwks.json" python -c '
import json, os, sys
keys = json.load(open(os.environ["KEYCLOAK_FIXTURE_JWKS"])).get("keys") or []
signing = [k for k in keys if k.get("use", "sig") == "sig" and k.get("alg", "").startswith(("RS", "ES", "PS"))]
if not signing:
    sys.exit("没有可用的签名公钥")
for k in signing:
    if not k.get("kid"):
        sys.exit("签名公钥缺 kid")
print(", ".join(k["kid"][:8] + "/" + k["alg"] for k in signing))
' 2>"$WORK/jwks.err" >"$WORK/jwks.txt"; then
    ok "签名密钥：$(cat "$WORK/jwks.txt")"
  else
    bad "JWKS 不满足要求：$(cat "$WORK/jwks.err")"
  fi
else
  bad "拉不到 JWKS"
fi

hdr "3. token 里的 claim（openclaw 靠这三个认人）"

if OP_TOKEN=$(token_for "$OP_USER" "$OP_PASS"); then
  ok "取到 operator 的 token"

  if KC_TOKEN="$OP_TOKEN" python -c '
import base64, json, os, sys, uuid
t = os.environ["KC_TOKEN"]
seg = t.split(".")[1]; seg += "=" * (-len(seg) % 4)
p = json.loads(base64.urlsafe_b64decode(seg))

errs = []
sub = p.get("sub")
if not sub:
    errs.append("缺 sub")
else:
    try:
        uuid.UUID(sub)
    except ValueError:
        errs.append("sub 不是 UUID: %r" % sub)
if not p.get("preferred_username"):
    errs.append("缺 preferred_username")
role = p.get("operator_role")
if not role:
    errs.append("缺 operator_role（Keycloak 26 默认会静默丢弃未声明的 attribute）")
elif not isinstance(role, str):
    errs.append("operator_role 不是单值字符串，实际为 %r" % (role,))
elif role not in ("viewer", "operator", "admin"):
    errs.append("operator_role=%r 不在 viewer/operator/admin 之内，会被降级成 viewer" % role)

if errs:
    # 写到 stderr：本块的 stdout 被重定向到 claim.txt（成功分支要用），
    # 失败分支读的是 claim.err，写错流会让诊断信息变成空行。
    print("\n".join("    " + e for e in errs), file=sys.stderr)
    sys.exit(1)
print("sub=%s operator_role=%s user=%s" % (sub[:8], role, p.get("preferred_username")))
' 2>"$WORK/claim.err" >"$WORK/claim.txt"; then
    ok "claim 完整：$(cat "$WORK/claim.txt")"
  else
    bad "claim 不完整："
    cat "$WORK/claim.err" >&2
  fi
else
  bad "取 token 失败（openclaw-verify client 是否启用？种子用户口令是否被改过？）"
  echo "     可用 KC_TEST_OPERATOR / KC_TEST_OPERATOR_PASS 覆盖" >&2
fi

hdr "4. 角色是否真的影响权限"

if [ -n "$GATEWAY" ]; then
  GATEWAY="${GATEWAY%/}"
  if [ -n "${OP_TOKEN:-}" ]; then
    # /tools/approve 需要 admin。operator 应被 403 拒。
    # 必须是 POST：该路由由 MapPost 注册，GET 会得到 405。
    CODE=$(curl -s -o "$WORK/op.txt" -w '%{http_code}' --max-time 10 -X POST \
      -H "Authorization: Bearer $OP_TOKEN" \
      "${GATEWAY}/tools/approve?approvalId=probe&approved=true" 2>/dev/null || echo 000)
    case "$CODE" in
      403) ok "operator 被拒（403），角色下限生效" ;;
      400) inconclusive "operator 探测缺少 requester identity，未验证角色授权" ;;
      000) bad "连不上 ${GATEWAY}" ;;
      404) bad "operator 竟然通过了 admin 判定（404=权限已过），角色映射有问题" ;;
      405) bad "operator 请求 405：/tools/approve 是 POST 路由，方法用错了" ;;
      *)   bad "operator 请求返回 ${CODE}，期望 403" ;;
    esac
  fi

  if AD_TOKEN=$(token_for "$AD_USER" "$AD_PASS"); then
    CODE=$(curl -s -o "$WORK/ad.txt" -w '%{http_code}' --max-time 10 -X POST \
      -H "Authorization: Bearer $AD_TOKEN" \
      "${GATEWAY}/tools/approve?approvalId=probe&approved=true" 2>/dev/null || echo 000)
    case "$CODE" in
      # 404 说明已通过 admin 权限判定，只是这个审批 id 不存在 —— 期望结果
      404) ok "admin 通过权限判定（404=无此审批单，符合预期）" ;;
      400) inconclusive "admin 探测缺少 requester identity，未验证角色授权" ;;
      403) bad "admin 被拒（403），operator_role=admin 没生效" ;;
      000) bad "连不上 ${GATEWAY}" ;;
      405) bad "admin 请求 405：方法用错了，应为 POST" ;;
      *)   bad "admin 请求返回 ${CODE}，期望 404" ;;
    esac
  else
    bad "取不到 admin 的 token"
  fi

  # 无 token / 伪造 token 必须被拒
  CODE=$(curl -s -o /dev/null -w '%{http_code}' --max-time 10 "${GATEWAY}/api/contracts" 2>/dev/null || echo 000)
  [ "$CODE" = "401" ] && ok "无 token → 401" || bad "无 token 返回 ${CODE}，期望 401"

  FORGED=$(OP_TOKEN="${OP_TOKEN:-}" python -c "
import os
t = os.environ.get('OP_TOKEN', '')
if not t:
    raise SystemExit(0)
p = t.split('.')
print(p[0] + '.' + p[1] + '.' + 'A' * len(p[2]))
" 2>/dev/null || true)
  if [ -n "$FORGED" ] && [ -n "${OP_TOKEN:-}" ]; then
    CODE=$(curl -s -o /dev/null -w '%{http_code}' --max-time 10 \
      -H "Authorization: Bearer $FORGED" "${GATEWAY}/api/contracts" 2>/dev/null || echo 000)
    [ "$CODE" = "401" ] && ok "伪造签名 → 401" || bad "伪造签名返回 ${CODE}，期望 401"
  fi
else
  echo "  跳过：未提供 gateway 地址"
  echo "  要一并验证：$0 http://localhost:18789"
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

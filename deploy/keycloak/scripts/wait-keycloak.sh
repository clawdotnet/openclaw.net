#!/usr/bin/env bash
# 等待 Keycloak 就绪。
#
# 关键：/health/ready 在**管理端口 9000**，不在 8080。--health-enabled=true
# 只让管理口多出 /health 系列路由，8080 上访问 /health/ready 永远是 404 ——
# 这个 404 很容易被误读成"Keycloak 没起来"或"realm 导入失败"。
#
# Keycloak 26 镜像基于 ubi-micro（无 curl/wget/bash），只能从宿主机探。
#
# 用法: ./scripts/wait-keycloak.sh [url] [timeout_seconds]
set -euo pipefail

URL="${1:-http://localhost:9000/health/ready}"
TIMEOUT="${2:-180}"
DEADLINE=$(( $(date +%s) + TIMEOUT ))

echo "waiting for ${URL} (timeout ${TIMEOUT}s)"

while :; do
  code=$(curl -s -o /dev/null -w '%{http_code}' --max-time 3 "$URL" 2>/dev/null || true)
  if [ "$code" = "200" ]; then
    echo "keycloak is ready"
    exit 0
  fi
  if [ "$(date +%s)" -ge "$DEADLINE" ]; then
    echo "TIMEOUT after ${TIMEOUT}s, last status=${code:-none}" >&2
    echo "查看日志: docker logs openclaw-keycloak --tail 80" >&2
    exit 1
  fi
  sleep 2
done

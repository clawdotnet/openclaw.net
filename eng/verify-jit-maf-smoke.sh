#!/usr/bin/env bash
set -euo pipefail

ROOT_DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
ARTIFACTS_DIR="${JIT_MAF_ARTIFACTS_DIR:-$ROOT_DIR/artifacts/jit-maf}"
WORK_DIR="$(mktemp -d "${TMPDIR:-/tmp}/openclaw-jit-maf-smoke.XXXXXX")"
KEEP_WORK_DIR="${KEEP_JIT_MAF_SMOKE_DIR:-0}"

cleanup() {
  if [[ "$KEEP_WORK_DIR" != "1" ]]; then
    rm -rf "$WORK_DIR"
  else
    echo "Keeping work dir: $WORK_DIR"
  fi
}
trap cleanup EXIT

mkdir -p "$ARTIFACTS_DIR/gateway" "$WORK_DIR/memory"

GATEWAY_CONFIG="$WORK_DIR/gateway.maf.smoke.json"
cat > "$GATEWAY_CONFIG" <<JSON
{
  "OpenClaw": {
    "Port": 19919,
    "Runtime": {
      "Mode": "jit",
      "Orchestrator": "maf"
    },
    "Memory": {
      "StoragePath": "$WORK_DIR/memory"
    },
    "Plugins": {
      "DynamicNative": {
        "Enabled": true
      }
    }
  }
}
JSON

echo "Publishing JIT gateway with the MAF experiment enabled..."
dotnet publish "$ROOT_DIR/src/OpenClaw.Gateway/OpenClaw.Gateway.csproj" \
  -c Release \
  -p:PublishAot=false \
  -p:OpenClawEnableMafExperiment=true \
  -o "$ARTIFACTS_DIR/gateway"

resolve_binary() {
  local dir="$1"
  local base="$2"
  if [[ -x "$dir/$base" ]]; then
    echo "$dir/$base"
    return 0
  fi
  if [[ -x "$dir/$base.exe" ]]; then
    echo "$dir/$base.exe"
    return 0
  fi
  echo "Expected binary '$base' not found in $dir" >&2
  return 1
}

GATEWAY_BIN="$(resolve_binary "$ARTIFACTS_DIR/gateway" "OpenClaw.Gateway")"

echo "Running published JIT gateway --doctor..."
MODEL_PROVIDER_KEY="smoke-test-key" "$GATEWAY_BIN" --config "$GATEWAY_CONFIG" --doctor >/tmp/openclaw-jit-maf-doctor.log 2>&1

echo "Starting published JIT gateway..."
MODEL_PROVIDER_KEY="smoke-test-key" "$GATEWAY_BIN" --config "$GATEWAY_CONFIG" >/tmp/openclaw-jit-maf-gateway.log 2>&1 &
GATEWAY_PID=$!
trap 'kill "$GATEWAY_PID" >/dev/null 2>&1 || true; cleanup' EXIT

for _ in {1..30}; do
  if curl --silent --fail "http://127.0.0.1:19919/health" >/dev/null; then
    break
  fi
  sleep 1
done

curl --silent --fail "http://127.0.0.1:19919/health" >/dev/null

echo "Stopping published gateway..."
kill "$GATEWAY_PID" >/dev/null 2>&1 || true
wait "$GATEWAY_PID" || true

echo "JIT MAF smoke passed."

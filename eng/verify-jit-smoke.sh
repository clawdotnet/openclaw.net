#!/usr/bin/env bash
set -euo pipefail

ROOT_DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
ARTIFACTS_DIR="${JIT_ARTIFACTS_DIR:-$ROOT_DIR/artifacts/jit}"
WORK_DIR="$(mktemp -d "${TMPDIR:-/tmp}/openclaw-jit-smoke.XXXXXX")"
KEEP_WORK_DIR="${KEEP_JIT_SMOKE_DIR:-0}"

cleanup() {
  if [[ "$KEEP_WORK_DIR" != "1" ]]; then
    rm -rf "$WORK_DIR"
  else
    echo "Keeping work dir: $WORK_DIR"
  fi
}
trap cleanup EXIT

mkdir -p "$ARTIFACTS_DIR/gateway" "$ARTIFACTS_DIR/cli" "$WORK_DIR/memory"

GATEWAY_CONFIG="$WORK_DIR/gateway.smoke.json"
cat > "$GATEWAY_CONFIG" <<JSON
{
  "OpenClaw": {
    "BindAddress": "127.0.0.1",
    "Port": 19909,
    "Runtime": {
      "Mode": "jit"
    },
    "Llm": {
      "Provider": "ollama",
      "Model": "llama3.2"
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

echo "Publishing JIT gateway..."
dotnet publish "$ROOT_DIR/src/OpenClaw.Gateway/OpenClaw.Gateway.csproj" \
  -c Release \
  -p:PublishAot=false \
  -o "$ARTIFACTS_DIR/gateway"

echo "Publishing JIT CLI..."
dotnet publish "$ROOT_DIR/src/OpenClaw.Cli/OpenClaw.Cli.csproj" \
  -c Release \
  -p:PublishAot=false \
  -o "$ARTIFACTS_DIR/cli"

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
CLI_BIN="$(resolve_binary "$ARTIFACTS_DIR/cli" "openclaw")"

echo "Running published JIT gateway --doctor..."
MODEL_PROVIDER_KEY="smoke-test-key" "$GATEWAY_BIN" --config "$GATEWAY_CONFIG" --doctor >/tmp/openclaw-jit-doctor.log 2>&1

echo "Starting published JIT gateway..."
MODEL_PROVIDER_KEY="smoke-test-key" "$GATEWAY_BIN" --config "$GATEWAY_CONFIG" >/tmp/openclaw-jit-gateway.log 2>&1 &
GATEWAY_PID=$!
trap 'kill "$GATEWAY_PID" >/dev/null 2>&1 || true; cleanup' EXIT

for _ in {1..30}; do
  if curl --silent --fail "http://127.0.0.1:19909/health" >/dev/null; then
    break
  fi
  sleep 1
done

curl --silent --fail "http://127.0.0.1:19909/health" >/dev/null

echo "Running published CLI smoke..."
"$CLI_BIN" --help >/dev/null
"$CLI_BIN" version >/dev/null

echo "Stopping published gateway..."
kill "$GATEWAY_PID" >/dev/null 2>&1 || true
wait "$GATEWAY_PID" || true

echo "JIT smoke passed."

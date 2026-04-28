#!/usr/bin/env bash
set -euo pipefail

ROOT_DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
ARTIFACTS_DIR="${AOT_MAF_ARTIFACTS_DIR:-$ROOT_DIR/artifacts/aot-maf}"
RUNTIME_ID="${AOT_MAF_RUNTIME_IDENTIFIER:-$(dotnet --info | awk -F': ' '/ RID: / {print $2; exit}' | xargs)}"
WORK_DIR="$(mktemp -d "${TMPDIR:-/tmp}/openclaw-aot-maf-smoke.XXXXXX")"
KEEP_WORK_DIR="${KEEP_AOT_MAF_SMOKE_DIR:-0}"

cleanup() {
  if [[ "$KEEP_WORK_DIR" != "1" ]]; then
    rm -rf "$WORK_DIR"
  else
    echo "Keeping work dir: $WORK_DIR"
  fi
}
trap cleanup EXIT

mkdir -p "$ARTIFACTS_DIR/gateway" "$WORK_DIR/memory"

if [[ -z "$RUNTIME_ID" ]]; then
  echo "Unable to determine runtime identifier for NativeAOT publish." >&2
  exit 1
fi

GATEWAY_CONFIG="$WORK_DIR/gateway.maf.aot.smoke.json"
cat > "$GATEWAY_CONFIG" <<JSON
{
  "OpenClaw": {
    "BindAddress": "127.0.0.1",
    "Port": 19929,
    "Runtime": {
      "Mode": "aot",
      "Orchestrator": "maf"
    },
    "Llm": {
      "Provider": "ollama",
      "Model": "llama3.2"
    },
    "Memory": {
      "StoragePath": "$WORK_DIR/memory"
    },
    "Tooling": {
      "EnableBrowserTool": false
    }
  }
}
JSON

echo "Publishing NativeAOT gateway with the MAF experiment enabled for $RUNTIME_ID..."
dotnet publish "$ROOT_DIR/src/OpenClaw.Gateway/OpenClaw.Gateway.csproj" \
  -c Release \
  -r "$RUNTIME_ID" \
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

echo "Running NativeAOT gateway --doctor..."
if ! MODEL_PROVIDER_KEY="smoke-test-key" "$GATEWAY_BIN" --config "$GATEWAY_CONFIG" --doctor >/tmp/openclaw-aot-maf-doctor.log 2>&1; then
  cat /tmp/openclaw-aot-maf-doctor.log >&2
  exit 1
fi

echo "Starting NativeAOT gateway..."
MODEL_PROVIDER_KEY="smoke-test-key" "$GATEWAY_BIN" --config "$GATEWAY_CONFIG" >/tmp/openclaw-aot-maf-gateway.log 2>&1 &
GATEWAY_PID=$!
trap 'kill "$GATEWAY_PID" >/dev/null 2>&1 || true; cleanup' EXIT

for _ in {1..30}; do
  if curl --silent --fail "http://127.0.0.1:19929/health" >/dev/null; then
    break
  fi
  sleep 1
done

curl --silent --fail "http://127.0.0.1:19929/health" >/dev/null

echo "Stopping published gateway..."
kill "$GATEWAY_PID" >/dev/null 2>&1 || true
wait "$GATEWAY_PID" || true

echo "AOT MAF smoke passed."

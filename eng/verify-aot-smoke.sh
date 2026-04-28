#!/usr/bin/env bash
set -euo pipefail

ROOT_DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
ARTIFACTS_DIR="${AOT_ARTIFACTS_DIR:-$ROOT_DIR/artifacts/aot}"
RUNTIME_ID="${AOT_RUNTIME_IDENTIFIER:-$(dotnet --info | awk -F': ' '/ RID: / {print $2; exit}' | xargs)}"
WORK_DIR="$(mktemp -d "${TMPDIR:-/tmp}/openclaw-aot-smoke.XXXXXX")"
KEEP_WORK_DIR="${KEEP_AOT_SMOKE_DIR:-0}"

cleanup() {
  if [[ "$KEEP_WORK_DIR" != "1" ]]; then
    rm -rf "$WORK_DIR"
  else
    echo "Keeping work dir: $WORK_DIR"
  fi
}
trap cleanup EXIT

mkdir -p "$ARTIFACTS_DIR/gateway" "$ARTIFACTS_DIR/cli" "$WORK_DIR/memory"

if [[ -z "$RUNTIME_ID" ]]; then
  echo "Unable to determine runtime identifier for NativeAOT publish." >&2
  exit 1
fi

GATEWAY_CONFIG="$WORK_DIR/gateway.smoke.json"
cat > "$GATEWAY_CONFIG" <<JSON
{
  "OpenClaw": {
    "BindAddress": "127.0.0.1",
    "Port": 19899,
    "Runtime": {
      "Mode": "aot"
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

echo "Publishing NativeAOT gateway for $RUNTIME_ID..."
dotnet publish "$ROOT_DIR/src/OpenClaw.Gateway/OpenClaw.Gateway.csproj" \
  -c Release \
  -r "$RUNTIME_ID" \
  -o "$ARTIFACTS_DIR/gateway"

echo "Publishing NativeAOT CLI for $RUNTIME_ID..."
dotnet publish "$ROOT_DIR/src/OpenClaw.Cli/OpenClaw.Cli.csproj" \
  -c Release \
  -r "$RUNTIME_ID" \
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

echo "Running published gateway --doctor..."
if ! MODEL_PROVIDER_KEY="smoke-test-key" "$GATEWAY_BIN" --config "$GATEWAY_CONFIG" --doctor >/tmp/openclaw-aot-doctor.log 2>&1; then
  cat /tmp/openclaw-aot-doctor.log >&2
  exit 1
fi

echo "Starting published gateway..."
MODEL_PROVIDER_KEY="smoke-test-key" "$GATEWAY_BIN" --config "$GATEWAY_CONFIG" >/tmp/openclaw-aot-gateway.log 2>&1 &
GATEWAY_PID=$!
trap 'kill "$GATEWAY_PID" >/dev/null 2>&1 || true; cleanup' EXIT

for _ in {1..30}; do
  if curl --silent --fail "http://127.0.0.1:19899/health" >/dev/null; then
    break
  fi
  sleep 1
done

curl --silent --fail "http://127.0.0.1:19899/health" >/dev/null

echo "Running published CLI smoke..."
"$CLI_BIN" --help >/dev/null
"$CLI_BIN" version >/dev/null

echo "Stopping published gateway..."
kill "$GATEWAY_PID" >/dev/null 2>&1 || true
wait "$GATEWAY_PID" || true

echo "AOT smoke passed."

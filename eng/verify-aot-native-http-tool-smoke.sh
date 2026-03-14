#!/usr/bin/env bash
set -euo pipefail

ROOT_DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
ARTIFACTS_DIR="${AOT_NATIVE_HTTP_TOOL_ARTIFACTS_DIR:-$ROOT_DIR/artifacts/aot-native-http-tool}"
RUNTIME_ID="${AOT_RUNTIME_IDENTIFIER:-$(dotnet --info | awk -F': ' '/ RID: / {print $2; exit}' | xargs)}"
WORK_DIR="$(mktemp -d "${TMPDIR:-/tmp}/openclaw-aot-native-http-tool.XXXXXX")"
KEEP_WORK_DIR="${KEEP_AOT_NATIVE_HTTP_TOOL_DIR:-0}"
GATEWAY_PORT="${AOT_NATIVE_HTTP_TOOL_GATEWAY_PORT:-19969}"
PROVIDER_PORT="${AOT_NATIVE_HTTP_TOOL_PROVIDER_PORT:-21969}"
NOTE_KEY="aot-native-smoke"
NOTE_CONTENT="from published native aot"

cleanup() {
  if [[ -n "${GATEWAY_PID:-}" ]]; then
    kill "$GATEWAY_PID" >/dev/null 2>&1 || true
    wait "$GATEWAY_PID" || true
  fi
  if [[ -n "${PROVIDER_PID:-}" ]]; then
    kill "$PROVIDER_PID" >/dev/null 2>&1 || true
    wait "$PROVIDER_PID" || true
  fi
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

FAKE_PROVIDER_LOG="$WORK_DIR/fake-provider.jsonl"
FAKE_PROVIDER_STDOUT="$WORK_DIR/fake-provider.stdout.log"
GATEWAY_STDOUT="$WORK_DIR/gateway.stdout.log"
GATEWAY_RESPONSE="$WORK_DIR/gateway-response.json"
ADMIN_SUMMARY="$WORK_DIR/admin-summary.json"
ADMIN_EVENTS="$WORK_DIR/admin-events.json"
REPORT_JSON="$ARTIFACTS_DIR/report.json"

now_ms() {
  python3 -c 'import time; print(time.time_ns() // 1_000_000)'
}

echo "Starting fake OpenAI-compatible provider on port $PROVIDER_PORT..."
python3 "$ROOT_DIR/eng/fake_openai_tool_provider.py" \
  --port "$PROVIDER_PORT" \
  --log "$FAKE_PROVIDER_LOG" \
  --note-key "$NOTE_KEY" \
  --note-content "$NOTE_CONTENT" \
  >"$FAKE_PROVIDER_STDOUT" 2>&1 &
PROVIDER_PID=$!

for _ in {1..30}; do
  if curl --silent --fail "http://127.0.0.1:$PROVIDER_PORT/health" >/dev/null; then
    break
  fi
  sleep 1
done

curl --silent --fail "http://127.0.0.1:$PROVIDER_PORT/health" >/dev/null

GATEWAY_CONFIG="$WORK_DIR/gateway.native.aot.http-tool.json"
cat > "$GATEWAY_CONFIG" <<JSON
{
  "OpenClaw": {
    "BindAddress": "127.0.0.1",
    "Port": $GATEWAY_PORT,
    "Llm": {
      "Provider": "openai-compatible",
      "Model": "fake-maf-model",
      "ApiKey": "test-key",
      "Endpoint": "http://127.0.0.1:$PROVIDER_PORT/v1"
    },
    "Runtime": {
      "Mode": "aot",
      "Orchestrator": "native"
    },
    "Memory": {
      "StoragePath": "$WORK_DIR/memory",
      "MaxHistoryTurns": 12
    },
    "Tooling": {
      "EnableBrowserTool": false,
      "ToolTimeoutSeconds": 10,
      "RequireToolApproval": false
    }
  }
}
JSON

echo "Publishing NativeAOT native gateway with HTTP tool smoke support for $RUNTIME_ID..."
dotnet publish "$ROOT_DIR/src/OpenClaw.Gateway/OpenClaw.Gateway.csproj" \
  -c Release \
  -r "$RUNTIME_ID" \
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

echo "Running NativeAOT native gateway --doctor..."
"$GATEWAY_BIN" --config "$GATEWAY_CONFIG" --doctor >/tmp/openclaw-aot-native-http-tool-doctor.log 2>&1

echo "Starting NativeAOT native gateway..."
GATEWAY_START_MS="$(now_ms)"
"$GATEWAY_BIN" --config "$GATEWAY_CONFIG" >"$GATEWAY_STDOUT" 2>&1 &
GATEWAY_PID=$!

for _ in {1..30}; do
  if curl --silent --fail "http://127.0.0.1:$GATEWAY_PORT/health" >/dev/null; then
    break
  fi
  sleep 1
done

curl --silent --fail "http://127.0.0.1:$GATEWAY_PORT/health" >/dev/null
GATEWAY_READY_MS="$(now_ms)"
GATEWAY_STARTUP_MS="$((GATEWAY_READY_MS - GATEWAY_START_MS))"

echo "Issuing OpenAI-compatible request through the published AOT native gateway..."
REQUEST_START_MS="$(now_ms)"
curl --silent --fail \
  -H "Content-Type: application/json" \
  -d '{"model":"fake-maf-model","messages":[{"role":"user","content":"Use the memory tool to save a note."}]}' \
  "http://127.0.0.1:$GATEWAY_PORT/v1/chat/completions" \
  >"$GATEWAY_RESPONSE"
REQUEST_END_MS="$(now_ms)"
GATEWAY_REQUEST_MS="$((REQUEST_END_MS - REQUEST_START_MS))"

echo "Fetching admin summary and runtime events..."
curl --silent --fail "http://127.0.0.1:$GATEWAY_PORT/admin/summary" >"$ADMIN_SUMMARY"
curl --silent --fail "http://127.0.0.1:$GATEWAY_PORT/admin/events?component=llm&channelId=openai-http&limit=20" >"$ADMIN_EVENTS"

python3 "$ROOT_DIR/eng/verify_maf_http_tool_result.py" \
  --response "$GATEWAY_RESPONSE" \
  --provider-log "$FAKE_PROVIDER_LOG" \
  --notes-dir "$WORK_DIR/memory/notes" \
  --summary "$ADMIN_SUMMARY" \
  --events "$ADMIN_EVENTS" \
  --expected-mode aot \
  --expected-orchestrator native \
  --expected-provider openai-compatible \
  --expected-model fake-maf-model \
  --expected-note-key "$NOTE_KEY" \
  --expected-note-content "$NOTE_CONTENT" \
  --gateway-startup-ms "$GATEWAY_STARTUP_MS" \
  --gateway-request-ms "$GATEWAY_REQUEST_MS" \
  --report "$REPORT_JSON"

echo "Stopping published gateway..."
kill "$GATEWAY_PID" >/dev/null 2>&1 || true
wait "$GATEWAY_PID" || true
unset GATEWAY_PID

echo "Stopping fake provider..."
kill "$PROVIDER_PID" >/dev/null 2>&1 || true
wait "$PROVIDER_PID" || true
unset PROVIDER_PID

echo "AOT native HTTP tool smoke passed."

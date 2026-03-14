#!/usr/bin/env bash
set -euo pipefail

ROOT_DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
ARTIFACTS_DIR="${APPROVAL_SMOKE_ARTIFACTS_DIR:?APPROVAL_SMOKE_ARTIFACTS_DIR is required}"
RUNTIME_MODE="${APPROVAL_SMOKE_RUNTIME_MODE:?APPROVAL_SMOKE_RUNTIME_MODE is required}"
ORCHESTRATOR="${APPROVAL_SMOKE_ORCHESTRATOR:?APPROVAL_SMOKE_ORCHESTRATOR is required}"
GATEWAY_PORT="${APPROVAL_SMOKE_GATEWAY_PORT:?APPROVAL_SMOKE_GATEWAY_PORT is required}"
PROVIDER_PORT="${APPROVAL_SMOKE_PROVIDER_PORT:?APPROVAL_SMOKE_PROVIDER_PORT is required}"
NOTE_KEY="${APPROVAL_SMOKE_NOTE_KEY:?APPROVAL_SMOKE_NOTE_KEY is required}"
NOTE_CONTENT="${APPROVAL_SMOKE_NOTE_CONTENT:?APPROVAL_SMOKE_NOTE_CONTENT is required}"
PUBLISH_AOT="${APPROVAL_SMOKE_PUBLISH_AOT:-0}"
ENABLE_MAF="${APPROVAL_SMOKE_ENABLE_MAF:-0}"
KEEP_WORK_DIR="${KEEP_WS_APPROVAL_SMOKE_DIR:-0}"
WORK_DIR="$(mktemp -d "${TMPDIR:-/tmp}/openclaw-ws-approval-${RUNTIME_MODE}-${ORCHESTRATOR}.XXXXXX")"
RUNTIME_ID="${AOT_RUNTIME_IDENTIFIER:-$(dotnet --info | awk -F': ' '/ RID: / {print $2; exit}' | xargs)}"

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

FAKE_PROVIDER_LOG="$WORK_DIR/fake-provider.jsonl"
FAKE_PROVIDER_STDOUT="$WORK_DIR/fake-provider.stdout.log"
GATEWAY_STDOUT="$WORK_DIR/gateway.stdout.log"
WS_TRANSCRIPT="$WORK_DIR/ws-transcript.json"
ADMIN_SUMMARY="$WORK_DIR/admin-summary.json"
APPROVAL_HISTORY="$WORK_DIR/approval-history.json"
APPROVAL_EVENTS="$WORK_DIR/approval-events.json"
LLM_EVENTS="$WORK_DIR/llm-events.json"
PENDING_APPROVALS="$WORK_DIR/pending-approvals.json"
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

GATEWAY_CONFIG="$WORK_DIR/gateway.websocket.approval.json"
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
      "Mode": "$RUNTIME_MODE",
      "Orchestrator": "$ORCHESTRATOR"
    },
    "Memory": {
      "StoragePath": "$WORK_DIR/memory",
      "MaxHistoryTurns": 12
    },
    "Tooling": {
      "EnableBrowserTool": false,
      "ToolTimeoutSeconds": 10,
      "RequireToolApproval": true,
      "ApprovalRequiredTools": ["memory"],
      "ToolApprovalTimeoutSeconds": 30
    }
  }
}
JSON

if [[ "$PUBLISH_AOT" == "1" ]]; then
  if [[ -z "$RUNTIME_ID" ]]; then
    echo "Unable to determine runtime identifier for NativeAOT publish." >&2
    exit 1
  fi

  echo "Publishing NativeAOT gateway for websocket approval smoke ($RUNTIME_MODE/$ORCHESTRATOR) on $RUNTIME_ID..."
  publish_args=(
    -c Release
    -r "$RUNTIME_ID"
  )
else
  echo "Publishing JIT gateway for websocket approval smoke ($RUNTIME_MODE/$ORCHESTRATOR)..."
  publish_args=(
    -c Release
    -p:PublishAot=false
  )
fi

if [[ "$ENABLE_MAF" == "1" ]]; then
  publish_args+=(-p:OpenClawEnableMafExperiment=true)
fi

dotnet publish "$ROOT_DIR/src/OpenClaw.Gateway/OpenClaw.Gateway.csproj" \
  "${publish_args[@]}" \
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

echo "Running gateway --doctor..."
"$GATEWAY_BIN" --config "$GATEWAY_CONFIG" --doctor >"$WORK_DIR/doctor.log" 2>&1

echo "Starting gateway..."
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

echo "Running websocket approval flow..."
FLOW_START_MS="$(now_ms)"
node "$ROOT_DIR/eng/ws_approval_client.mjs" \
  --url "ws://127.0.0.1:$GATEWAY_PORT/ws" \
  --prompt "Use the memory tool to save a note." \
  --output "$WS_TRANSCRIPT"
FLOW_END_MS="$(now_ms)"
APPROVAL_FLOW_MS="$((FLOW_END_MS - FLOW_START_MS))"

echo "Fetching admin summary, approval, and event data..."
curl --silent --fail "http://127.0.0.1:$GATEWAY_PORT/admin/summary" >"$ADMIN_SUMMARY"
curl --silent --fail "http://127.0.0.1:$GATEWAY_PORT/tools/approvals/history?toolName=memory&limit=20" >"$APPROVAL_HISTORY"
curl --silent --fail "http://127.0.0.1:$GATEWAY_PORT/tools/approvals" >"$PENDING_APPROVALS"
curl --silent --fail "http://127.0.0.1:$GATEWAY_PORT/admin/events?component=approval&channelId=websocket&limit=20" >"$APPROVAL_EVENTS"
curl --silent --fail "http://127.0.0.1:$GATEWAY_PORT/admin/events?component=llm&channelId=websocket&limit=20" >"$LLM_EVENTS"

python3 "$ROOT_DIR/eng/verify_ws_approval_result.py" \
  --transcript "$WS_TRANSCRIPT" \
  --provider-log "$FAKE_PROVIDER_LOG" \
  --notes-dir "$WORK_DIR/memory/notes" \
  --summary "$ADMIN_SUMMARY" \
  --approval-history "$APPROVAL_HISTORY" \
  --approval-events "$APPROVAL_EVENTS" \
  --llm-events "$LLM_EVENTS" \
  --pending-approvals "$PENDING_APPROVALS" \
  --expected-mode "$RUNTIME_MODE" \
  --expected-orchestrator "$ORCHESTRATOR" \
  --expected-provider openai-compatible \
  --expected-model fake-maf-model \
  --expected-note-key "$NOTE_KEY" \
  --expected-note-content "$NOTE_CONTENT" \
  --gateway-startup-ms "$GATEWAY_STARTUP_MS" \
  --approval-flow-ms "$APPROVAL_FLOW_MS" \
  --report "$REPORT_JSON"

echo "Stopping gateway..."
kill "$GATEWAY_PID" >/dev/null 2>&1 || true
wait "$GATEWAY_PID" || true
unset GATEWAY_PID

echo "Stopping fake provider..."
kill "$PROVIDER_PID" >/dev/null 2>&1 || true
wait "$PROVIDER_PID" || true
unset PROVIDER_PID

echo "Websocket approval smoke passed for $RUNTIME_MODE/$ORCHESTRATOR."

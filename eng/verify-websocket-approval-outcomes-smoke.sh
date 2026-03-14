#!/usr/bin/env bash
set -euo pipefail

ROOT_DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
ARTIFACTS_DIR="${APPROVAL_OUTCOMES_ARTIFACTS_DIR:?APPROVAL_OUTCOMES_ARTIFACTS_DIR is required}"
RUNTIME_MODE="${APPROVAL_OUTCOMES_RUNTIME_MODE:?APPROVAL_OUTCOMES_RUNTIME_MODE is required}"
ORCHESTRATOR="${APPROVAL_OUTCOMES_ORCHESTRATOR:?APPROVAL_OUTCOMES_ORCHESTRATOR is required}"
GATEWAY_PORT="${APPROVAL_OUTCOMES_GATEWAY_PORT:?APPROVAL_OUTCOMES_GATEWAY_PORT is required}"
PROVIDER_PORT="${APPROVAL_OUTCOMES_PROVIDER_PORT:?APPROVAL_OUTCOMES_PROVIDER_PORT is required}"
NOTE_KEY="${APPROVAL_OUTCOMES_NOTE_KEY:-approval-outcome-note}"
NOTE_CONTENT="${APPROVAL_OUTCOMES_NOTE_CONTENT:-approval outcome content}"
APPROVAL_TIMEOUT_SECONDS="${APPROVAL_OUTCOMES_TIMEOUT_SECONDS:-5}"
PUBLISH_AOT="${APPROVAL_OUTCOMES_PUBLISH_AOT:-0}"
ENABLE_MAF="${APPROVAL_OUTCOMES_ENABLE_MAF:-0}"
KEEP_WORK_DIR="${KEEP_WS_APPROVAL_OUTCOMES_DIR:-0}"
WORK_DIR="$(mktemp -d "${TMPDIR:-/tmp}/openclaw-ws-approval-outcomes-${RUNTIME_MODE}-${ORCHESTRATOR}.XXXXXX")"
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
ADMIN_SUMMARY="$WORK_DIR/admin-summary.json"
APPROVAL_HISTORY="$WORK_DIR/approval-history.json"
APPROVAL_EVENTS="$WORK_DIR/approval-events.json"
PENDING_APPROVALS="$WORK_DIR/pending-approvals.json"
REPORT_JSON="$ARTIFACTS_DIR/report.json"

WS_DENY_JSON="$WORK_DIR/ws-deny.json"
HTTP_ADMIN_DENY_JSON="$WORK_DIR/http-admin-deny.json"
HTTP_REQUESTER_MISMATCH_JSON="$WORK_DIR/http-requester-mismatch.json"
WS_TIMEOUT_JSON="$WORK_DIR/ws-timeout.json"
WS_MISMATCH_ACK_JSON="$WORK_DIR/ws-mismatch-ack.json"
ADMIN_DENY_RESPONSE_JSON="$WORK_DIR/http-admin-deny-response.json"
MISMATCH_FINALIZE_RESPONSE_JSON="$WORK_DIR/http-mismatch-finalize-response.json"

now_ms() {
  python3 -c 'import time; print(time.time_ns() // 1_000_000)'
}

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

write_http_response_json() {
  local url="$1"
  local output="$2"
  local status_file="${output}.status"
  local body_file="${output}.body"
  local status_code
  status_code="$(curl --silent --show-error --output "$body_file" --write-out '%{http_code}' --request POST "$url")"
  printf '%s' "$status_code" >"$status_file"
  python3 - <<'PY' "$status_file" "$body_file" "$output"
import json
import pathlib
import sys

status = pathlib.Path(sys.argv[1]).read_text(encoding="utf-8")
body = pathlib.Path(sys.argv[2]).read_text(encoding="utf-8")
payload = {
    "statusCode": int(status),
    "body": body,
}
pathlib.Path(sys.argv[3]).write_text(json.dumps(payload, indent=2) + "\n", encoding="utf-8")
PY
  rm -f "$status_file" "$body_file"
}

wait_for_pending_approval_id() {
  local output_file="$1"
  for _ in {1..60}; do
    if curl --silent --fail "http://127.0.0.1:$GATEWAY_PORT/tools/approvals" >"$output_file"; then
      local approval_id
      approval_id="$(python3 - <<'PY' "$output_file"
import json
import pathlib
import sys

payload = json.loads(pathlib.Path(sys.argv[1]).read_text(encoding="utf-8"))
items = payload.get("items") or []
print(items[0].get("approvalId") or "" if items else "")
PY
)"
      if [[ -n "$approval_id" ]]; then
        printf '%s\n' "$approval_id"
        return 0
      fi
    fi
    sleep 0.25
  done

  echo "Timed out waiting for pending approval id." >&2
  return 1
}

wait_for_no_pending_approvals() {
  for _ in {1..60}; do
    if curl --silent --fail "http://127.0.0.1:$GATEWAY_PORT/tools/approvals" >"$PENDING_APPROVALS"; then
      if python3 - <<'PY' "$PENDING_APPROVALS"
import json
import pathlib
import sys

payload = json.loads(pathlib.Path(sys.argv[1]).read_text(encoding="utf-8"))
items = payload.get("items") or []
raise SystemExit(0 if not items else 1)
PY
      then
        return 0
      fi
    fi
    sleep 0.25
  done

  echo "Timed out waiting for pending approvals to drain." >&2
  return 1
}

start_waiting_client() {
  local output="$1"
  node "$ROOT_DIR/eng/ws_approval_client.mjs" \
    --url "ws://127.0.0.1:$GATEWAY_PORT/ws" \
    --prompt "Use the memory tool to save a note." \
    --decision wait \
    --timeout-ms 20000 \
    --output "$output" &
  LAST_CLIENT_PID="$!"
}

wait_for_client() {
  local pid="$1"
  wait "$pid"
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

GATEWAY_CONFIG="$WORK_DIR/gateway.websocket.approval-outcomes.json"
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
      "ToolApprovalTimeoutSeconds": $APPROVAL_TIMEOUT_SECONDS
    }
  }
}
JSON

if [[ "$PUBLISH_AOT" == "1" ]]; then
  publish_args=(-c Release -r "$RUNTIME_ID")
else
  publish_args=(-c Release -p:PublishAot=false)
fi

if [[ "$ENABLE_MAF" == "1" ]]; then
  publish_args+=(-p:OpenClawEnableMafExperiment=true)
fi

dotnet publish "$ROOT_DIR/src/OpenClaw.Gateway/OpenClaw.Gateway.csproj" \
  "${publish_args[@]}" \
  -o "$ARTIFACTS_DIR/gateway"

GATEWAY_BIN="$(resolve_binary "$ARTIFACTS_DIR/gateway" "OpenClaw.Gateway")"
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

echo "Running websocket deny scenario..."
START_MS="$(now_ms)"
node "$ROOT_DIR/eng/ws_approval_client.mjs" \
  --url "ws://127.0.0.1:$GATEWAY_PORT/ws" \
  --prompt "Use the memory tool to save a note." \
  --decision deny \
  --timeout-ms 20000 \
  --output "$WS_DENY_JSON"
END_MS="$(now_ms)"
WS_DENY_MS="$((END_MS - START_MS))"
wait_for_no_pending_approvals

echo "Running HTTP admin deny scenario..."
START_MS="$(now_ms)"
start_waiting_client "$HTTP_ADMIN_DENY_JSON"
HTTP_ADMIN_PID="$LAST_CLIENT_PID"
HTTP_ADMIN_APPROVAL_ID="$(wait_for_pending_approval_id "$WORK_DIR/http-admin-pending.json")"
write_http_response_json \
  "http://127.0.0.1:$GATEWAY_PORT/tools/approve?approvalId=${HTTP_ADMIN_APPROVAL_ID}&approved=false" \
  "$ADMIN_DENY_RESPONSE_JSON"
wait_for_client "$HTTP_ADMIN_PID"
END_MS="$(now_ms)"
HTTP_ADMIN_DENY_MS="$((END_MS - START_MS))"
wait_for_no_pending_approvals

echo "Running HTTP requester mismatch scenario..."
START_MS="$(now_ms)"
start_waiting_client "$HTTP_REQUESTER_MISMATCH_JSON"
HTTP_MISMATCH_PID="$LAST_CLIENT_PID"
HTTP_MISMATCH_APPROVAL_ID="$(wait_for_pending_approval_id "$WORK_DIR/http-mismatch-pending.json")"
node "$ROOT_DIR/eng/ws_raw_message_client.mjs" \
  --url "ws://127.0.0.1:$GATEWAY_PORT/ws" \
  --message "/approve ${HTTP_MISMATCH_APPROVAL_ID} yes" \
  --output "$WS_MISMATCH_ACK_JSON"
write_http_response_json \
  "http://127.0.0.1:$GATEWAY_PORT/tools/approve?approvalId=${HTTP_MISMATCH_APPROVAL_ID}&approved=false" \
  "$MISMATCH_FINALIZE_RESPONSE_JSON"
wait_for_client "$HTTP_MISMATCH_PID"
END_MS="$(now_ms)"
HTTP_REQUESTER_MISMATCH_MS="$((END_MS - START_MS))"
wait_for_no_pending_approvals

echo "Running websocket timeout scenario..."
START_MS="$(now_ms)"
node "$ROOT_DIR/eng/ws_approval_client.mjs" \
  --url "ws://127.0.0.1:$GATEWAY_PORT/ws" \
  --prompt "Use the memory tool to save a note." \
  --decision wait \
  --timeout-ms 20000 \
  --output "$WS_TIMEOUT_JSON"
END_MS="$(now_ms)"
WS_TIMEOUT_MS="$((END_MS - START_MS))"
wait_for_no_pending_approvals

curl --silent --fail "http://127.0.0.1:$GATEWAY_PORT/admin/summary" >"$ADMIN_SUMMARY"
curl --silent --fail "http://127.0.0.1:$GATEWAY_PORT/tools/approvals/history?toolName=memory&limit=100" >"$APPROVAL_HISTORY"
curl --silent --fail "http://127.0.0.1:$GATEWAY_PORT/admin/events?component=approval&channelId=websocket&limit=100" >"$APPROVAL_EVENTS"
curl --silent --fail "http://127.0.0.1:$GATEWAY_PORT/tools/approvals" >"$PENDING_APPROVALS"

python3 "$ROOT_DIR/eng/verify_approval_outcome_result.py" \
  --ws-deny "$WS_DENY_JSON" \
  --http-admin-deny "$HTTP_ADMIN_DENY_JSON" \
  --http-requester-mismatch "$HTTP_REQUESTER_MISMATCH_JSON" \
  --ws-timeout "$WS_TIMEOUT_JSON" \
  --ws-mismatch-ack "$WS_MISMATCH_ACK_JSON" \
  --admin-deny-response "$ADMIN_DENY_RESPONSE_JSON" \
  --mismatch-finalize-response "$MISMATCH_FINALIZE_RESPONSE_JSON" \
  --summary "$ADMIN_SUMMARY" \
  --approval-history "$APPROVAL_HISTORY" \
  --approval-events "$APPROVAL_EVENTS" \
  --pending-approvals "$PENDING_APPROVALS" \
  --provider-log "$FAKE_PROVIDER_LOG" \
  --notes-dir "$WORK_DIR/memory/notes" \
  --expected-mode "$RUNTIME_MODE" \
  --expected-orchestrator "$ORCHESTRATOR" \
  --expected-provider openai-compatible \
  --expected-model fake-maf-model \
  --gateway-startup-ms "$GATEWAY_STARTUP_MS" \
  --ws-deny-ms "$WS_DENY_MS" \
  --http-admin-deny-ms "$HTTP_ADMIN_DENY_MS" \
  --http-requester-mismatch-ms "$HTTP_REQUESTER_MISMATCH_MS" \
  --ws-timeout-ms "$WS_TIMEOUT_MS" \
  --report "$REPORT_JSON"

echo "Stopping gateway..."
kill "$GATEWAY_PID" >/dev/null 2>&1 || true
wait "$GATEWAY_PID" || true
unset GATEWAY_PID

echo "Stopping fake provider..."
kill "$PROVIDER_PID" >/dev/null 2>&1 || true
wait "$PROVIDER_PID" || true
unset PROVIDER_PID

echo "Websocket approval outcome smoke passed for $RUNTIME_MODE/$ORCHESTRATOR."

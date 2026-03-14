#!/usr/bin/env bash
set -euo pipefail

ROOT_DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
ARTIFACTS_DIR="${RESTART_SMOKE_ARTIFACTS_DIR:?RESTART_SMOKE_ARTIFACTS_DIR is required}"
RUNTIME_MODE="${RESTART_SMOKE_RUNTIME_MODE:?RESTART_SMOKE_RUNTIME_MODE is required}"
ORCHESTRATOR="${RESTART_SMOKE_ORCHESTRATOR:?RESTART_SMOKE_ORCHESTRATOR is required}"
GATEWAY_PORT="${RESTART_SMOKE_GATEWAY_PORT:?RESTART_SMOKE_GATEWAY_PORT is required}"
PROVIDER_PORT="${RESTART_SMOKE_PROVIDER_PORT:?RESTART_SMOKE_PROVIDER_PORT is required}"
PUBLISH_AOT="${RESTART_SMOKE_PUBLISH_AOT:-0}"
ENABLE_MAF="${RESTART_SMOKE_ENABLE_MAF:-0}"
KEEP_WORK_DIR="${KEEP_RESTART_SMOKE_DIR:-0}"
SESSION_ID="${RESTART_SMOKE_SESSION_ID:-restart-smoke-session}"
NOTE_KEY="${RESTART_SMOKE_NOTE_KEY:-restart-smoke-note}"
NOTE_CONTENT="${RESTART_SMOKE_NOTE_CONTENT:-from restart smoke}"
WORK_DIR="$(mktemp -d "${TMPDIR:-/tmp}/openclaw-restart-${RUNTIME_MODE}-${ORCHESTRATOR}.XXXXXX")"
RUNTIME_ID="${AOT_RUNTIME_IDENTIFIER:-$(dotnet --info | awk -F': ' '/ RID: / {print $2; exit}' | xargs)}"
SESSION_FILE="$(python3 - <<'PY' "$WORK_DIR/memory" "$SESSION_ID"
import base64
import pathlib
import sys

root = pathlib.Path(sys.argv[1]) / "sessions"
session_id = sys.argv[2].encode("utf-8")
encoded = base64.b64encode(session_id).decode("ascii").replace("+", "-").replace("/", "_").rstrip("=")
print(root / f"{encoded}.json")
PY
)"

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
TURN1_JSON="$WORK_DIR/turn1.json"
TURN2_JSON="$WORK_DIR/turn2.json"
TURN3_JSON="$WORK_DIR/turn3.json"
GATEWAY_LOG1="$WORK_DIR/gateway-run1.log"
GATEWAY_LOG2="$WORK_DIR/gateway-run2.log"
GATEWAY_LOG3="$WORK_DIR/gateway-run3.log"
ADMIN_SUMMARY="$WORK_DIR/admin-summary.json"
REPORT_JSON="$ARTIFACTS_DIR/report.json"
SIDECAR_PATH_FILE="$WORK_DIR/sidecar-path.txt"

STREAM_TOOL_ARGUMENTS_JSON="$(python3 - <<'PY' "$NOTE_KEY" "$NOTE_CONTENT"
import json
import sys

print(json.dumps({
    "action": "write",
    "key": sys.argv[1],
    "content": sys.argv[2],
}))
PY
)"

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

start_gateway() {
  local log_file="$1"
  "$GATEWAY_BIN" --config "$GATEWAY_CONFIG" >"$log_file" 2>&1 &
  GATEWAY_PID=$!
  for _ in {1..30}; do
    if curl --silent --fail "http://127.0.0.1:$GATEWAY_PORT/health" >/dev/null; then
      return 0
    fi
    sleep 1
  done
  curl --silent --fail "http://127.0.0.1:$GATEWAY_PORT/health" >/dev/null
}

stop_gateway() {
  if [[ -n "${GATEWAY_PID:-}" ]]; then
    kill "$GATEWAY_PID" >/dev/null 2>&1 || true
    wait "$GATEWAY_PID" || true
    unset GATEWAY_PID
  fi
}

run_turn() {
  local prompt="$1"
  local output="$2"
  local start_ms end_ms
  start_ms="$(now_ms)"
  node "$ROOT_DIR/eng/ws_turn_client.mjs" \
    --url "ws://127.0.0.1:$GATEWAY_PORT/ws" \
    --session-id "$SESSION_ID" \
    --prompt "$prompt" \
    --output "$output"
  end_ms="$(now_ms)"
  echo "$((end_ms - start_ms))"
}

wait_for_session_history() {
  local expected_count="$1"
  local expected_fragment="$2"

  for _ in {1..30}; do
    if [[ -f "$SESSION_FILE" ]]; then
      if python3 - <<'PY' "$SESSION_FILE" "$expected_count" "$expected_fragment"
import json
import pathlib
import sys

session = json.loads(pathlib.Path(sys.argv[1]).read_text(encoding="utf-8"))
history = session.get("history") or []
expected_count = int(sys.argv[2])
expected_fragment = sys.argv[3]
parts = []
for item in history:
    if not isinstance(item, dict):
        continue
    parts.append(str(item.get("content") or ""))
    for call in item.get("toolCalls") or []:
        if isinstance(call, dict):
            parts.append(str(call.get("result") or ""))
history_text = "\n".join(parts)
if len(history) >= expected_count and expected_fragment in history_text:
    raise SystemExit(0)
raise SystemExit(1)
PY
      then
        return 0
      fi
    fi
    sleep 1
  done

  echo "Timed out waiting for persisted session history for $SESSION_ID" >&2
  return 1
}

echo "Starting fake OpenAI-compatible provider on port $PROVIDER_PORT..."
python3 "$ROOT_DIR/eng/fake_openai_tool_provider.py" \
  --port "$PROVIDER_PORT" \
  --log "$FAKE_PROVIDER_LOG" \
  --tool-name memory \
  --note-key "$NOTE_KEY" \
  --note-content "$NOTE_CONTENT" \
  --stream-tool-name memory \
  --stream-tool-arguments-json "$STREAM_TOOL_ARGUMENTS_JSON" \
  --followup-trigger "what note did i save earlier?" \
  --followup-trigger "repeat the saved note key." \
  --followup-required-fragment "Saved note: $NOTE_KEY" \
  --followup-response-template "Earlier note was {note_key}." \
  >"$FAKE_PROVIDER_STDOUT" 2>&1 &
PROVIDER_PID=$!

for _ in {1..30}; do
  if curl --silent --fail "http://127.0.0.1:$PROVIDER_PORT/health" >/dev/null; then
    break
  fi
  sleep 1
done
curl --silent --fail "http://127.0.0.1:$PROVIDER_PORT/health" >/dev/null

GATEWAY_CONFIG="$WORK_DIR/gateway.restart.json"
python3 - <<'PY' "$GATEWAY_CONFIG" "$GATEWAY_PORT" "$PROVIDER_PORT" "$RUNTIME_MODE" "$ORCHESTRATOR" "$WORK_DIR"
import json
import pathlib
import sys

config_path = pathlib.Path(sys.argv[1])
gateway_port = int(sys.argv[2])
provider_port = int(sys.argv[3])
runtime_mode = sys.argv[4]
orchestrator = sys.argv[5]
work_dir = pathlib.Path(sys.argv[6])

config = {
    "OpenClaw": {
        "BindAddress": "127.0.0.1",
        "Port": gateway_port,
        "Llm": {
            "Provider": "openai-compatible",
            "Model": "fake-maf-model",
            "ApiKey": "test-key",
            "Endpoint": f"http://127.0.0.1:{provider_port}/v1",
        },
        "Runtime": {
            "Mode": runtime_mode,
            "Orchestrator": orchestrator,
        },
        "Memory": {
            "StoragePath": str(work_dir / "memory"),
            "MaxHistoryTurns": 12,
        },
        "Tooling": {
            "EnableBrowserTool": False,
            "ToolTimeoutSeconds": 10,
            "RequireToolApproval": False,
        },
    },
}

config_path.write_text(json.dumps(config, indent=2) + "\n", encoding="utf-8")
PY

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

GATEWAY_START_MS="$(now_ms)"
start_gateway "$GATEWAY_LOG1"
GATEWAY_READY_MS="$(now_ms)"
GATEWAY_STARTUP_MS="$((GATEWAY_READY_MS - GATEWAY_START_MS))"
TURN1_MS="$(run_turn "Use the memory tool to save a note." "$TURN1_JSON")"
wait_for_session_history 3 "Saved note: $NOTE_KEY"
sleep 1
stop_gateway

SIDECAR_PATH=""
if [[ "$ORCHESTRATOR" == "maf" ]]; then
  SIDECAR_PATH="$(python3 - <<'PY' "$WORK_DIR/memory" "$SESSION_ID"
import hashlib
import pathlib
import sys

root = pathlib.Path(sys.argv[1]) / "experiments" / "maf" / "sessions"
session_id = sys.argv[2].encode("utf-8")
print(root / f"{hashlib.sha256(session_id).hexdigest().upper()}.json")
PY
)"
  printf '%s\n' "$SIDECAR_PATH" >"$SIDECAR_PATH_FILE"
fi

start_gateway "$GATEWAY_LOG2"
TURN2_MS="$(run_turn "What note did I save earlier?" "$TURN2_JSON")"
wait_for_session_history 5 "What note did I save earlier?"
sleep 1
stop_gateway

if [[ "$ORCHESTRATOR" == "maf" ]]; then
  printf '{corrupt json' >"$SIDECAR_PATH"
fi

start_gateway "$GATEWAY_LOG3"
TURN3_MS="$(run_turn "Repeat the saved note key." "$TURN3_JSON")"
wait_for_session_history 7 "Repeat the saved note key."
sleep 1
curl --silent --fail "http://127.0.0.1:$GATEWAY_PORT/admin/summary" >"$ADMIN_SUMMARY"
verify_args=(
  --turn1 "$TURN1_JSON"
  --turn2 "$TURN2_JSON"
  --turn3 "$TURN3_JSON"
  --summary "$ADMIN_SUMMARY"
  --session-file "$SESSION_FILE"
  --provider-log "$FAKE_PROVIDER_LOG"
  --expected-mode "$RUNTIME_MODE"
  --expected-orchestrator "$ORCHESTRATOR"
  --expected-provider openai-compatible
  --expected-model fake-maf-model
  --expected-note-key "$NOTE_KEY"
  --expected-note-content "$NOTE_CONTENT"
  --expected-session-id "$SESSION_ID"
  --log-second "$GATEWAY_LOG2"
  --log-third "$GATEWAY_LOG3"
  --gateway-startup-ms "$GATEWAY_STARTUP_MS"
  --turn1-ms "$TURN1_MS"
  --turn2-ms "$TURN2_MS"
  --turn3-ms "$TURN3_MS"
  --report "$REPORT_JSON"
)

if [[ -n "$SIDECAR_PATH" ]]; then
  verify_args+=(--sidecar-path "$SIDECAR_PATH")
fi

python3 "$ROOT_DIR/eng/verify_restart_resume_result.py" "${verify_args[@]}"
stop_gateway

kill "$PROVIDER_PID" >/dev/null 2>&1 || true
wait "$PROVIDER_PID" || true
unset PROVIDER_PID

echo "Websocket restart/resume smoke passed for $RUNTIME_MODE/$ORCHESTRATOR."

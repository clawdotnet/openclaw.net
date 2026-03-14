#!/usr/bin/env bash
set -euo pipefail

ROOT_DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
ARTIFACTS_DIR="${HTTP_RESPONSES_FAILED_SMOKE_ARTIFACTS_DIR:?HTTP_RESPONSES_FAILED_SMOKE_ARTIFACTS_DIR is required}"
RUNTIME_MODE="${HTTP_RESPONSES_FAILED_SMOKE_RUNTIME_MODE:?HTTP_RESPONSES_FAILED_SMOKE_RUNTIME_MODE is required}"
ORCHESTRATOR="${HTTP_RESPONSES_FAILED_SMOKE_ORCHESTRATOR:?HTTP_RESPONSES_FAILED_SMOKE_ORCHESTRATOR is required}"
GATEWAY_PORT="${HTTP_RESPONSES_FAILED_SMOKE_GATEWAY_PORT:?HTTP_RESPONSES_FAILED_SMOKE_GATEWAY_PORT is required}"
PROVIDER_PORT="${HTTP_RESPONSES_FAILED_SMOKE_PROVIDER_PORT:?HTTP_RESPONSES_FAILED_SMOKE_PROVIDER_PORT is required}"
PUBLISH_AOT="${HTTP_RESPONSES_FAILED_SMOKE_PUBLISH_AOT:-0}"
ENABLE_MAF="${HTTP_RESPONSES_FAILED_SMOKE_ENABLE_MAF:-0}"
KEEP_WORK_DIR="${KEEP_HTTP_RESPONSES_FAILED_SMOKE_DIR:-0}"
WORK_DIR="$(mktemp -d "${TMPDIR:-/tmp}/openclaw-http-responses-failed-${RUNTIME_MODE}-${ORCHESTRATOR}.XXXXXX")"
RUNTIME_ID="${AOT_RUNTIME_IDENTIFIER:-$(dotnet --info | awk -F': ' '/ RID: / {print $2; exit}' | xargs)}"
TOOL_NAME="stream_echo"
TOOL_ARGUMENTS_JSON='{"chunks":["a","b","c"]}'
EXPECTED_TOOL_RESULT="abc"
EXPECTED_ERROR_CODE="provider_error"
EXPECTED_ERROR_MESSAGE_SUBSTRING="trouble reaching my AI provider"

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
GATEWAY_STREAM="$WORK_DIR/gateway-responses-failed.sse"
ADMIN_SUMMARY="$WORK_DIR/admin-summary.json"
ADMIN_EVENTS="$WORK_DIR/admin-events.json"
REPORT_JSON="$ARTIFACTS_DIR/report.json"

now_ms() {
  python3 -c 'import time; print(time.time_ns() // 1_000_000)'
}

echo "Starting fake OpenAI-compatible provider on port $PROVIDER_PORT with failing second stream..."
python3 "$ROOT_DIR/eng/fake_openai_tool_provider.py" \
  --port "$PROVIDER_PORT" \
  --log "$FAKE_PROVIDER_LOG" \
  --model "fake-maf-model" \
  --stream-tool-name "$TOOL_NAME" \
  --stream-tool-arguments-json "$TOOL_ARGUMENTS_JSON" \
  --fail-on-stream-final \
  --fail-status-code 500 \
  --fail-error-message "simulated upstream failure" \
  >"$FAKE_PROVIDER_STDOUT" 2>&1 &
PROVIDER_PID=$!

for _ in {1..30}; do
  if curl --silent --fail "http://127.0.0.1:$PROVIDER_PORT/health" >/dev/null; then
    break
  fi
  sleep 1
done

curl --silent --fail "http://127.0.0.1:$PROVIDER_PORT/health" >/dev/null

GATEWAY_CONFIG="$WORK_DIR/gateway.responses.failed.json"
python3 - <<'PY' "$GATEWAY_CONFIG" "$GATEWAY_PORT" "$PROVIDER_PORT" "$RUNTIME_MODE" "$ORCHESTRATOR"
import json
import pathlib
import sys

config_path = pathlib.Path(sys.argv[1])
gateway_port = int(sys.argv[2])
provider_port = int(sys.argv[3])
runtime_mode = sys.argv[4]
orchestrator = sys.argv[5]

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
            "StoragePath": str(config_path.parent / "memory"),
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
  if [[ -z "$RUNTIME_ID" ]]; then
    echo "Unable to determine runtime identifier for NativeAOT publish." >&2
    exit 1
  fi

  echo "Publishing NativeAOT gateway for Responses failure smoke ($RUNTIME_MODE/$ORCHESTRATOR) on $RUNTIME_ID..."
  publish_args=(
    -c Release
    -r "$RUNTIME_ID"
  )
else
  echo "Publishing JIT gateway for Responses failure smoke ($RUNTIME_MODE/$ORCHESTRATOR)..."
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
OPENCLAW_ENABLE_STREAMING_SMOKE_TOOL=1 \
  "$GATEWAY_BIN" --config "$GATEWAY_CONFIG" --doctor >"$WORK_DIR/doctor.log" 2>&1

echo "Starting gateway..."
GATEWAY_START_MS="$(now_ms)"
OPENCLAW_ENABLE_STREAMING_SMOKE_TOOL=1 \
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

echo "Issuing failing Responses API streaming tool request through the published gateway..."
REQUEST_START_MS="$(now_ms)"
curl --silent --fail --no-buffer \
  -H "Content-Type: application/json" \
  -d '{"model":"fake-maf-model","stream":true,"input":"Use the stream_echo tool and then summarize it."}' \
  "http://127.0.0.1:$GATEWAY_PORT/v1/responses" \
  >"$GATEWAY_STREAM"
REQUEST_END_MS="$(now_ms)"
GATEWAY_REQUEST_MS="$((REQUEST_END_MS - REQUEST_START_MS))"

echo "Fetching admin summary and runtime events..."
curl --silent --fail "http://127.0.0.1:$GATEWAY_PORT/admin/summary" >"$ADMIN_SUMMARY"
curl --silent --fail "http://127.0.0.1:$GATEWAY_PORT/admin/events?component=llm&channelId=openai-responses&limit=40" >"$ADMIN_EVENTS"

python3 "$ROOT_DIR/eng/verify_response_stream_failure_result.py" \
  --stream "$GATEWAY_STREAM" \
  --provider-log "$FAKE_PROVIDER_LOG" \
  --summary "$ADMIN_SUMMARY" \
  --events "$ADMIN_EVENTS" \
  --expected-mode "$RUNTIME_MODE" \
  --expected-orchestrator "$ORCHESTRATOR" \
  --expected-provider openai-compatible \
  --expected-model fake-maf-model \
  --expected-tool-name "$TOOL_NAME" \
  --expected-tool-arguments-json "$TOOL_ARGUMENTS_JSON" \
  --expected-tool-result "$EXPECTED_TOOL_RESULT" \
  --expected-error-code "$EXPECTED_ERROR_CODE" \
  --expected-error-message-substring "$EXPECTED_ERROR_MESSAGE_SUBSTRING" \
  --gateway-startup-ms "$GATEWAY_STARTUP_MS" \
  --gateway-request-ms "$GATEWAY_REQUEST_MS" \
  --report "$REPORT_JSON"

echo "Stopping gateway..."
kill "$GATEWAY_PID" >/dev/null 2>&1 || true
wait "$GATEWAY_PID" || true
unset GATEWAY_PID

echo "Stopping fake provider..."
kill "$PROVIDER_PID" >/dev/null 2>&1 || true
wait "$PROVIDER_PID" || true
unset PROVIDER_PID

echo "Responses failure smoke passed for $RUNTIME_MODE/$ORCHESTRATOR."

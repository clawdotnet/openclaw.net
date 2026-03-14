#!/usr/bin/env bash
set -euo pipefail

ROOT_DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
ARTIFACTS_DIR="${HTTP_PLUGIN_SMOKE_ARTIFACTS_DIR:?HTTP_PLUGIN_SMOKE_ARTIFACTS_DIR is required}"
RUNTIME_MODE="${HTTP_PLUGIN_SMOKE_RUNTIME_MODE:?HTTP_PLUGIN_SMOKE_RUNTIME_MODE is required}"
ORCHESTRATOR="${HTTP_PLUGIN_SMOKE_ORCHESTRATOR:?HTTP_PLUGIN_SMOKE_ORCHESTRATOR is required}"
GATEWAY_PORT="${HTTP_PLUGIN_SMOKE_GATEWAY_PORT:?HTTP_PLUGIN_SMOKE_GATEWAY_PORT is required}"
PROVIDER_PORT="${HTTP_PLUGIN_SMOKE_PROVIDER_PORT:?HTTP_PLUGIN_SMOKE_PROVIDER_PORT is required}"
NOTE_KEY="${HTTP_PLUGIN_SMOKE_NOTE_KEY:?HTTP_PLUGIN_SMOKE_NOTE_KEY is required}"
NOTE_CONTENT="${HTTP_PLUGIN_SMOKE_NOTE_CONTENT:?HTTP_PLUGIN_SMOKE_NOTE_CONTENT is required}"
PUBLISH_AOT="${HTTP_PLUGIN_SMOKE_PUBLISH_AOT:-0}"
ENABLE_MAF="${HTTP_PLUGIN_SMOKE_ENABLE_MAF:-0}"
KEEP_WORK_DIR="${KEEP_HTTP_PLUGIN_SMOKE_DIR:-0}"
CONFIG_SOURCE="${HTTP_PLUGIN_SMOKE_CONFIG_SOURCE:-env}"
WORK_DIR="$(mktemp -d "${TMPDIR:-/tmp}/openclaw-http-plugin-${RUNTIME_MODE}-${ORCHESTRATOR}.XXXXXX")"
RUNTIME_ID="${AOT_RUNTIME_IDENTIFIER:-$(dotnet --info | awk -F': ' '/ RID: / {print $2; exit}' | xargs)}"
PLUGIN_ID="bridge-note-plugin"
TOOL_NAME="bridge_note_write"

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

if ! command -v node >/dev/null 2>&1; then
  echo "Node.js is required for the plugin bridge smoke." >&2
  exit 1
fi

mkdir -p "$ARTIFACTS_DIR/gateway" "$WORK_DIR/plugin-output" "$WORK_DIR/plugin"

FAKE_PROVIDER_LOG="$WORK_DIR/fake-provider.jsonl"
FAKE_PROVIDER_STDOUT="$WORK_DIR/fake-provider.stdout.log"
GATEWAY_STDOUT="$WORK_DIR/gateway.stdout.log"
GATEWAY_RESPONSE="$WORK_DIR/gateway-response.json"
ADMIN_SUMMARY="$WORK_DIR/admin-summary.json"
ADMIN_EVENTS="$WORK_DIR/admin-events.json"
REPORT_JSON="$ARTIFACTS_DIR/report.json"
PLUGIN_DIR="$WORK_DIR/plugin/$PLUGIN_ID"
PLUGIN_OUTPUT_DIR="$WORK_DIR/plugin-output"
PLUGIN_OUTPUT_FILE="$PLUGIN_OUTPUT_DIR/$NOTE_KEY.txt"
TOOL_RESULT_FRAGMENT="Plugin note saved: $NOTE_KEY"

now_ms() {
  python3 -c 'import time; print(time.time_ns() // 1_000_000)'
}

find_available_port() {
  python3 - <<'PY' "$1"
import socket
import sys

start = int(sys.argv[1])
for port in range(start, start + 200):
    sock = socket.socket(socket.AF_INET, socket.SOCK_STREAM)
    try:
        sock.bind(("127.0.0.1", port))
    except OSError:
        sock.close()
        continue

    sock.close()
    print(port)
    raise SystemExit(0)

raise SystemExit(f"No free port found starting at {start}.")
PY
}

GATEWAY_PORT="$(find_available_port "$GATEWAY_PORT")"
PROVIDER_PORT="$(find_available_port "$PROVIDER_PORT")"

mkdir -p "$PLUGIN_DIR"

python3 - <<'PY' "$PLUGIN_DIR/openclaw.plugin.json"
import json
import pathlib
import sys

path = pathlib.Path(sys.argv[1])
manifest = {
    "id": "bridge-note-plugin",
    "name": "Bridge Note Plugin",
    "version": "1.0.0",
    "description": "Writes note content to a plugin-managed artifact file.",
}
path.write_text(json.dumps(manifest, indent=2) + "\n", encoding="utf-8")
PY

python3 - <<'PY' "$PLUGIN_DIR/index.js"
import pathlib
import sys

path = pathlib.Path(sys.argv[1])
path.write_text(
    """const fs = require("node:fs");
const path = require("node:path");

module.exports = function(api) {
  api.registerTool({
    name: "bridge_note_write",
    description: "Write plugin-managed note content to a deterministic artifact file.",
    parameters: {
      type: "object",
      properties: {
        key: { type: "string" },
        content: { type: "string" }
      },
      required: ["key", "content"]
    },
    execute: async (_pluginId, params) => {
      const pluginConfig = api.config ?? api.pluginConfig ?? {};
      const outputDir = String(pluginConfig.outputDir ?? process.env.OPENCLAW_PLUGIN_OUTPUT_DIR ?? "");
      if (!outputDir) {
        throw new Error("plugin config outputDir or OPENCLAW_PLUGIN_OUTPUT_DIR is required");
      }
      fs.mkdirSync(outputDir, { recursive: true });
      const key = String(params.key ?? "note");
      const content = String(params.content ?? "");
      const outputPath = path.join(outputDir, `${key}.txt`);
      fs.writeFileSync(outputPath, content, "utf8");
      return `Plugin note saved: ${key}`;
    }
  });
};
""",
    encoding="utf-8",
)
PY

TOOL_ARGUMENTS_JSON="$(python3 - <<'PY' "$NOTE_KEY" "$NOTE_CONTENT"
import json
import sys

print(json.dumps({"key": sys.argv[1], "content": sys.argv[2]}))
PY
)"

echo "Starting fake OpenAI-compatible provider on port $PROVIDER_PORT..."
python3 "$ROOT_DIR/eng/fake_openai_tool_provider.py" \
  --port "$PROVIDER_PORT" \
  --log "$FAKE_PROVIDER_LOG" \
  --tool-name "$TOOL_NAME" \
  --tool-arguments-json "$TOOL_ARGUMENTS_JSON" \
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

GATEWAY_CONFIG="$WORK_DIR/gateway.http.plugin.json"
python3 - <<'PY' "$GATEWAY_CONFIG" "$GATEWAY_PORT" "$PROVIDER_PORT" "$RUNTIME_MODE" "$ORCHESTRATOR" "$PLUGIN_DIR" "$PLUGIN_OUTPUT_DIR" "$CONFIG_SOURCE"
import json
import pathlib
import sys

config_path = pathlib.Path(sys.argv[1])
gateway_port = int(sys.argv[2])
provider_port = int(sys.argv[3])
runtime_mode = sys.argv[4]
orchestrator = sys.argv[5]
plugin_dir = sys.argv[6]
plugin_output_dir = sys.argv[7]
config_source = sys.argv[8]

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
        "Plugins": {
            "Enabled": True,
            "Load": {
                "Paths": [plugin_dir],
            },
        },
    },
}

if config_source == "plugin_config":
    config["OpenClaw"]["Plugins"]["Entries"] = {
        "bridge-note-plugin": {
            "Config": {
                "outputDir": plugin_output_dir,
            }
        }
    }

config_path.write_text(json.dumps(config, indent=2) + "\n", encoding="utf-8")
PY

if [[ "$PUBLISH_AOT" == "1" ]]; then
  if [[ -z "$RUNTIME_ID" ]]; then
    echo "Unable to determine runtime identifier for NativeAOT publish." >&2
    exit 1
  fi

  echo "Publishing NativeAOT gateway for HTTP plugin smoke ($RUNTIME_MODE/$ORCHESTRATOR) on $RUNTIME_ID..."
  publish_args=(
    -c Release
    -r "$RUNTIME_ID"
  )
else
  echo "Publishing JIT gateway for HTTP plugin smoke ($RUNTIME_MODE/$ORCHESTRATOR)..."
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

run_gateway_command() {
  if [[ "$CONFIG_SOURCE" != "plugin_config" ]]; then
    env OPENCLAW_PLUGIN_OUTPUT_DIR="$PLUGIN_OUTPUT_DIR" "$@"
  else
    "$@"
  fi
}

echo "Running gateway --doctor..."
run_gateway_command \
  "$GATEWAY_BIN" --config "$GATEWAY_CONFIG" --doctor >"$WORK_DIR/doctor.log" 2>&1

echo "Starting gateway..."
GATEWAY_START_MS="$(now_ms)"
run_gateway_command \
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

echo "Issuing OpenAI-compatible request through the published gateway..."
REQUEST_START_MS="$(now_ms)"
curl --silent --fail \
  -H "Content-Type: application/json" \
  -d '{"model":"fake-maf-model","messages":[{"role":"user","content":"Use the bridge_note_write tool to save a plugin note."}]}' \
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
  --artifact-path "$PLUGIN_OUTPUT_FILE" \
  --summary "$ADMIN_SUMMARY" \
  --events "$ADMIN_EVENTS" \
  --expected-mode "$RUNTIME_MODE" \
  --expected-orchestrator "$ORCHESTRATOR" \
  --expected-provider openai-compatible \
  --expected-model fake-maf-model \
  --expected-note-key "$NOTE_KEY" \
  --expected-note-content "$NOTE_CONTENT" \
  --expected-result-fragment "$TOOL_RESULT_FRAGMENT" \
  --expected-plugin-id "$PLUGIN_ID" \
  --expected-plugin-tool-count 1 \
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

echo "HTTP plugin smoke passed for $RUNTIME_MODE/$ORCHESTRATOR."

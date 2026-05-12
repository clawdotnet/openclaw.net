#!/usr/bin/env bash
set -euo pipefail

ROOT_DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
OUTPUT_ROOT="${OUTPUT_ROOT:-$ROOT_DIR/artifacts/releases}"
CONFIGURATION="${CONFIGURATION:-Release}"
RUNTIME_ID="${AOT_RUNTIME_IDENTIFIER:-$(dotnet --info | awk -F': ' '/ RID: / {print $2; exit}' | xargs)}"

publish_artifact() {
  local name="$1"
  shift

  local output_dir="$OUTPUT_ROOT/$name"
  rm -rf "$output_dir"
  mkdir -p "$output_dir"

  echo "Publishing $name ..."
  dotnet publish "$ROOT_DIR/src/OpenClaw.Gateway/OpenClaw.Gateway.csproj" "$@" -o "$output_dir"
}

mkdir -p "$OUTPUT_ROOT"

if [[ -z "$RUNTIME_ID" ]]; then
  echo "Unable to determine runtime identifier for NativeAOT publish. Set AOT_RUNTIME_IDENTIFIER." >&2
  exit 1
fi

publish_artifact \
  "gateway-jit" \
  -c "$CONFIGURATION" \
  -p:PublishAot=false

publish_artifact \
  "gateway-aot" \
  -c "$CONFIGURATION" \
  -r "$RUNTIME_ID" \
  -p:PublishAot=true

cat <<EOF

Published artifacts:
- $OUTPUT_ROOT/gateway-jit
- $OUTPUT_ROOT/gateway-aot

Runtime selection:
- Normal artifacts: Runtime.Orchestrator=native|maf; native remains the default.
- A2A and durable workflow backends remain opt-in by configuration.
EOF

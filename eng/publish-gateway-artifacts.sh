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
  "gateway-standard-jit" \
  -c "$CONFIGURATION" \
  -p:PublishAot=false \
  -p:OpenClawEnableMafExperiment=false

publish_artifact \
  "gateway-maf-enabled-jit" \
  -c "$CONFIGURATION" \
  -p:PublishAot=false \
  -p:OpenClawEnableMafExperiment=true

publish_artifact \
  "gateway-standard-aot" \
  -c "$CONFIGURATION" \
  -r "$RUNTIME_ID" \
  -p:PublishAot=true \
  -p:OpenClawEnableMafExperiment=false

publish_artifact \
  "gateway-maf-enabled-aot" \
  -c "$CONFIGURATION" \
  -r "$RUNTIME_ID" \
  -p:PublishAot=true \
  -p:OpenClawEnableMafExperiment=true

cat <<EOF

Published artifacts:
- $OUTPUT_ROOT/gateway-standard-jit
- $OUTPUT_ROOT/gateway-maf-enabled-jit
- $OUTPUT_ROOT/gateway-standard-aot
- $OUTPUT_ROOT/gateway-maf-enabled-aot

Runtime selection:
- Standard artifacts: Runtime.Orchestrator=native only; maf fails fast at startup.
- MAF-enabled artifacts: Runtime.Orchestrator=native|maf; native remains the default.
EOF

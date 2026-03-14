#!/usr/bin/env bash
set -euo pipefail

ROOT_DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"

bash "$ROOT_DIR/eng/verify-jit-native-http-stream-tool-smoke.sh"
bash "$ROOT_DIR/eng/verify-jit-maf-http-stream-tool-smoke.sh"
bash "$ROOT_DIR/eng/verify-aot-native-http-stream-tool-smoke.sh"
bash "$ROOT_DIR/eng/verify-aot-maf-http-stream-tool-smoke.sh"

python3 "$ROOT_DIR/eng/generate_maf_aot_jit_stream_tool_findings.py" \
  --output "$ROOT_DIR/docs/experiments/maf-aot-jit-stream-tool-findings.md" \
  "$ROOT_DIR/artifacts/jit-native-http-stream-tool/report.json" \
  "$ROOT_DIR/artifacts/jit-maf-http-stream-tool/report.json" \
  "$ROOT_DIR/artifacts/aot-native-http-stream-tool/report.json" \
  "$ROOT_DIR/artifacts/aot-maf-http-stream-tool/report.json"

echo "Generated docs/experiments/maf-aot-jit-stream-tool-findings.md"

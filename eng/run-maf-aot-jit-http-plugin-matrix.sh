#!/usr/bin/env bash
set -euo pipefail

ROOT_DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"

bash "$ROOT_DIR/eng/verify-jit-native-http-plugin-smoke.sh"
bash "$ROOT_DIR/eng/verify-jit-maf-http-plugin-smoke.sh"
bash "$ROOT_DIR/eng/verify-aot-native-http-plugin-smoke.sh"
bash "$ROOT_DIR/eng/verify-aot-maf-http-plugin-smoke.sh"

python3 "$ROOT_DIR/eng/generate_maf_aot_jit_plugin_findings.py" \
  --output "$ROOT_DIR/docs/experiments/maf-aot-jit-plugin-findings.md" \
  "$ROOT_DIR/artifacts/jit-native-http-plugin/report.json" \
  "$ROOT_DIR/artifacts/jit-maf-http-plugin/report.json" \
  "$ROOT_DIR/artifacts/aot-native-http-plugin/report.json" \
  "$ROOT_DIR/artifacts/aot-maf-http-plugin/report.json"

echo "Generated docs/experiments/maf-aot-jit-plugin-findings.md"

#!/usr/bin/env bash
set -euo pipefail

ROOT_DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"

bash "$ROOT_DIR/eng/verify-jit-native-ws-restart-smoke.sh"
bash "$ROOT_DIR/eng/verify-jit-maf-ws-restart-smoke.sh"
bash "$ROOT_DIR/eng/verify-aot-native-ws-restart-smoke.sh"
bash "$ROOT_DIR/eng/verify-aot-maf-ws-restart-smoke.sh"

python3 "$ROOT_DIR/eng/generate_maf_aot_jit_restart_findings.py" \
  --output "$ROOT_DIR/docs/experiments/maf-aot-jit-restart-findings.md" \
  "$ROOT_DIR/artifacts/jit-native-ws-restart/report.json" \
  "$ROOT_DIR/artifacts/jit-maf-ws-restart/report.json" \
  "$ROOT_DIR/artifacts/aot-native-ws-restart/report.json" \
  "$ROOT_DIR/artifacts/aot-maf-ws-restart/report.json"

echo "Generated docs/experiments/maf-aot-jit-restart-findings.md"

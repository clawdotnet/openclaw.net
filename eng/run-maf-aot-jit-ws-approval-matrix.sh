#!/usr/bin/env bash
set -euo pipefail

ROOT_DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"

bash "$ROOT_DIR/eng/verify-jit-native-ws-approval-smoke.sh"
bash "$ROOT_DIR/eng/verify-jit-maf-ws-approval-smoke.sh"
bash "$ROOT_DIR/eng/verify-aot-native-ws-approval-smoke.sh"
bash "$ROOT_DIR/eng/verify-aot-maf-ws-approval-smoke.sh"

python3 "$ROOT_DIR/eng/generate_maf_aot_jit_approval_findings.py" \
  --output "$ROOT_DIR/docs/experiments/maf-aot-jit-approval-findings.md" \
  "$ROOT_DIR/artifacts/jit-native-ws-approval/report.json" \
  "$ROOT_DIR/artifacts/jit-maf-ws-approval/report.json" \
  "$ROOT_DIR/artifacts/aot-native-ws-approval/report.json" \
  "$ROOT_DIR/artifacts/aot-maf-ws-approval/report.json"

echo "Generated docs/experiments/maf-aot-jit-approval-findings.md"

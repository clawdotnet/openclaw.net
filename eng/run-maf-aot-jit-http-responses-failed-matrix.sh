#!/usr/bin/env bash
set -euo pipefail

ROOT_DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"

bash "$ROOT_DIR/eng/verify-jit-native-http-responses-failed-smoke.sh"
bash "$ROOT_DIR/eng/verify-jit-maf-http-responses-failed-smoke.sh"
bash "$ROOT_DIR/eng/verify-aot-native-http-responses-failed-smoke.sh"
bash "$ROOT_DIR/eng/verify-aot-maf-http-responses-failed-smoke.sh"

python3 "$ROOT_DIR/eng/generate_maf_aot_jit_responses_failed_findings.py" \
  --output "$ROOT_DIR/docs/experiments/maf-aot-jit-responses-failed-findings.md" \
  "$ROOT_DIR/artifacts/jit-native-http-responses-failed/report.json" \
  "$ROOT_DIR/artifacts/jit-maf-http-responses-failed/report.json" \
  "$ROOT_DIR/artifacts/aot-native-http-responses-failed/report.json" \
  "$ROOT_DIR/artifacts/aot-maf-http-responses-failed/report.json"

echo "Generated docs/experiments/maf-aot-jit-responses-failed-findings.md"

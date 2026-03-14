#!/usr/bin/env bash
set -euo pipefail

ROOT_DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"

bash "$ROOT_DIR/eng/verify-jit-native-ws-approval-outcomes-smoke.sh"
bash "$ROOT_DIR/eng/verify-jit-maf-ws-approval-outcomes-smoke.sh"
bash "$ROOT_DIR/eng/verify-aot-native-ws-approval-outcomes-smoke.sh"
bash "$ROOT_DIR/eng/verify-aot-maf-ws-approval-outcomes-smoke.sh"
python3 "$ROOT_DIR/eng/generate_maf_aot_jit_approval_outcome_findings.py"

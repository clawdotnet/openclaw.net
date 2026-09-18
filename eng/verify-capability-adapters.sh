#!/usr/bin/env bash
set -euo pipefail
cd "$(dirname "$0")/.."
project=src/OpenClaw.Gateway/OpenClaw.Gateway.csproj
fast=(-c Release -p:OpenClawSkipDashboardBuild=true --nologo)

dotnet build "$project" "${fast[@]}"
python3 - <<'PY'
import json
from pathlib import Path
assets = json.loads(Path('src/OpenClaw.Gateway/obj/standard/project.assets.json').read_text())
vendors = [p for p in assets['libraries'] if 'nacos' in p.lower()]
assert not vendors, f'Default Gateway must not depend on Nacos: {vendors}'
PY

dotnet build "$project" "${fast[@]}" -p:OpenClawEnableNacos=true
python3 - <<'PY'
import json
from pathlib import Path
assets = json.loads(Path('src/OpenClaw.Gateway/obj/standard-nacos/project.assets.json').read_text())
assert not any(p.lower().startswith('rednb.') for p in assets['libraries']), 'Router adapter must not pull in the SDK'
PY

dotnet build "$project" "${fast[@]}" -p:OpenClawEnableNacos=true -p:OpenClawEnableNacosEvents=true -p:PublishAot=false
log=$(mktemp)
trap 'rm -f "$log"' EXIT
if dotnet build "$project" "${fast[@]}" -p:OpenClawEnableNacos=true -p:OpenClawEnableNacosEvents=true -p:PublishAot=true >"$log" 2>&1; then
  echo 'NativeAOT SDK events unexpectedly succeeded' >&2
  exit 1
fi
if ! grep -Eq 'Nacos.*(JIT|NativeAOT)' "$log"; then
  cat "$log"
  exit 1
fi

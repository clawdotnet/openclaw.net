#!/usr/bin/env python3
"""Assert that only NativeAOT publish removes the optional Vault integration."""
import json
import subprocess
from pathlib import Path

root = Path(__file__).resolve().parent.parent
for publishing, aot, excluded in [(False, True, False), (True, False, False), (True, True, True)]:
    output = subprocess.check_output([
        'dotnet', 'msbuild', 'src/OpenClaw.Gateway/OpenClaw.Gateway.csproj',
        '-getProperty:DefineConstants', '-getItem:ProjectReference',
        f'-p:_IsPublishing={str(publishing).lower()}', f'-p:PublishAot={str(aot).lower()}',
    ], cwd=root, text=True)
    data = json.loads(output)
    has_reference = any('OpenClaw.Security.Vault' in item['Identity'] for item in data['Items']['ProjectReference'])
    has_exclusion = 'OPENCLAW_VAULT_EXCLUDED' in data['Properties']['DefineConstants'].split(';')
    assert has_reference == (not excluded), (publishing, aot, 'project reference')
    assert has_exclusion == excluded, (publishing, aot, 'compile boundary')
    print(f'Vault boundary OK: publishing={publishing}, aot={aot}, excluded={excluded}')

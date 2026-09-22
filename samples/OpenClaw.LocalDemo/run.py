#!/usr/bin/env python3
"""One-command local gateway + Ollama + browser demo. Requires Python, .NET 10 and Docker."""
import argparse
import json
import os
from pathlib import Path
import socket
import subprocess
import tempfile
import time
import urllib.request
import webbrowser

REPO = Path(__file__).resolve().parents[2]


def port():
    with socket.socket() as listener:
        listener.bind(('127.0.0.1', 0))
        return listener.getsockname()[1]


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument('--model', default='llama3.2:3b')
    parser.add_argument('--no-browser', action='store_true')
    parser.add_argument('--prepare-only', action='store_true', help='Write and print isolated demo config without starting services')
    args = parser.parse_args()
    home = Path(tempfile.mkdtemp(prefix='openclaw-demo-'))
    workspace = home / 'workspace'
    workspace.mkdir()
    (workspace / 'project-notes.txt').write_text('Demo project: Community garden\nBudget: $500\nTasks: buy seeds, recruit 5 volunteers, schedule Saturday planting.\nBlocker: water access approval pending.\n', encoding='utf-8')
    gateway_port, ollama_port = port(), port()
    config = {'OpenClaw': {
        'BindAddress': '127.0.0.1', 'Port': gateway_port,
        'Llm': {'Provider': 'ollama', 'Model': args.model, 'Endpoint': f'http://127.0.0.1:{ollama_port}'},
        'Memory': {'Provider': 'file', 'StoragePath': str(home / 'memory'), 'Sqlite': {'DbPath': str(home / 'memory/openclaw.db')}, 'Retention': {'ArchivePath': str(home / 'archive')}},
        'Plugins': {'Enabled': False},
        'Tooling': {'AllowShell': False, 'EnableBrowserTool': False,
                    'AllowedReadRoots': [str(workspace)], 'AllowedWriteRoots': [str(workspace)],
                    'Audiences': {'Enabled': True, 'DefaultAudience': 'demo',
                                  'Profiles': {'demo': {'AllowedTools': ['read_file'], 'IncludePrivateContext': False}}}}
    }}
    config_path = home / 'gateway.json'
    config_path.write_text(json.dumps(config, indent=2), encoding='utf-8')
    print(f'Isolated demo files: {home}', flush=True)
    if args.prepare_only:
        return
    container = 'openclaw-demo-' + home.name.removeprefix('openclaw-demo-').lower()
    gateway = None
    started = False
    try:
        subprocess.run(['docker', 'info'], check=True, stdout=subprocess.DEVNULL)
        subprocess.run(['dotnet', 'build', str(REPO / 'src/OpenClaw.Gateway'), '-c', 'Debug', '-v', 'quiet'], check=True)
        subprocess.run(['docker', 'run', '--detach', '--rm', '--name', container,
                        '-p', f'127.0.0.1:{ollama_port}:11434', '-v', 'openclaw-demo-models:/root/.ollama',
                        'ollama/ollama:latest'], check=True)
        started = True
        print(f'Pulling {args.model}; model weights are cached in the openclaw-demo-models Docker volume.', flush=True)
        for _ in range(60):
            if subprocess.run(['docker', 'exec', container, 'ollama', 'list'], stdout=subprocess.DEVNULL, stderr=subprocess.DEVNULL).returncode == 0:
                break
            time.sleep(1)
        subprocess.run(['docker', 'exec', container, 'ollama', 'pull', args.model], check=True)
        env = {k: v for k, v in os.environ.items() if not k.lower().startswith(('openclaw', 'model_provider'))}
        env['OPENCLAW_WORKSPACE'] = str(workspace)
        env['ASPNETCORE_ENVIRONMENT'] = 'Development'
        with (home / 'gateway.log').open('w') as log:
            gateway = subprocess.Popen(['dotnet', str(REPO / 'src/OpenClaw.Gateway/bin/Debug/net10.0/OpenClaw.Gateway.dll'), '--config', str(config_path)], cwd=REPO / 'src/OpenClaw.Gateway', env=env, stdout=log, stderr=subprocess.STDOUT)
            url = f'http://127.0.0.1:{gateway_port}/chat'
            for _ in range(120):
                if gateway.poll() is not None:
                    raise RuntimeError(f'Gateway exited; inspect {home / "gateway.log"}')
                try:
                    with urllib.request.urlopen(url, timeout=1):
                        break
                except OSError:
                    time.sleep(1)
            else:
                raise RuntimeError(f'Gateway did not become ready; inspect {home / "gateway.log"}')
            print(f'Open {url}\nTry: Read {workspace / "project-notes.txt"} and produce a three-step plan with the blocker first.\nPress Ctrl+C to stop. Model cache and demo files are retained.', flush=True)
            if not args.no_browser:
                webbrowser.open(url)
            gateway.wait()
    except KeyboardInterrupt:
        # Ctrl+C is the normal way to stop the interactive demo.
        pass
    finally:
        if gateway and gateway.poll() is None:
            gateway.terminate()
            try:
                gateway.wait(timeout=15)
            except subprocess.TimeoutExpired:
                gateway.kill()
                gateway.wait()
        if started:
            subprocess.run(['docker', 'stop', container], stdout=subprocess.DEVNULL)


if __name__ == '__main__':
    main()

#!/usr/bin/env python3
"""Provision isolated Nacos 3.2.4 + Router 0.2.2, then check production adapters.

Requires Java 17+, .NET 10 and a Python environment with requirements.txt.
Never connects to an existing Nacos deployment. Only owned child processes and
the temporary deployment are cleaned up; reports/logs remain in --output.
"""
import argparse
import base64
import hashlib
import json
import os
from pathlib import Path
import secrets
import signal
import shutil
import socket
import subprocess
import sys
import tarfile
import tempfile
import time
import urllib.error
import urllib.parse
import urllib.request

ROOT = Path(__file__).resolve().parents[2]
HERE = Path(__file__).resolve().parent
VERSION = "3.2.4"


def request(url, data=None, headers=None):
    body = urllib.parse.urlencode(data).encode() if data is not None else None
    with urllib.request.urlopen(urllib.request.Request(url, data=body, headers=headers or {}), timeout=5) as response:
        return json.load(response)


def wait_for(check, seconds, description):
    deadline = time.monotonic() + seconds
    while time.monotonic() < deadline:
        try:
            if check():
                return
        except (OSError, ValueError):
            pass
        time.sleep(0.25)
    raise TimeoutError(description)


def free_port(offsets=(0,)):
    # Nacos gRPC ports are defined relative to the main port (+1000/+1001).
    for _ in range(100):
        port = 20000 + secrets.randbelow(20000)
        held = []
        try:
            for offset in offsets:
                held.append(socket.socket())
                held[-1].bind(("127.0.0.1", port + offset))
            return port
        except OSError:
            pass
        finally:
            for sock in held:
                sock.close()
    raise RuntimeError("Cannot reserve acceptance ports")


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--archive", type=Path, required=True, help="Official nacos-server-3.2.4.tar.gz")
    parser.add_argument("--python", default=sys.executable, help="Python with the pinned Router requirements")
    parser.add_argument("--managed", type=Path, required=True, help="Built NacosLiveSmoke.dll")
    parser.add_argument("--native", type=Path, required=True, help="Published NativeAOT NacosLiveSmoke executable")
    parser.add_argument("--test-dll", type=Path, help="Built OpenClaw.Tests.dll; runs both runtime acceptance tests")
    parser.add_argument("--output", type=Path, required=True, help="New directory for reports and logs")
    parser.add_argument("--live-weather", action="store_true", help="Use Open-Meteo instead of labelled fixture observations")
    args = parser.parse_args()
    args.output = args.output.resolve()
    args.output.mkdir(parents=True, exist_ok=False)
    os.chmod(args.output, 0o700)
    # Pin the exact official release archive, downloaded over GitHub HTTPS and
    # checked against the upstream release digest before recording this SHA-256.
    with args.archive.open("rb") as archive:
        if hashlib.file_digest(archive, "sha256").hexdigest() != "da5eec77934140133fe93e5532079e4e99b4cae7eb50463f2f6cd2bc8f380a70":
            raise ValueError("Archive does not match the official Nacos 3.2.4 release")
    children = []
    logs = []

    def start(name, command, env=None, cwd=None):
        log = (args.output / (name + ".log")).open("w")
        logs.append(log)
        process = subprocess.Popen(command, cwd=cwd, env=env, stdout=log, stderr=subprocess.STDOUT, start_new_session=True)
        children.append(process)
        return process

    work = Path(tempfile.mkdtemp(prefix="openclaw-nacos-"))
    try:
        with tarfile.open(args.archive) as archive:
            archive.extractall(work, filter="data")
        nacos = work / "nacos"
        port = free_port((0, 1000, 1001))
        console_port, router_port = free_port(), free_port()
        password = secrets.token_urlsafe(24)
        properties = nacos / "conf/application.properties"
        properties.write_text(properties.read_text() + "\n" + "\n".join([
            f"nacos.server.main.port={port}", f"nacos.console.port={console_port}",
            "server.address=127.0.0.1", "nacos.inetutils.ip-address=127.0.0.1",
            "nacos.core.auth.enabled=true", "nacos.core.auth.admin.enabled=true",
            "nacos.core.auth.console.enabled=true", "nacos.core.auth.caching.enabled=false", "nacos.ai.mcp.registry.enabled=false",
            "nacos.ai.skill.registry.enabled=false", "nacos.core.auth.server.identity.key=acceptance",
            "nacos.core.auth.server.identity.value=" + secrets.token_urlsafe(24),
            "nacos.core.auth.plugin.nacos.token.secret.key=" + base64.b64encode(secrets.token_bytes(48)).decode(),
        ]) + "\n")
        os.chmod(properties, 0o600)
        server = start("nacos", ["java", "-Xms256m", "-Xmx512m", "-Dnacos.standalone=true",
            "--add-opens=java.base/java.lang=ALL-UNNAMED", "--add-opens=java.base/java.lang.reflect=ALL-UNNAMED",
            "--add-opens=java.base/java.util=ALL-UNNAMED",
            "-Dnacos.deployment.type=merged", f"-Dnacos.home={nacos}", f"-Dloader.path={nacos}/plugins", "-jar", str(nacos / "target/nacos-server.jar"),
            f"--spring.config.additional-location=file:{nacos}/conf/", f"--logging.config={nacos}/conf/nacos-logback.xml"], cwd=work)
        console = f"http://127.0.0.1:{console_port}"
        def ready():
            if server.poll() is not None:
                raise RuntimeError("Nacos exited; see nacos.log")
            return request(console + "/v3/console/health/readiness")

        wait_for(ready, 90, "Nacos readiness failed; see nacos.log")
        initialized = request(console + "/v3/auth/user/admin", {"password": password})
        if initialized.get("code") != 0:
            raise RuntimeError("Isolated Nacos administrator initialization failed")
        login = request(console + "/v3/auth/user/login", {"username": "nacos", "password": password})
        addr = f"127.0.0.1:{port}"
        weather_args = [str(HERE / "weather_server.py")] + (["--live-weather"] if args.live_weather else [])
        specification = {
            "name": "weather-mcp", "description": "weather city 天气 城市 Oslo temperature query",
            "protocol": "stdio", "enabled": True, "versionDetail": {"version": "1.0.0"},
            "localServerConfig": {"mcpServers": {"weather-mcp": {"command": str(Path(args.python).absolute()), "args": weather_args}}}
        }
        registered = request(f"http://{addr}/nacos/v3/admin/ai/mcp", {
            "mcpName": "weather-mcp", "serverSpecification": json.dumps(specification),
            "toolSpecification": json.dumps({"tools": []}), "endpointSpecification": "{}"
        }, {"accessToken": login["accessToken"]})
        if registered.get("code") != 0:
            raise RuntimeError("Weather MCP registration failed: " + str(registered.get("message")))
        env = os.environ.copy()
        env.update({"NACOS_ADDR": addr, "NACOS_USERNAME": "nacos", "NACOS_PASSWORD": password,
            "NACOS_NAMESPACE": "", "ACCESS_KEY_ID": "", "ACCESS_KEY_SECRET": "", "MODE": "router",
            "TRANSPORT_TYPE": "streamable_http", "PORT": str(router_port), "UPDATE_INTERVAL": "2",
            "ANONYMIZED_TELEMETRY": "False", "OPENCLAW_NACOS_LIVE": "1",
            "OPENCLAW_NACOS_ROUTER_URL": f"http://127.0.0.1:{router_port}/mcp",
            "OPENCLAW_NACOS_SERVER": addr, "OPENCLAW_NACOS_USERNAME": "nacos", "OPENCLAW_NACOS_PASSWORD": password})
        router = start("router", [args.python, str(HERE / "router_server.py")], env=env, cwd=work)

        def listening():
            if router.poll() is not None:
                raise RuntimeError("Router exited; see router.log")
            with socket.create_connection(("127.0.0.1", router_port), timeout=1):
                return True

        wait_for(listening, 90, "Router startup timed out")
        # Each smoke checks initial listener reconciliation, warm static/dynamic
        # cache hits, <=2s invalidation, and both rebinds with reflection disabled.
        for name, command in [("managed", ["dotnet", str(args.managed.resolve())]), ("native", [str(args.native.resolve())])]:
            env["OPENCLAW_NACOS_REPORT"] = str(args.output / (name + ".json"))
            smoke = start(name, command, env=env, cwd=work)
            if smoke.wait(timeout=210) != 0:
                raise RuntimeError(name + " acceptance failed; see " + name + ".log")
            report = json.loads((args.output / (name + ".json")).read_text())
            if report["nativeAot"] != (name == "native") or report["jsonReflection"]:
                raise RuntimeError(name + " execution mode was not verified")
            print(name, json.dumps(report), flush=True)
        if args.test_dll:
            tests = start("runtimes", ["dotnet", "test", str(args.test_dll.resolve()), "--filter", "FullyQualifiedName~LiveRouter", "--logger", f"trx;LogFileName={args.output}/live-runtimes.trx"], env=env, cwd=ROOT)
            if tests.wait(timeout=300) != 0:
                raise RuntimeError("Dual-runtime acceptance failed; see runtimes.log")
        (args.output / "acceptance.json").write_text(json.dumps({"nacos": VERSION, "router": "0.2.2", "authentication": True,
            "weather": "Open-Meteo" if args.live_weather else "labelled fixture", "dualRuntimeTests": bool(args.test_dll), "passed": True}, indent=2))
        print("NACOS_LIVE_ACCEPTANCE_PASS", args.output, flush=True)
    finally:
        for process in reversed(children):
            # The router can own stdio MCP children after its parent exits.
            try:
                os.killpg(process.pid, signal.SIGTERM)
            except ProcessLookupError:
                pass
        for process in reversed(children):
            try:
                process.wait(timeout=10)
            except subprocess.TimeoutExpired:
                os.killpg(process.pid, signal.SIGKILL)
                process.wait(timeout=5)
        for log in logs:
            log.close()
        shutil.rmtree(work)


if __name__ == "__main__":
    main()

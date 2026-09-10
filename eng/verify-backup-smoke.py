#!/usr/bin/env python3
"""Exercise a published CLI's offline backup and SQLite restore without starting a host."""
import json
from pathlib import Path
import sqlite3
import subprocess
import sys
import tempfile

binary = str(Path(sys.argv[1]).resolve(strict=True))
with tempfile.TemporaryDirectory(prefix="openclaw-backup-smoke-") as temporary:
    root = Path(temporary).resolve()
    source = root / "source"
    source.mkdir()
    with sqlite3.connect(source / "state.sqlite3") as database:
        database.execute("CREATE TABLE goals (objective TEXT)")
        database.execute("INSERT INTO goals VALUES ('restore without dispatch')")
    (source / "schedules.json").write_text('{"enabled":true}', encoding="utf-8")
    plan = root / "plan.json"
    plan.write_text(json.dumps({
        "Roots": {"state": str(source)},
        "Categories": {key: "state" for key in ["configuration", "sessions", "goals", "schedules", "governance"]},
        "SecretReferences": ["env:SMOKE_REFERENCE_ONLY"]
    }), encoding="utf-8")
    backup, restored = root / "backup", root / "restored"
    for arguments in [
        ["backup", "-h"],
        ["backup", "create", str(plan), str(backup), "--offline"],
        ["backup", "validate", str(backup)],
        ["backup", "restore", str(backup), str(restored)],
    ]:
        result = subprocess.run([binary, *arguments], capture_output=True, text=True, timeout=60)
        if result.returncode:
            raise RuntimeError(f"Published CLI failed: {result.stdout}\n{result.stderr}")
    with sqlite3.connect(restored / "state" / "state.sqlite3") as database:
        assert database.execute("SELECT objective FROM goals").fetchone()[0] == "restore without dispatch"
    assert (restored / "RESTORE-REQUIRES-REVIEW.txt").exists()
    (backup / "payload" / "state" / "schedules.json").write_text("corrupted", encoding="utf-8")
    failed_restore = root / "must-not-exist"
    result = subprocess.run([binary, "backup", "restore", str(backup), str(failed_restore)], capture_output=True, text=True, timeout=60)
    assert result.returncode != 0 and not failed_restore.exists()
print("Published CLI backup/validate/SQLite restore and corruption refusal passed; no host was started.")

#!/usr/bin/env python3
"""Provider-neutral entry point; the previous Jev command remains supported."""
from pathlib import Path
import runpy

if __name__ == "__main__":
    runpy.run_path(str(Path(__file__).with_name("evaluate-jev-routing.py")), run_name="__main__")

#!/usr/bin/env python3
"""Reject release documentation that still describes the release tag as future work."""

from pathlib import Path
import re
import sys


def normalize_tag(value: str) -> tuple[str, str]:
    tag = value.strip()
    match = re.fullmatch(r"v?(\d+\.\d+\.\d+(?:-[0-9A-Za-z.-]+)?)", tag)
    if match is None:
        raise ValueError(f"Expected a semantic release tag such as v0.3.0, got {value!r}.")
    version = match.group(1)
    return f"v{version}", version


def markdown_files(root: Path) -> list[Path]:
    candidates = [root / "README.md", root / "CHANGELOG.md"]
    candidates.extend((root / "docs").rglob("*.md"))
    return sorted(path for path in candidates if path.is_file())


def find_stale_references(root: Path, tag: str, version: str) -> list[str]:
    escaped_tag = re.escape(tag)
    escaped_version = re.escape(version)
    patterns = [
        re.compile(rf"\btarget(?:ed)?(?:\s+for)?\s+\*{{0,2}}{escaped_tag}\*{{0,2}}", re.IGNORECASE),
        re.compile(rf"\bmain[ -]only\b[^\n]{{0,120}}\b{escaped_tag}\b", re.IGNORECASE),
        re.compile(rf"\bnot (?:available|included) in\s+\*{{0,2}}{escaped_tag}\*{{0,2}}", re.IGNORECASE),
        re.compile(rf"\bplanned for\s+\*{{0,2}}v?{escaped_version}\*{{0,2}}", re.IGNORECASE),
    ]
    failures: list[str] = []
    for path in markdown_files(root):
        for line_number, line in enumerate(path.read_text(encoding="utf-8-sig").splitlines(), start=1):
            if any(pattern.search(line) for pattern in patterns):
                failures.append(f"{path.relative_to(root)}:{line_number}: {line.strip()}")
    return failures


def main() -> int:
    if len(sys.argv) != 2:
        print("usage: verify-release-docs.py <release-tag>", file=sys.stderr)
        return 2

    try:
        tag, version = normalize_tag(sys.argv[1])
    except ValueError as error:
        print(error, file=sys.stderr)
        return 2

    root = Path(__file__).resolve().parent.parent
    failures = find_stale_references(root, tag, version)
    if failures:
        print(f"Release documentation still describes {tag} as future or unavailable:", file=sys.stderr)
        for failure in failures:
            print(f"- {failure}", file=sys.stderr)
        print("Update roadmap and availability banners before publishing the tag.", file=sys.stderr)
        return 1

    print(f"Release documentation contains no stale future-work markers for {tag}.")
    return 0


if __name__ == "__main__":
    raise SystemExit(main())

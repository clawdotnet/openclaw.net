#!/usr/bin/env python3
"""Sign release bundles with an externally supplied RSA key; never emit the key."""
import argparse
import datetime
import hashlib
import json
from pathlib import Path
import subprocess


def main():
    parser = argparse.ArgumentParser()
    parser.add_argument('--assets', type=Path, required=True)
    parser.add_argument('--version', required=True)
    parser.add_argument('--channel', choices=['stable', 'beta'], required=True)
    parser.add_argument('--base-url', required=True)
    parser.add_argument('--key', type=Path, required=True)
    args = parser.parse_args()
    assets = []
    for path in sorted(args.assets.glob('openclaw-desktop-*.zip')):
        with path.open('rb') as stream:
            digest = hashlib.file_digest(stream, 'sha256').hexdigest()
        assets.append(dict(Rid=path.stem.removeprefix('openclaw-desktop-'), Url=args.base_url.rstrip('/')+'/'+path.name, Sha256=digest, Size=path.stat().st_size))
    if not assets:
        raise SystemExit('No desktop bundles found')
    feed = dict(SchemaVersion=1, ExpiresAtUtc=(datetime.datetime.now(datetime.timezone.utc)+datetime.timedelta(days=90)).isoformat(), Releases=[dict(Version=args.version.removeprefix('v'), Channel=args.channel, Assets=assets)])
    manifest = args.assets / 'update-manifest.json'
    manifest.write_text(json.dumps(feed, separators=(',', ':')), encoding='utf-8')
    subprocess.run(['openssl', 'dgst', '-sha256', '-sign', str(args.key), '-out', str(manifest)+'.sig', str(manifest)], check=True)


if __name__ == '__main__':
    main()

#!/usr/bin/env bash
set -euo pipefail

missing=()

if ! command -v clang >/dev/null 2>&1; then
  missing+=("clang")
fi

if ! dpkg-query -W -f='${Status}' zlib1g-dev 2>/dev/null | grep -q "install ok installed"; then
  missing+=("zlib1g-dev")
fi

if [ "${#missing[@]}" -eq 0 ]; then
  echo "NativeAOT prerequisites already installed; skipping apt."
  exit 0
fi

attempts="${APT_INSTALL_ATTEMPTS:-3}"
timeout_seconds="${APT_INSTALL_TIMEOUT_SECONDS:-120}"

for attempt in $(seq 1 "$attempts"); do
  echo "::group::Install missing NativeAOT prerequisites, attempt ${attempt}/${attempts}"
  set +e
  timeout "${timeout_seconds}s" sudo env DEBIAN_FRONTEND=noninteractive apt-get install \
    -y \
    --no-install-recommends \
    -o Acquire::Retries=2 \
    -o Dpkg::Lock::Timeout=60 \
    "${missing[@]}"
  status=$?
  set -e
  echo "::endgroup::"

  if [ "$status" -eq 0 ]; then
    exit 0
  fi

  if [ "$attempt" -eq "$attempts" ]; then
    echo "::error::Failed to install NativeAOT prerequisites after ${attempts} attempts."
    exit "$status"
  fi

  echo "::warning::apt-get install failed or timed out with exit code ${status}; retrying."
  sleep $((attempt * 10))
done

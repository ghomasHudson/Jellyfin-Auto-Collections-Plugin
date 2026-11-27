#!/usr/bin/env bash
set -euo pipefail

dir=$(cd -- "$(dirname -- "${BASH_SOURCE[0]}")" && pwd)
image=$(awk '$1 == "FROM" && $4 == "native" {print $2}' "$dir/Dockerfile")
archive=${1:?Pass the built ZIP archive}
allow_unavailable=${2:-}
root=$(mktemp -d)
trap 'rm -rf "$root"' EXIT
chmod 0755 "$root"

# Validate the ZIP and unpack it using the same explicit mode allowlist as the
# plugin. Keep the read-only mounted bundle distinct from the writable profile.
python3 -B - "$dir" "$archive" "$root" <<'PY'
import stat
import sys
from pathlib import Path
from zipfile import ZipFile

sys.path.insert(0, sys.argv[1])
from pack import EXECUTABLES

with ZipFile(sys.argv[2]) as archive:
    paths = set()
    for item in archive.infolist():
        name = item.filename
        mode = stat.S_IFREG | (0o755 if name in EXECUTABLES else 0o644)
        if (name in paths or name.startswith("/") or "\\" in name or
                {"", ".", ".."}.intersection(name.split("/")) or
                item.create_system != 3 or item.external_attr >> 16 != mode):
            raise ValueError("Unexpected helper ZIP entry")
        paths.add(name)
    if not EXECUTABLES <= paths or "render.py" not in paths:
        raise ValueError("Missing helper ZIP entrypoints")
    if "native/libXext.so.6" not in paths:
        raise ValueError("Missing bundled libXext.so.6 required by Chromium")
    archive.extractall(sys.argv[3])
    for name in EXECUTABLES:
        (Path(sys.argv[3]) / name).chmod(0o755)
print("Helper ZIP paths, modes, and file integrity verified.")
PY

unavailable() {
    if [[ "$allow_unavailable" == --allow-unavailable-sandbox ]]; then
        echo "::warning::Stock Jellyfin Chromium smoke test skipped: $1"
        exit 0
    fi
    echo "$1" >&2
    exit 1
}

if ! command -v podman >/dev/null; then
    unavailable "Podman not installed."
fi
if ! output=$(podman unshare true 2>&1); then
    echo "$output" >&2
    if [[ "$output" == *"Operation not permitted"* || "$output" == *"Permission denied"* || "$output" == *newuidmap* ]]; then
        unavailable "The runner does not permit rootless Podman user namespaces."
    fi
    exit 1
fi

podman image exists "$image" || podman pull "$image"
container=(--rm --pull=never --read-only --network none --user 1000:1000 --tmpfs '/tmp:exec,mode=1777' -w /tmp)
if ! output=$(podman run "${container[@]}" --entrypoint /usr/bin/unshare "$image" \
    --user --map-root-user --pid --net --fork /bin/true 2>&1); then
    echo "$output" >&2
    if [[ "$output" == *"Operation not permitted"* || "$output" == *"Permission denied"* ]]; then
        unavailable "The runner blocks nested user, PID, or network namespaces in stock Jellyfin."
    fi
    exit 1
fi

podman run "${container[@]}" -v "$root:/bundle:ro" -v "$dir/smoke.py:/smoke.py:ro" \
    -e HOME=/tmp -e TMPDIR=/tmp -e PYTHONUTF8=1 -e PYTHONHOME=/bundle/python \
    -e LD_LIBRARY_PATH=/bundle/native:/bundle/python/lib \
    -e PLAYWRIGHT_BROWSERS_PATH=/bundle/browsers \
    --entrypoint /bundle/python/bin/python3.12 "$image" -s -B -P /smoke.py

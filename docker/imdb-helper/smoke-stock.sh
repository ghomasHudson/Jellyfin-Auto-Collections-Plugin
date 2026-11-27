#!/usr/bin/env bash
set -euo pipefail

dir=$(cd -- "$(dirname -- "${BASH_SOURCE[0]}")" && pwd)
stock_image=$(awk '$1 == "FROM" && $4 == "native" {print $2}' "$dir/Dockerfile")
# Check both Ubuntu bases used by LinuxServer, not just the Debian build base.
linuxserver_2404=docker.io/linuxserver/jellyfin@sha256:313571f253bf1035809f6856bb063904456226ef3899b96e60ee4597b3477246
linuxserver_2604=docker.io/linuxserver/jellyfin@sha256:1c60c32d1de1529cc58ea6c7879dd47f89d0d88082b085a66214243de3150b5c
archive=${1:?Pass the built ZIP archive}
allow_unavailable=${2:-}
work=$(mktemp -d)
trap 'rm -rf "$work"' EXIT
root="$work/bundle"
mkdir -m 0755 "$root"

# Validate the ZIP and unpack it using the same explicit mode allowlist as the
# plugin. Keep the read-only mounted bundle distinct from the writable profile.
python3 -B - "$dir" "$archive" "$root" "$work/elf-paths.txt" <<'PY'
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
    for library in ("libXext.so.6", "libXrender.so.1", "libmount.so.1", "libxml2.so.2"):
        if f"native/{library}" not in paths:
            raise ValueError(f"Missing bundled runtime library {library}")
    archive.extractall(sys.argv[3])
    for name in EXECUTABLES:
        (Path(sys.argv[3]) / name).chmod(0o755)
    with Path(sys.argv[4]).open("w", encoding="utf-8") as manifest:
        for name in sorted(paths):
            with (Path(sys.argv[3]) / name).open("rb") as file:
                if file.read(4) == b"\x7fELF":
                    print(f"/bundle/{name}", file=manifest)
print("Helper ZIP paths, modes, and file integrity verified.")
PY

unavailable() {
    if [[ "$allow_unavailable" == --allow-unavailable-sandbox ]]; then
        echo "::warning::IMDb helper smoke test skipped: $1"
        return 0
    fi
    echo "$1" >&2
    exit 1
}

if ! command -v podman >/dev/null; then
    unavailable "Podman not installed."
    exit 0
fi
if ! output=$(podman unshare true 2>&1); then
    echo "$output" >&2
    if [[ "$output" == *"Operation not permitted"* || "$output" == *"Permission denied"* || "$output" == *newuidmap* ]]; then
        unavailable "The runner does not permit rootless Podman user namespaces."
        exit 0
    fi
    exit 1
fi

container=(--rm --pull=never --read-only --network none --user 1000:1000 --tmpfs '/tmp:exec,mode=1777' -w /tmp)
for image in "$stock_image" "$linuxserver_2404" "$linuxserver_2604"; do
    podman image exists "$image" || podman pull "$image"
    echo "Checking IMDb helper in $image"
    # Check the dynamic loader even if the CI runner cannot start the browser sandbox.
    podman run "${container[@]}" -v "$root:/bundle:ro" \
        -v "$work/elf-paths.txt:/elf-paths.txt:ro" \
        -e LD_LIBRARY_PATH=/bundle/native:/bundle/python/lib \
        --entrypoint /bin/sh "$image" -eu -c '
        count=0
        failed=0
        while IFS= read -r binary; do
            count=$((count + 1))
            if ! dependencies=$(ldd "$binary" 2>&1); then
                printf "%s:\n%s\n" "$binary" "$dependencies" >&2
                failed=1
            elif missing=$(printf "%s\n" "$dependencies" | grep -E "not found|error while loading"); then
                printf "%s:\n%s\n" "$binary" "$missing" >&2
                failed=1
            fi
        done </elf-paths.txt
        test "$count" -gt 0
        test "$failed" -eq 0
        printf "Shared-library dependencies resolved for all %s ELF files.\n" "$count"
    '
    if ! output=$(podman run "${container[@]}" --entrypoint /usr/bin/unshare "$image" \
        --user --map-root-user --pid --net --fork /bin/true 2>&1); then
        echo "$output" >&2
        if [[ "$output" == *"Operation not permitted"* || "$output" == *"Permission denied"* ]]; then
            unavailable "The runner blocks nested user, PID, or network namespaces in $image."
            continue
        fi
        exit 1
    fi

    podman run "${container[@]}" -v "$root:/bundle:ro" -v "$dir/smoke.py:/smoke.py:ro" \
        -e HOME=/tmp -e TMPDIR=/tmp -e PYTHONUTF8=1 -e PYTHONHOME=/bundle/python \
        -e LD_LIBRARY_PATH=/bundle/native:/bundle/python/lib \
        -e PLAYWRIGHT_BROWSERS_PATH=/bundle/browsers \
        --entrypoint /bundle/python/bin/python3.12 "$image" -s -B -P /smoke.py
done

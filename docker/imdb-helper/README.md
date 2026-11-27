# IMDb helper release asset

Build from the repository root with Docker + BuildKit on Linux x64:

```sh
docker build --platform linux/amd64 --target archive \
  --output type=local,dest=/tmp/imdb-helper docker/imdb-helper
sha256sum /tmp/imdb-helper/magic-collections-imdb-linux-x64.zip
```

Podman accepts `--output /tmp/imdb-helper` instead. Only the build stages use
package managers. The ZIP archive contains only regular files, with a fixed
entry order and timestamp. The build verifies input digests and uses a dated,
signed Debian snapshot. Review security updates and re-run the smoke test
whenever any pinned input changes.

## Runtime contract

The target is stock Jellyfin 10.11.5 on Linux x64 (Debian 13). The helper uses
the system loader and libraries already present in that image. Run as a
non-root user, with writable private temporary storage and a container/kernel
that permits the Chromium namespace sandbox. There is no unsandboxed fallback.

When Jellyfin itself runs as root, the plugin uses UID/GID 65534 for the
helper. That user needs execute (traverse) access on every parent directory
of the installed helper. If the data mount is private to root, run Jellyfin
as a non-root user with access to its data instead. A `noexec` data mount
also prevents the bundled helper from starting.

The bundle includes `libXext.so.6` because some images, including LinuxServer's
Jellyfin image, omit it. The official image is the tested target; other images
may still lack libraries that the helper expects from the runtime.

The archive records `0755` for these three files and `0644` for other files.
The plugin applies read-only modes after extraction: `0555` for these three
files and `0444` for other files. It also sets `LD_LIBRARY_PATH` to
`ROOT/native:ROOT/python/lib`, `PYTHONHOME` to `ROOT/python`, and
`PLAYWRIGHT_BROWSERS_PATH` to `ROOT/browsers`.

```
python/bin/python3.12
python/lib/python3.12/site-packages/playwright/driver/node
browsers/chromium_headless_shell-1255/chrome-headless-shell-linux64/chrome-headless-shell
```

Invoke `python/bin/python3.12` with the extracted `render.py` and one canonical
HTTPS `www.imdb.com` URL. The supported routes are public lists with an
optional `?page=N`, `/chart/top/`, and `/chart/toptv/`. A successful read
returns one UTF-8 JSON object with `finalUrl`, the last main-document HTTP
`status`, and `pageData`. The latter holds the unmodified text of the unique
`script#__NEXT_DATA__`. A non-200 document on the requested URL
returns the same envelope with empty `pageData` and a nonzero exit. Other
failures exit nonzero with no stdout and a static stderr message. The helper
does not emit raw HTML or use PyMovieDb.

## Offline smoke test

From the repository root with rootless Podman:

```sh
bash docker/imdb-helper/smoke-stock.sh /tmp/imdb-helper/magic-collections-imdb-linux-x64.zip
```

The script checks the archive's paths and file modes before extracting it.
Then it mounts the bundle read-only in stock Jellyfin and runs as UID 1000
with networking disabled. The browser test verifies JavaScript execution,
renderer namespace and seccomp isolation, same-URL document replacement, and
exact script extraction. It uses local fixtures rather than live IMDb pages.
On CI, the script warns and skips the browser test if the runner's default
sandbox policy blocks the required namespaces. It does not change that policy.

## Licenses

This directory contains the source for the GPL-3.0 helper glue (see the
repository's `LICENSE`). The bundle retains CPython's license, wheel
`*.dist-info` licenses, Playwright's driver notices (including Node), Chrome
Headless Shell's license, and the Debian library notices and source package
versions under `licenses/`. The exact third-party inputs and the local
`urlrender` changes are recorded in `Dockerfile`, `collect-native.sh`,
`requirements.txt`, and `prepare.py`.

"""Write the deterministic release ZIP, containing only plain files."""

import shutil
import stat
import sys
from pathlib import Path
from zipfile import ZIP_DEFLATED, ZipFile, ZipInfo


# Keep this list in sync with the plugin's explicit permission restore.
EXECUTABLES = {
    "python/bin/python3.12",
    "python/lib/python3.12/site-packages/playwright/driver/node",
    "browsers/chromium_headless_shell-1255/chrome-headless-shell-linux64/chrome-headless-shell",
}


def pack(root: Path, dest: Path) -> None:
    paths = []
    for path in sorted(root.rglob("*")):
        mode = path.lstat().st_mode
        if stat.S_ISDIR(mode):
            continue
        if not stat.S_ISREG(mode):
            raise ValueError(f"Unexpected non-regular file: {path}")
        paths.append(path.relative_to(root).as_posix())
    if not EXECUTABLES.issubset(paths):
        raise ValueError("Missing helper executables")

    # Normalize timestamps, creator platform, permissions, and ordering.
    # For Python 3.12, ZipInfo.open writes with zlib's default compression level.
    with ZipFile(dest, "w", compression=ZIP_DEFLATED, allowZip64=False) as zip_out:
        for relative in paths:
            info = ZipInfo(relative, (1980, 1, 1, 0, 0, 0))
            info.create_system = 3  # Unix
            info.external_attr = (stat.S_IFREG | (0o755 if relative in EXECUTABLES else 0o644)) << 16
            info.compress_type = ZIP_DEFLATED
            info.file_size = (root / relative).stat().st_size
            with (root / relative).open("rb") as source, zip_out.open(info, "w") as output:
                shutil.copyfileobj(source, output)


if __name__ == "__main__":
    pack(Path(sys.argv[1]), Path(sys.argv[2]))

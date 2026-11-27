#!/bin/sh
set -eu
export LC_ALL=C

# Use the signed package indexes of a fixed Debian archive, not the moving distro mirrors.
snapshot=20261127T000000Z
rm -f /etc/apt/sources.list /etc/apt/sources.list.d/*
cat >/etc/apt/sources.list.d/helper.sources <<EOF
Types: deb
URIs: https://snapshot.debian.org/archive/debian/$snapshot/
Suites: trixie trixie-updates
Components: main
Signed-By: /usr/share/keyrings/debian-archive-keyring.pgp
Check-Valid-Until: no

Types: deb
URIs: https://snapshot.debian.org/archive/debian-security/$snapshot/
Suites: trixie-security
Components: main
Signed-By: /usr/share/keyrings/debian-archive-keyring.pgp
Check-Valid-Until: no
EOF

dpkg-query -W -f='${Package} ${Version}\n' | sort > /tmp/base-packages
apt-get -o Acquire::Retries=3 update
apt-get -o Acquire::Retries=3 install -y --no-install-recommends \
    libatomic1 libglib2.0-0t64 libnss3 libnspr4 libatk-bridge2.0-0t64 \
    libdbus-1-3 libxcomposite1 libxdamage1 libxfixes3 libxrandr2 \
    libgbm1 libxkbcommon0 libasound2t64
dpkg-query -W -f='${Package} ${Version}\n' | sort > /tmp/with-packages

# Reuse libraries already available in the pinned runtime; never ship glibc or ld-linux.
# This is intentionally based on versions as well as package names.
mkdir -p /bundle/native /bundle/licenses/debian
comm -13 /tmp/base-packages /tmp/with-packages > /tmp/added-packages
cat > /bundle/licenses/debian/sources.txt <<EOF
Unmodified Debian 13 packages; see their copyright files and corresponding source packages:
https://snapshot.debian.org/archive/debian/$snapshot/
https://snapshot.debian.org/archive/debian-security/$snapshot/

Binary package | version | source package | source version
EOF
while read -r pkg _; do
    case "$pkg" in
        libc6*|libc-bin) echo "Unexpected glibc upgrade in the helper build." >&2; exit 1 ;;
    esac
    dpkg-query -W -f='${Package} | ${Version} | ${source:Package} | ${source:Version}\n' "$pkg" \
        >> /bundle/licenses/debian/sources.txt
    cp -L "/usr/share/doc/$pkg/copyright" "/bundle/licenses/debian/$pkg.txt"
    dpkg-query -L "$pkg" > /tmp/package-files
    while IFS= read -r path; do
        case "$path" in
            # The LLVM-19 compatibility alias is not used by the runtime.
            /usr/lib/x86_64-linux-gnu/libLLVM-19.so) ;;
            /usr/lib/x86_64-linux-gnu/*.so|/usr/lib/x86_64-linux-gnu/*.so.*)
                # Copy loader names as regular files, not symlinks. Keep the full
                # name only if it has no loader symlink pointing to it.
                name=${path##*/}
                case "$name" in
                    *.so.*.*)
                        stem=${path%%.so.*}
                        major=${name#*.so.}
                        soname=$stem.so.${major%%.*}
                        if [ -L "$soname" ] && [ "$path" -ef "$soname" ]; then
                            continue
                        fi ;;
                esac
                if [ -f "$path" ]; then cp -L "$path" /bundle/native/; fi ;;
        esac
    done < /tmp/package-files
done < /tmp/added-packages
cp -rL /usr/share/common-licenses /bundle/licenses/debian/

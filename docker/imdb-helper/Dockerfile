# Linux amd64 manifests for the stock Jellyfin runtime and official Python 3.12.16.
FROM docker.io/jellyfin/jellyfin:10.11.5@sha256:6c78d98d1a5d7a2516a4cc2cbfdbd7de7b14426437cad4fc899f319081f2c6d8 AS native
USER root
COPY collect-native.sh /tmp/
RUN sh /tmp/collect-native.sh

FROM docker.io/library/python:3.12-slim-bookworm@sha256:0f086103b026048cc50cb5da25ec435c023c5241a83fc44daea593377abc61c4 AS bundle
ENV PYTHONDONTWRITEBYTECODE=1 PIP_DISABLE_PIP_VERSION_CHECK=1
WORKDIR /build
COPY requirements.txt prepare.py /build/
RUN set -eu; \
    test "$(uname -m)" = x86_64; \
    mkdir -p /bundle/python/bin /bundle/python/lib /bundle/licenses /out; \
    cp /usr/local/bin/python3.12 /bundle/python/bin/; \
    cp /usr/local/lib/libpython3.12.so.1.0 /bundle/python/lib/; \
    cp -a /usr/local/lib/python3.12 /bundle/python/lib/; \
    python -m pip --isolated --disable-pip-version-check install --require-hashes --only-binary=:all: --no-cache-dir \
        --root-user-action=ignore --no-compile --target /bundle/python/lib/python3.12/site-packages -r requirements.txt; \
    python prepare.py /bundle
COPY --from=native /bundle/ /bundle/
COPY render.py /bundle/
COPY pack.py /build/
RUN python pack.py /bundle /out/magic-collections-imdb-linux-x64.zip

FROM scratch AS archive
COPY --from=bundle /out/magic-collections-imdb-linux-x64.zip /

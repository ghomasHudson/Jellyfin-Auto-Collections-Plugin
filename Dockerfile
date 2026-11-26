FROM mcr.microsoft.com/dotnet/sdk:9.0 AS build
WORKDIR /src

COPY Jellyfin.Plugin.MagicCollections/Jellyfin.Plugin.MagicCollections.csproj Jellyfin.Plugin.MagicCollections/
RUN dotnet restore Jellyfin.Plugin.MagicCollections
COPY Jellyfin.Plugin.MagicCollections/ Jellyfin.Plugin.MagicCollections/
RUN dotnet build Jellyfin.Plugin.MagicCollections -c Release --no-restore -o /out

FROM docker.io/jellyfin/jellyfin:10.11.5
COPY --from=build /out/Jellyfin.Plugin.MagicCollections.dll /opt/magiccollections/
COPY docker/start-jellyfin.sh /opt/magiccollections/

# Zero-byte movie placeholders for testing library scans, not playback.
RUN for movie in \
        "The Matrix (1999) [tmdbid-603]" \
        "Inception (2010) [tmdbid-27205]" \
        "Tenet (2020) [tmdbid-577922]"; do \
        mkdir -p "/test-media/$movie" && \
        touch "/test-media/$movie/$movie.mp4" || exit 1; \
    done

ENTRYPOINT ["/bin/sh", "/opt/magiccollections/start-jellyfin.sh"]

FROM mcr.microsoft.com/dotnet/sdk:9.0 AS build
WORKDIR /src

COPY Jellyfin.Plugin.AutoCollections/Jellyfin.Plugin.AutoCollections.csproj Jellyfin.Plugin.AutoCollections/
RUN dotnet restore Jellyfin.Plugin.AutoCollections
COPY Jellyfin.Plugin.AutoCollections/ Jellyfin.Plugin.AutoCollections/
RUN dotnet build Jellyfin.Plugin.AutoCollections -c Release --no-restore -o /out

FROM docker.io/jellyfin/jellyfin:10.11.5
COPY --from=build /out/Jellyfin.Plugin.AutoCollections.dll /opt/autocollections/
COPY docker/start-jellyfin.sh /opt/autocollections/
ENTRYPOINT ["/bin/sh", "/opt/autocollections/start-jellyfin.sh"]

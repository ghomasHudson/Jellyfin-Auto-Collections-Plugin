#!/bin/sh
set -eu

# Refresh the DLL even when /config is an existing volume.
plugin_dir="$JELLYFIN_DATA_DIR/plugins/AutoCollections"
mkdir -p "$plugin_dir"
cp /opt/autocollections/Jellyfin.Plugin.AutoCollections.dll "$plugin_dir/"

exec /jellyfin/jellyfin "$@"

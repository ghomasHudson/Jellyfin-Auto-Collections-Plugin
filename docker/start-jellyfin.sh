#!/bin/sh
set -eu

# Refresh the DLL even when /config is an existing volume.
plugin_dir="$JELLYFIN_DATA_DIR/plugins/MagicCollections"
mkdir -p "$plugin_dir"
cp /opt/magiccollections/Jellyfin.Plugin.MagicCollections.dll "$plugin_dir/"

# Remove the DLL installed by older sandbox images, but keep the user's settings.
rm -f "$JELLYFIN_DATA_DIR/plugins/AutoCollections/Jellyfin.Plugin.AutoCollections.dll"

exec /jellyfin/jellyfin "$@"

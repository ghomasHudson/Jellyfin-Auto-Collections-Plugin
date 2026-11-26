#!/bin/sh
set -eu

# Refresh the DLL even when /config is an existing volume.
plugin_dir="$JELLYFIN_DATA_DIR/plugins/MagicCollections"
mkdir -p "$plugin_dir"
cp /opt/magiccollections/Jellyfin.Plugin.MagicCollections.dll "$plugin_dir/"

# Remove the DLL installed by older sandbox images, but keep the user's settings.
rm -f "$JELLYFIN_DATA_DIR/plugins/AutoCollections/Jellyfin.Plugin.AutoCollections.dll"

seed_test_library() {
    # Jellyfin 10.11 stores library types and paths under root/default.
    libraries_dir="$JELLYFIN_DATA_DIR/root/default"
    for shortcut in "$libraries_dir"/*/*.mblink; do
        if [ -f "$shortcut" ] && grep -Fxq -e /test-media -e /test-media/ "$shortcut"; then
            return
        fi
    done

    library_base="$libraries_dir/Magic Collections Test Movies"
    library_dir="$library_base"
    suffix=2
    while [ -e "$library_dir" ]; do
        library_dir="$library_base $suffix"
        suffix=$((suffix + 1))
    done
    mkdir -p "$library_dir"
    printf '%s' /test-media > "$library_dir/test-media.mblink"
    touch "$library_dir/movies.collection"
}

# Seed once per data volume so a user can rename or remove the library.
seed_marker="$JELLYFIN_DATA_DIR/.magiccollections-test-library-seeded"
if [ ! -f "$seed_marker" ]; then
    seed_test_library
    touch "$seed_marker"
fi

jellyfin_api() {
    curl --noproxy '*' --fail --silent --show-error --output /dev/null --max-time 10 "$@"
}

finish_setup() {
    startup_url=http://127.0.0.1:8096/Startup

    # GET /Startup/User waits for Jellyfin and initializes the first user.
    jellyfin_api --retry 60 --retry-all-errors --retry-delay 1 --retry-max-time 60 "$startup_url/User" 2>/dev/null &&
        jellyfin_api --json '{"UICulture":"en-US","MetadataCountryCode":"US","PreferredMetadataLanguage":"en"}' "$startup_url/Configuration" &&
        jellyfin_api --json '{"Name":"admin","Password":"magiccollections-test"}' "$startup_url/User" &&
        jellyfin_api --request POST "$startup_url/Complete"
}

# Only a fresh sandbox gets the default login. Leave existing accounts alone.
if [ ! -e "$JELLYFIN_CONFIG_DIR/system.xml" ] && [ ! -e "$JELLYFIN_DATA_DIR/data/jellyfin.db" ]; then
    (finish_setup || echo "Automatic setup failed. Complete the Jellyfin setup wizard manually." >&2) &
fi

exec /jellyfin/jellyfin "$@"

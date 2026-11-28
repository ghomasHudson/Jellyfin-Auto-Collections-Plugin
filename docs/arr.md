# Radarr and Sonarr tag collections

Magic Collections can mirror the movies assigned to a Radarr tag or the whole TV series assigned to a Sonarr tag. It only includes titles already in your Jellyfin library. It doesn't download media, modify either server, or combine tags from multiple servers into one collection.

## Add a server

1. In Radarr or Sonarr, copy the API key from **Settings → General → Security**.
2. In Jellyfin, open **Dashboard → Plugins → Magic Collections** and expand **\*arr**.
3. Under **Radarr** or **Sonarr**, enter your server's **Base URL** and **API key** in the same row. Include the port and reverse proxy path if your server uses them. To add another instance, select **Add Radarr server** or **Add Sonarr server**. Each server must have a distinct base URL, including across Radarr and Sonarr.

## Set the source URL

1. Under **Source list URLs**, add the base URL followed by `/` and the exact tag label. URL-encode the label if it contains spaces or other special characters. For example:

   - If Radarr is at `https://radarr.example.com`, use `https://radarr.example.com/family-movies` for the `family-movies` tag.
   - If Sonarr is at `https://sonarr.example.com`, use `https://sonarr.example.com/family-shows` for the `family-shows` tag.
   - If your reverse proxy serves Sonarr at `https://media.example.com/sonarr`, use `https://media.example.com/sonarr/family-shows` for that same tag.

2. Select **Save**. To sync now, run **Sync Magic Collections** from **Scheduled Tasks**.

The source list URL identifies the tag to Magic Collections. It doesn't have to open a tag page in your browser. Use the same scheme, host, port, and case-sensitive base path as your configured server. Don't put a username, password, API key, query string, or fragment in either URL. API keys are stored in the Jellyfin plugin's admin settings; protect your Jellyfin configuration and backups. Use HTTPS where possible.

## Supported versions

Magic Collections uses API v3 with Radarr 3.x through 7.x and Sonarr 3.x and 4.x. Radarr collections match movies by IMDb or TMDb ID. Sonarr collections match whole series by TVDb, IMDb, or TMDb ID. Older Sonarr versions don't return TMDb IDs; a TVDb ID alone is enough.

If a tag has no matching titles, sync can empty its managed collection. Sync never deletes media. If the tag no longer exists or the plugin can't read the list, the collection stays unchanged.

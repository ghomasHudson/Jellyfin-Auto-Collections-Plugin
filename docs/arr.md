# Radarr tag collections

Magic Collections can mirror the movies assigned to a tag on one Radarr server. It matches movies already in your Jellyfin library by IMDb or TMDb ID. It doesn't download movies, modify Radarr, or combine tags from multiple servers into one collection.

## Add a Radarr tag

1. In Radarr, copy the API key from **Settings → General → Security**.
2. In Jellyfin, open **Dashboard → Plugins → Magic Collections** and expand **\*arr**.
3. Under **Radarr**, enter your server's **Base URL** and **API key** in the same row. Include the port and the path Radarr uses behind your reverse proxy, if any. Use **Add Radarr server** for another instance or **Remove** to clear a pair. Each server must have a distinct base URL, including across Radarr and Sonarr.

## Set the source URL

1. Under **Source list URLs**, add the base URL followed by `/` and the exact Radarr tag label. URL-encode the label so it takes up one path segment. For a tag called `family-movies`, use `https://radarr.example.com/family-movies`. If the base URL is `https://media.example.com/radarr`, use `https://media.example.com/radarr/family-movies` instead.
2. Select **Save**. To sync now, run **Sync Magic Collections** from **Scheduled Tasks**.

The source list URL identifies the tag to Magic Collections. It doesn't have to open a tag page in your browser. Use the same scheme, host, port, and case-sensitive base path as your configured server. Don't put a username, password, API key, query string, or fragment in either URL. API keys are stored in the Jellyfin plugin's admin settings; protect your Jellyfin configuration and backups. Use HTTPS where possible.

The plugin reads tags and movies through Radarr's API v3, available in Radarr app versions 3–7. If a tag has no matching movies, sync can empty its managed collection. Sync never deletes media. If the tag no longer exists or the plugin can't read the list, the collection stays unchanged.

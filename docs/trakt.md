# Trakt public lists and charts

Open **Dashboard → Plugins → Magic Collections**. Paste a supported Trakt URL under **Source list URLs**, select **Save**, then run **Sync Magic Collections** under **Scheduled Tasks**. The plugin uses its own Trakt Client ID, so you don't need to sign in or provide a Client Secret, access token, or token file. The plugin can't read private lists or watchlists.

For public user lists, use `https://app.trakt.tv/users/<user>/lists/<slug>`. Older `https://trakt.tv/users/<user>/lists/<slug>` links also work.

The plugin supports the unfiltered [Popular movies](https://app.trakt.tv/discover/popular?mode=movie), [Popular shows](https://app.trakt.tv/discover/popular?mode=show), [Trending movies](https://app.trakt.tv/discover/trending?mode=movie), and [Trending shows](https://app.trakt.tv/discover/trending?mode=show) charts. Older chart URLs on `trakt.tv` still work: `/movies/popular`, `/shows/popular`, `/movies/trending`, and `/shows/trending`.

Try [Open Your Eyes](https://app.trakt.tv/users/justin/lists/open-your-eyes) for a mixed list. The plugin reads every page of a public user list, and up to the first 100 ranked entries of each chart. It matches only movies and whole series already in your Jellyfin library. If a request fails, including a rate limit or an error on a later page, the plugin leaves that collection unchanged for that run. Check the Jellyfin log and wait before retrying.

### Optional: use your own Client ID

Leave the override blank to use the included Client ID. For development or a personal setup that needs its own ID, sign in to the [Trakt developer portal](https://developer.trakt.tv/apps). [Link a verified GitHub account](https://developer.trakt.tv/docs/create-an-app), create a Standard app, and copy its **Client ID** into the **Trakt** panel in Magic Collections. A personal app can stay on Development. Keep the ID out of source URLs; the plugin sends it to the official Standard API only.

Magic Collections uses Trakt but is not endorsed or certified by Trakt.

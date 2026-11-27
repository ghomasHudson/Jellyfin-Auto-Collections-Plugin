# Trakt lists and charts

You can import public lists and charts without a Trakt account.

In **Dashboard → Plugins → Magic Collections**, add the public Trakt link under **Source list URLs** and save your settings. Jellyfin will import the list at the next sync. You can also run **Sync Magic Collections** from **Scheduled Tasks**.

For public user lists, use `https://app.trakt.tv/users/<user>/lists/<slug>`. Older `https://trakt.tv/users/<user>/lists/<slug>` links also work.

The plugin supports the unfiltered [Popular movies](https://app.trakt.tv/discover/popular?mode=movie), [Popular shows](https://app.trakt.tv/discover/popular?mode=show), [Trending movies](https://app.trakt.tv/discover/trending?mode=movie), and [Trending shows](https://app.trakt.tv/discover/trending?mode=show) charts. Older chart URLs on `trakt.tv` still work: `/movies/popular`, `/shows/popular`, `/movies/trending`, and `/shows/trending`.

Try [Open Your Eyes](https://app.trakt.tv/users/justin/lists/open-your-eyes) for a mixed list. The plugin reads every page of a public user list, and up to the first 100 ranked entries of each chart. It matches only movies and whole series already in your Jellyfin library. If a request fails, including a rate limit or an error on a later page, the plugin leaves that collection unchanged for that run. Check the Jellyfin log and wait before retrying.

### Optional: use your own Client ID

Leave the override blank to use the included Client ID. For development or a personal setup that needs its own ID, sign in to the [Trakt developer portal](https://developer.trakt.tv/apps). [Link a verified GitHub account](https://developer.trakt.tv/docs/create-an-app), create a Standard app, and copy its **Client ID** into the **Trakt** panel in Magic Collections. A personal app can stay on Development. Keep the ID out of source URLs.

Magic Collections uses Trakt but is not endorsed or certified by Trakt.

## Import a private Trakt list

1. Create a Trakt Development app in the [developer portal](https://developer.trakt.tv/apps). Register `https://ghomashudson.github.io/Jellyfin-Magic-Collections-Plugin/trakt/callback.html` as its redirect URI. You do not need a Client Secret.
2. In Magic Collections, expand **Trakt**. Paste the app’s **Client ID** into the override field and select **Save**.
3. Select **Connect Trakt**, open the sign-in link, and approve access. On the callback page, select **Copy response**. Paste the response into the same Jellyfin dashboard and select **Finish connecting**.
4. Check the connected account name. Enter one personal list URL per line under **Private Trakt list URLs to share in Jellyfin** and select **Save**. Then run **Sync Magic Collections**.

Jellyfin uses one connected account for private lists. The links must belong to that account. Watchlists and smart lists are not part of this setup.

**The resulting Jellyfin collections are shared.** People with access to your collections may see the imported list names, descriptions, and matching titles. Disconnecting stops future reads but leaves existing collections in place.

If you move a list from public to private, the first successful private sync removes the public URL tag from the existing collection. This doesn't erase the URL from old logs or backups.

The callback uses HTTPS even if your dashboard doesn’t. Only use an HTTP Jellyfin dashboard on a trusted network. Jellyfin stores the tokens on its own data volume, so protect access to that volume and its backups.

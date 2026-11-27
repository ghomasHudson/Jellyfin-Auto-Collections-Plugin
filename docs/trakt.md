# Trakt lists and charts

You can import public lists and charts without a Trakt account.

In **Dashboard → Plugins → Magic Collections**, add the public Trakt link under **Source list URLs** and save your settings. Jellyfin will import the list at the next sync. You can also run **Sync Magic Collections** from **Scheduled Tasks**.

For public user lists, use `https://app.trakt.tv/users/<user>/lists/<slug>`. Older `https://trakt.tv/users/<user>/lists/<slug>` links also work.

The plugin supports the unfiltered [Popular movies](https://app.trakt.tv/discover/popular?mode=movie), [Popular shows](https://app.trakt.tv/discover/popular?mode=show), [Trending movies](https://app.trakt.tv/discover/trending?mode=movie), and [Trending shows](https://app.trakt.tv/discover/trending?mode=show) charts. Older chart URLs on `trakt.tv` still work: `/movies/popular`, `/shows/popular`, `/movies/trending`, and `/shows/trending`.

Try [Open Your Eyes](https://app.trakt.tv/users/justin/lists/open-your-eyes) for a mixed list. The plugin reads every page of a public user list, and up to the first 100 ranked entries of each chart. It matches only movies and whole series already in your Jellyfin library. If a request fails, including a rate limit or an error on a later page, the plugin leaves that collection unchanged for that run. Check the Jellyfin log and wait before retrying.

### Optional: use your own Client ID

Leave the override blank to use the included Client ID for public reads and Trakt sign-in. If you want to use your own app, sign in to the [Trakt developer portal](https://developer.trakt.tv/apps). [Link a verified GitHub account](https://developer.trakt.tv/docs/create-an-app) and create a Standard app. To use it for sign-in, enable sign-in and register `https://ghomashudson.github.io/Jellyfin-Magic-Collections-Plugin/trakt/callback.html` as its redirect URI. In Magic Collections, expand **Advanced**, paste the app’s **Client ID** into the override field, and select **Save**. You don’t need a Client Secret.

Magic Collections uses Trakt but is not endorsed or certified by Trakt.

## Import a private Trakt list

1. In Magic Collections, expand **Trakt** and select **Connect Trakt**. You don’t need to set a Client ID.
2. Open the sign-in link and approve access. On the callback page, select **Copy response**. Paste the response into the same Jellyfin dashboard and select **Finish connecting**.
3. Check the connected account name. Add your personal list links under **Source list URLs**, select **Save**, and run **Sync Magic Collections**.

Public lists still work without sign-in. If Trakt denies access to a personal list, Jellyfin checks who owns it. Jellyfin only retries with the connected account if it owns the list.

Magic Collections doesn't import Trakt watchlists or smart lists.

**The resulting Jellyfin collections are shared.** People with access to your collections may see the imported list names, descriptions, and matching titles. Disconnecting stops future reads but leaves existing collections in place.

If you make a Trakt list private, the first successful private sync removes the public URL tag from the existing collection. This doesn't erase the URL from old logs or backups.

The callback uses HTTPS even if your dashboard doesn’t. Only use an HTTP Jellyfin dashboard on a trusted network. Jellyfin stores the tokens on its own data volume, so protect access to that volume and its backups.

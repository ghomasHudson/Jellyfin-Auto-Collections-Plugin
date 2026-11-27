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

**The resulting Jellyfin collections are shared.** People with access to your collections may see the imported list names, descriptions, and matching titles. Disconnecting stops future reads but leaves existing collections in place.

If you make a Trakt list private, the first successful private sync removes the public URL tag from the existing collection. This doesn't erase the URL from old logs or backups.

The callback uses HTTPS even if your dashboard doesn’t. Only use an HTTP Jellyfin dashboard on a trusted network. Jellyfin stores the tokens on its own data volume, so protect access to that volume and its backups.

## Import your Trakt watchlist

Connect Trakt, then add `https://app.trakt.tv/users/me/watchlist` under **Source list URLs**. Select **Save** and run **Sync Magic Collections**. The plugin adds matching movies and whole series to a shared **Trakt Watchlist** collection. It does not remove titles that leave your watchlist.

The link always uses the connected Trakt account. If you switch accounts, the plugin keeps the previous collection separate. Disconnecting stops future watchlist reads.

## Import Trakt recommendations

Connect Trakt and paste a **Discover → Recommended** link under **Source list URLs**. Choose a link for movies, shows, or both. Magic Collections preserves `ratings`, `runtimes`, and `genres` filters, as in [this filtered movie and show link](https://app.trakt.tv/discover/recommended?mode=media&ratings=78-100&runtimes=80-500&genres=action). Links with other filters aren't supported.

The plugin reads up to 100 personalized recommendations for the connected account and adds matching titles to a shared collection. Different filters and different Trakt accounts use separate collections. Sync doesn't remove titles that are no longer recommended.

## Import a public Trakt smart list

Paste a public smart list link such as [Hidden Gems](https://app.trakt.tv/lists/smart/view/hidden-gems-6ae39104c63e248a) under **Source list URLs**. The plugin uses the list's name and checks every page for matching movies and shows. You don't need to connect Trakt. Private smart lists aren't supported.

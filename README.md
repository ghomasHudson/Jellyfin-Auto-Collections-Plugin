<img height="400" alt="image" src="https://github.com/user-attachments/assets/06681ad8-0ca4-4b78-b423-9fa34639a2ff" />

<img width="812" height="383" alt="image" src="https://github.com/user-attachments/assets/4849512f-e9a5-4a41-89c2-ad1a49f0f922" />

WIP attempt to take the lessons learned from [Jellyfin-Auto-Collections](https://github.com/ghomasHudson/Jellyfin-Auto-Collections) and turn it into a native jellyfin plugin.

## Install

1. Open your Jellyfin Web interface.
2. Go to **Dashboard &rarr; Plugins &rarr; Repositories**.
3. Click the **➕ Add** button and enter:
   - **Repository Name:** `Magic Collections`
   - **Repository URL:**
     ```text
     https://raw.githubusercontent.com/ghomasHudson/Jellyfin-Magic-Collections-Plugin/main/manifest.json
     ```
4. Click **Save**.
5. Go to the **Catalog** tab, locate **Magic Collections**, and click **Install**.
6. Restart your Jellyfin server.

## Supported list sources

Here are the supported sites and example URLs:

- MDBList
  - <https://mdblist.com/lists/garycrawfordgc/top-movies>
  - <https://mdblist.com/lists/official/movies/popular>
- Steven Lu’s Popular Movies
  - <https://popular-movies-data.stevenlu.com/movies.json>
  - <https://popular-movies-data.stevenlu.com/all-movies.json>
  - <https://popular-movies-data.stevenlu.com/movies-metacritic-min70.json>
  - <https://popular-movies-data.stevenlu.com/movies-imdb-min7.json>
  - <https://popular-movies-data.stevenlu.com/movies-rottentomatoes-min80.json>
- Letterboxd
  - <https://letterboxd.com/official/list/letterboxds-top-500-films/>

### MDBList

Use the URL from your browser, not a `/json` export URL. The plugin supports public user lists and official movie and show lists, for example:

- <https://mdblist.com/lists/garycrawfordgc/top-movies>
- <https://mdblist.com/lists/official/movies/popular>

The regular `streaming-charts` list works. The interactive JustWatch charts (`justwatch-streaming-charts`) do not, because their JSON export ignores the chart filters.

### Letterboxd

Use a public custom list URL in the form `https://letterboxd.com/<member>/list/<slug>/`, including the trailing slash and without a query string or fragment. For example, <https://letterboxd.com/jf_auto_collect/list/test_list/>. Private lists, watchlists, liked films, short links, and filtered or sorted views are not supported.

The plugin reads the public list and film pages without signing in or calling an API. A film page must link to a TMDb movie or series to establish its media type. Jellyfin can then match by TMDb or IMDb ID, never by title alone. If a page is blocked or incomplete, the plugin leaves the managed collection unchanged.

### Not yet supported

Follow these issues for the status of other sources from Jellyfin Auto Collections:

- [BFI film lists](https://github.com/ghomasHudson/Jellyfin-Magic-Collections-Plugin/issues/16)
- [Criterion Channel](https://github.com/ghomasHudson/Jellyfin-Magic-Collections-Plugin/issues/18)
- [IMDb lists and charts](https://github.com/ghomasHudson/Jellyfin-Magic-Collections-Plugin/issues/13)
- [Jellyfin library queries](https://github.com/ghomasHudson/Jellyfin-Magic-Collections-Plugin/issues/21)
- [ListMania](https://github.com/ghomasHudson/Jellyfin-Magic-Collections-Plugin/issues/12)
- [Radarr tags](https://github.com/ghomasHudson/Jellyfin-Magic-Collections-Plugin/issues/19)
- [Sonarr tags](https://github.com/ghomasHudson/Jellyfin-Magic-Collections-Plugin/issues/20)
- [They Shoot Pictures, Don’t They? (TSPDT)](https://github.com/ghomasHudson/Jellyfin-Magic-Collections-Plugin/issues/17)
- [Trakt lists and charts](https://github.com/ghomasHudson/Jellyfin-Magic-Collections-Plugin/issues/15)

## Configure and run

1. Open **Dashboard → Plugins → Magic Collections**. Paste a [supported list URL](#supported-list-sources) into the blank field. To add more URLs, use the next blank field that appears. Select **Save**.
2. Open **Dashboard → Scheduled Tasks → Sync Magic Collections**. The default trigger is daily at 03:00 server time. Select **Run now** to run the same task immediately.
3. If the task fails, check the task result or Jellyfin server log.

A failed fetch or invalid list response stops the run and reports the source list URL without changing its managed collection. Unsupported URLs are logged and skipped. A valid empty source list is not a fetch error.

Sync gives managed collections without a poster a black poster with the collection name in white. The text uses the server's installed fonts. Existing posters stay as they are after a rename. To use the new name, delete the poster and sync again.

# Developers

## Build and install manually

The plugin targets Jellyfin Server **10.11.5** and uses its SkiaSharp and HarfBuzz libraries. Correct ordering for mixed left-to-right and right-to-left titles requires the operating system's International Components for Unicode (ICU) library, included in the official Jellyfin container.

Install the **.NET 9 SDK**, version 9.0.200 or later in the 9.0 series. On Linux, the poster tests need ICU, Fontconfig, and fonts with Latin, Arabic, and Japanese glyphs (for example, `fonts-dejavu-core` and `fonts-noto-cjk` on Ubuntu). Run this command from the repository root:

```sh
dotnet test MagicCollections.slnx -c Release
```

This builds the plugin and runs the tests. Stop Jellyfin and create a `MagicCollections` directory under the server’s `plugins` directory. Copy `Jellyfin.Plugin.MagicCollections.dll` and `AngleSharp.dll` from `Jellyfin.Plugin.MagicCollections/bin/Release/net9.0/` and `LICENSE.AngleSharp` from the repository root into that directory, then start Jellyfin again.

## Contributing

Use [Commitizen](https://commitizen-tools.github.io/commitizen/) to write Conventional Commits. Install it with [pipx](https://pipx.pypa.io/), then use `cz commit` instead of `git commit` after staging your changes:

```sh
pipx install 'commitizen==4.20.1'  # one-time setup
git add <files>
cz commit
```

For example: `fix(sync): skip duplicate collection entries`.

GitHub Actions builds and tests every pull request and every push to `main`. It also checks the commits introduced by each pull request without checking the base branch's existing history. For a branch targeting `main`, preview the commit check locally:

```sh
git fetch origin
cz check --rev-range origin/main..HEAD
```

If you squash-merge a pull request, use a Conventional Commit subject for the resulting commit too.

### Publish a release

In GitHub, you can start a release from **Actions → Release plugin → Run workflow**. Run it on `main`. Commitizen reads Conventional Commits to choose the next version when you select `auto`. If you’re publishing the first release and have no eligible commits yet, select `patch`.

The workflow runs `cz bump` to update the project version and `CHANGELOG.md` and create the release commit and tag. For example, Commitizen version `0.1.1` produces the Jellyfin assembly version `0.1.1.0` and tag `v0.1.1.0`. You can preview the changelog with `cz changelog --dry-run`.

You can also prepare the release locally. Start with a clean working tree on `main`:

```sh
git switch main
git pull --ff-only --tags
cz bump --check-consistency
git push --atomic origin main "refs/tags/v$(cz version --project).0"
```

Add `--increment PATCH` to `cz bump` to choose a patch release instead.

The workflow tests the plugin and generates GitHub release notes with `cz changelog`. It adds the new version and download details to `manifest.json` without removing older entries. If publishing fails after the tag reaches GitHub, you can run the workflow on that tag without bumping the version again:

```sh
gh workflow run release.yml --ref v0.1.1.0
```

## Try the plugin in Docker

Run this from the repository root. Docker Compose builds the plugin and starts Jellyfin 10.11.5 in a separate container.

```sh
docker compose up --build -d
```

To keep an existing Auto Collections sandbox and its data, run `docker compose -p autocollections-dev up --build -d`. Use `-p autocollections-dev` with any later Compose commands for that sandbox.

Open <http://localhost:8097>. On a fresh sandbox, Docker completes the setup wizard automatically. Sign in as `admin` with the password `magiccollections-test`. If you're reusing a sandbox, keep using your existing login. The default credentials are for local testing only; don't expose this sandbox to the network.

If port 8097 is in use, set `JELLYFIN_TEST_PORT` to a free port before starting Compose.

The container adds a **Magic Collections Test Movies** library for `/test-media` unless a library already uses that path. It contains zero-byte placeholders for The Matrix, Inception, and Tenet. They can be scanned and matched, but not played. Jellyfin scans them automatically on a fresh sandbox. If you reuse an existing sandbox and the titles don't appear, select **Scan All Libraries** on the dashboard.

1. Choose a public MDBList containing one of the test movies and check that the scanned item has a matching provider ID.
2. Open **Dashboard → Plugins → Magic Collections**. Save the full list page URL and reopen the page to confirm it was saved.
3. Open **Dashboard → Scheduled Tasks → Sync Magic Collections**. Check that it has a daily trigger, then select **Run now**.
4. Open **Collections** in Jellyfin. Check for the matching item and the black poster with the collection name in white.
5. Set a custom poster for the collection. Run the task again and confirm it keeps your poster.

After editing the plugin, rerun `docker compose up --build -d` to install the rebuilt plugin.

- Run `docker compose down` to stop Jellyfin and keep your settings.
- Run `docker compose down -v` to stop Jellyfin and delete the sandbox's configuration and cache.

## Add a new Site

Implement `ISourceListHandler` to recognize a site's URLs and return a `SourceList` with an optional name and entries containing a media type and provider IDs. Register the implementation for `ISourceListHandler` in `ServiceRegistrator`. The handler only reads the source list.

`CollectionSync` matches movies and series by media type and at least one shared provider name and ID. It still finds existing collections by their `AutoCollections:<absolute source URL>` tags. It adds only missing members.

A new collection uses the list name, or the final URL segment with spaces in place of hyphens and underscores. `JellyfinCollectionLibrary` handles Jellyfin queries and writes.

The plugin and settings page are adapted from the [official Jellyfin plugin template](https://github.com/jellyfin/jellyfin-plugin-template/tree/c93225a0a5a76d3843db05b4b5b77fcfc482fba3).

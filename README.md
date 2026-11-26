# Jellyfin Magic Collections Plugin

<img width="829" height="412" alt="image" src="https://github.com/user-attachments/assets/ea0bad46-a704-471d-b4bc-a9a8e2cda694" />

WIP attempt to take the lessons learned from [Jellyfin-Auto-Collections](https://github.com/ghomasHudson/Jellyfin-Auto-Collections) and turn it into a native jellyfin plugin.

## Install from the Jellyfin catalog

In Jellyfin, open **Dashboard → Plugins → Repositories** and add this repository URL:

```text
https://raw.githubusercontent.com/ghomasHudson/Jellyfin-Magic-Collections-Plugin/main/manifest.json
```

Magic Collections will appear in the plugin catalog after the first compatible release. Install it from there and restart Jellyfin. To try it before the first release, build it locally or use Docker.

## Build and install manually

The plugin targets Jellyfin Server **10.11.5**. Install the **.NET 9 SDK** (9.0.200 or a newer 9.0 SDK), then run this command from the repository root:

```sh
dotnet test MagicCollections.slnx -c Release
```

This builds the plugin and runs the tests. Stop Jellyfin, create a `MagicCollections` directory under the server’s `plugins` directory, and copy `Jellyfin.Plugin.MagicCollections/bin/Release/net9.0/Jellyfin.Plugin.MagicCollections.dll` into it. Start Jellyfin again.

If upgrading from Auto Collections, remove `plugins/AutoCollections/Jellyfin.Plugin.AutoCollections.dll` before restarting Jellyfin. Leave the configuration file in place. Your saved list URLs, sync schedule, and managed collections carry over.

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

Open <http://localhost:8097> and complete the Jellyfin setup wizard. If port 8097 is in use, set `JELLYFIN_TEST_PORT` to a free port before starting Compose.

1. Bind-mount a test media directory into the container and add it as a Jellyfin library. Include a movie or series from the public MDBList you want to try, and check that the Jellyfin item has a matching provider ID (TMDb, IMDb, or TVDb).
2. Open **Dashboard → Plugins → Magic Collections**. Save the full list page URL and reopen the page to confirm it was saved.
3. Open **Dashboard → Scheduled Tasks → Sync Magic Collections**. Check that it has a daily trigger, then select **Run now**.
4. Open **Collections** in Jellyfin and check that the managed collection contains your matching item.

After editing the plugin, rerun `docker compose up --build -d` to install the rebuilt DLL.

- Run `docker compose down` to stop Jellyfin and keep your settings.
- Run `docker compose down -v` to stop Jellyfin and delete the sandbox's configuration and cache.

## Configure and run

1. Open **Dashboard → Plugins → Magic Collections**. Paste a full public MDBList source list URL into the blank field. To add more URLs, use the next blank field that appears. Select **Save**. For example:
   - <https://mdblist.com/lists/garycrawfordgc/top-movies>
   - <https://mdblist.com/lists/official/movies/popular>

   Use the URL from your browser, not a `/json` export URL. No MDBList account or API key is needed.

   MDBList’s interactive JustWatch charts for movies and shows (`justwatch-streaming-charts`) are unsupported because their JSON export ignores the chart filters. The regular `streaming-charts` source list is supported.
2. Open **Dashboard → Scheduled Tasks → Sync Magic Collections**. The default trigger is daily at 03:00 server time. Select **Run now** to run the same task immediately.
3. If the task fails, check the task result or Jellyfin server log.

A failed fetch or invalid list response stops the run and reports the source list URL without changing its managed collection. Unsupported URLs are logged and skipped. A valid empty source list is not a fetch error.

## Add a handler

Implement `ISourceListHandler` to recognize a site's URLs and return a `SourceList` with an optional name and entries containing a media type and provider IDs. Register the implementation for `ISourceListHandler` in `ServiceRegistrator`. The handler only reads the source list.

`CollectionSync` matches movies and series by media type and at least one shared provider name and ID. It still finds existing collections by their `AutoCollections:<absolute source URL>` tags. It adds only missing members.

A new collection uses the list name, or the final URL segment with spaces in place of hyphens and underscores. `JellyfinCollectionLibrary` handles Jellyfin queries and writes.

The plugin and settings page are adapted from the [official Jellyfin plugin template](https://github.com/jellyfin/jellyfin-plugin-template/tree/c93225a0a5a76d3843db05b4b5b77fcfc482fba3).

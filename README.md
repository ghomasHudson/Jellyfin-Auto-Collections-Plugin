# Jellyfin Auto Collections Plugin

<img width="872" height="485" alt="image" src="https://github.com/user-attachments/assets/12843847-4166-4a5a-82ca-07839e264ac8" />

WIP attempt to take the lessons learned from [Jellyfin-Auto-Collections](https://github.com/ghomasHudson/Jellyfin-Auto-Collections) and turn it into a native jellyfin plugin.

Will publish it as a proper plugin when it's further along.


## Build and install

The plugin targets Jellyfin Server **10.11.5**. Install the **.NET 9 SDK** (tested with 9.0.321), then run this command from the repository root:

```sh
dotnet test AutoCollections.slnx -c Release
```

This builds the plugin and runs the tests. Stop Jellyfin, create an `AutoCollections` directory under the server’s `plugins` directory, and copy `Jellyfin.Plugin.AutoCollections/bin/Release/net9.0/Jellyfin.Plugin.AutoCollections.dll` into it. Start Jellyfin again.

## Try the plugin in Docker

Run this from the repository root. Docker Compose builds the plugin and starts Jellyfin 10.11.5 in a separate container.

```sh
docker compose up --build -d
```

Open <http://localhost:8097> and complete the Jellyfin setup wizard. If port 8097 is in use, set `JELLYFIN_TEST_PORT` to a free port before starting Compose.

1. Bind-mount a test media directory into the container and add it as a Jellyfin library. Include a movie or series from the public MDBList you want to try, and check that the Jellyfin item has a matching provider ID (TMDb, IMDb, or TVDb).
2. Open **Dashboard → Plugins → Auto Collections**. Save the full list page URL and reopen the page to confirm it was saved.
3. Open **Dashboard → Scheduled Tasks → Sync Auto Collections**. Check that it has a daily trigger, then select **Run now**.
4. Open **Collections** in Jellyfin and check that the managed collection contains your matching item.

After editing the plugin, rerun `docker compose up --build -d` to install the rebuilt DLL.

- Run `docker compose down` to stop Jellyfin and keep your settings.
- Run `docker compose down -v` to stop Jellyfin and delete the sandbox's configuration and cache.

## Configure and run

1. Open **Dashboard → Plugins → Auto Collections**. Paste a full public MDBList source list URL into the blank field. To add more URLs, use the next blank field that appears. Select **Save**. For example:
   - <https://mdblist.com/lists/garycrawfordgc/top-movies>
   - <https://mdblist.com/lists/official/movies/popular>

   Use the URL from your browser, not a `/json` export URL. No MDBList account or API key is needed.

   MDBList’s interactive JustWatch charts for movies and shows (`justwatch-streaming-charts`) are unsupported because their JSON export ignores the chart filters. The regular `streaming-charts` source list is supported.
2. Open **Dashboard → Scheduled Tasks → Sync Auto Collections**. The default trigger is daily at 03:00 server time. Select **Run now** to run the same task immediately.
3. If the task fails, check the task result or Jellyfin server log.

A failed fetch or invalid list response stops the run and reports the source list URL without changing its managed collection. Unsupported URLs are logged and skipped. A valid empty source list is not a fetch error.

## Add a handler

Implement `ISourceListHandler` to recognize a site's URLs and return a `SourceList` with an optional name and entries containing a media type and provider IDs. Register the implementation for `ISourceListHandler` in `ServiceRegistrator`. The handler only reads the source list.

`CollectionSync` matches movies and series by media type and at least one shared provider name and ID. It finds the collection by its `AutoCollections:<absolute source URL>` tag and adds only missing members. A new collection uses the list name, or the final URL segment with spaces in place of hyphens and underscores. `JellyfinCollectionLibrary` handles Jellyfin queries and writes.

The plugin and settings page are adapted from the [official Jellyfin plugin template](https://github.com/jellyfin/jellyfin-plugin-template/tree/c93225a0a5a76d3843db05b4b5b77fcfc482fba3).

# Jellyfin Auto Collections

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

Open <http://localhost:8097> and complete the Jellyfin setup wizard. You can skip adding a media library. If port 8097 is in use, set `JELLYFIN_TEST_PORT` to a free port before starting Compose.

1. Open **Dashboard → Plugins → Auto Collections**. Save `https://example.com/test-list` and reopen the page to check that the URL is still there.
2. Open **Dashboard → Scheduled Tasks → Sync Auto Collections** and run the task.
3. Run `docker compose logs jellyfin` and look for `Unsupported source list URL: https://example.com/test-list`. This confirms the task reads the saved URL. No collection is created because this version has no live source list handler.

After editing the plugin, rerun `docker compose up --build -d` to install the rebuilt DLL.

- Run `docker compose down` to stop Jellyfin and keep your settings.
- Run `docker compose down -v` to stop Jellyfin and delete the sandbox's configuration and cache.

## Configure and run

1. Open **Dashboard → Plugins → Auto Collections**. Enter one source list URL per line and select **Save**. Reopen the page to see the saved list.
2. Open **Dashboard → Scheduled Tasks → Sync Auto Collections**. The default trigger is daily at 03:00 server time. Select **Run now** to run the same task immediately.
3. Check the Jellyfin server log for unsupported URLs. The task skips each unsupported URL and continues with the next one.

## Add a handler

Implement `ISourceListHandler` to recognize a site's URLs and return a `SourceList` with an optional name and entries containing a media type and provider IDs. Register the implementation for `ISourceListHandler` in `ServiceRegistrator`. The handler only reads the source list.

`CollectionSync` matches movies and series by media type and at least one shared provider name and ID. It finds the collection by its `AutoCollections:<absolute source URL>` tag and adds only missing members. A new collection uses the list name, or the final URL segment with spaces in place of hyphens and underscores. `JellyfinCollectionLibrary` handles Jellyfin queries and writes.

The plugin and settings page are adapted from the [official Jellyfin plugin template](https://github.com/jellyfin/jellyfin-plugin-template/tree/c93225a0a5a76d3843db05b4b5b77fcfc482fba3).
